using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Data;

public record Course(string Subject, string CourseNumber, string Title, string? RequirementDesignation);

public record Section(
    string Term,
    string Subject,
    string CourseNumber,
    string SectionNumber,
    string Title,
    string? Component,
    string? Type,
    int? Units,
    string? Location,
    string? Times,
    int? SeatsAvailable,
    string? SeatsUpdated,
    double? GpaAvg,
    IReadOnlyList<string> Instructors);

public record Instructor(string Name, int SectionCount);

/// <summary>Read side of the database. The ingest projects own the writes.</summary>
public class CourseQueries(string connectionString)
{
    private SqliteConnection Open()
    {
        var database = new SqliteConnection(connectionString);
        database.Open();
        return database;
    }

    public IReadOnlyList<string> GetTerms()
    {
        using var database = Open();
        return database.Query<string>(
            "SELECT DISTINCT term FROM sections ORDER BY term").ToList();
    }

    /// <summary>Courses in a term, optionally filtered by text and by requirement designation.</summary>
    public IReadOnlyList<Course> SearchCourses(string term, string? query, string? requirement)
    {
        const string sql = @"
            SELECT DISTINCT c.subject AS Subject, c.course_number AS CourseNumber, c.title AS Title,
                   c.requirement_designation AS RequirementDesignation
            FROM sections s
            JOIN courses c ON c.subject = s.subject AND c.course_number = s.course_number
            WHERE s.term = @Term
              AND (@Query = '' OR c.subject LIKE @Like OR c.course_number LIKE @Like OR c.title LIKE @Like)
              AND (@Requirement = '' OR c.requirement_designation LIKE @RequirementLike)
            ORDER BY c.subject, c.course_number
            LIMIT 200;";

        query ??= "";
        requirement ??= "";
        using var database = Open();
        return database.Query<Course>(sql, new
        {
            Term = term,
            Query = query,
            Like = $"%{query}%",
            Requirement = requirement,
            RequirementLike = $"%{requirement}%"
        }).ToList();
    }

    /// <summary>Every section of one course in one term.</summary>
    public IReadOnlyList<Section> GetSections(string term, string subject, string courseNumber) =>
        FindSections(term, subject: subject, courseNumber: courseNumber);

    /// <summary>
    /// Sections in a term narrowed by any combination of text, requirement designation,
    /// open seats and start time. Time filtering happens in memory because the stored
    /// times are display strings, sometimes several patterns per section.
    /// </summary>
    public IReadOnlyList<Section> FindSections(
        string term,
        string? subject = null,
        string? courseNumber = null,
        string? query = null,
        string? requirement = null,
        bool openOnly = false,
        string? startAfter = null,
        string? startBefore = null)
    {
        const string sql = @"
            SELECT s.term, s.subject, s.course_number, s.section_number, c.title,
                   s.component, s.type, s.units, s.location, s.times,
                   s.seats_available, s.seats_updated, s.gpa_avg
            FROM sections s
            JOIN courses c ON c.subject = s.subject AND c.course_number = s.course_number
            WHERE s.term = @Term
              AND (@Subject = '' OR s.subject = @Subject)
              AND (@CourseNumber = '' OR s.course_number = @CourseNumber)
              AND (@Query = '' OR s.subject LIKE @Like OR s.course_number LIKE @Like OR c.title LIKE @Like)
              AND (@Requirement = '' OR c.requirement_designation LIKE @RequirementLike)
              AND (@OpenOnly = 0 OR s.seats_available > 0)
            ORDER BY s.subject, s.course_number, s.section_number
            LIMIT 500;";

        subject ??= ""; courseNumber ??= ""; query ??= ""; requirement ??= "";
        using var database = Open();

        var rows = database.Query(sql, new
        {
            Term = term,
            Subject = subject,
            CourseNumber = courseNumber,
            Query = query,
            Like = $"%{query}%",
            Requirement = requirement,
            RequirementLike = $"%{requirement}%",
            OpenOnly = openOnly ? 1 : 0
        }).ToList();

        var instructors = InstructorsFor(database, term, subject, courseNumber);

        var sections = rows.Select(row => new Section(
            (string)row.term,
            (string)row.subject,
            (string)row.course_number,
            (string)row.section_number,
            (string)row.title,
            (string?)row.component,
            (string?)row.type,
            (int?)(long?)row.units,
            (string?)row.location,
            (string?)row.times,
            (int?)(long?)row.seats_available,
            (string?)row.seats_updated,
            (double?)row.gpa_avg,
            instructors.TryGetValue(SectionKey((string)row.subject, (string)row.course_number, (string)row.section_number), out var names)
                ? names : Array.Empty<string>()
        ));

        var after = ToMinutes(startAfter);
        var before = ToMinutes(startBefore);
        if (after is not null || before is not null)
            sections = sections.Where(section =>
            {
                var start = EarliestStart(section.Times);
                if (start is null) return false;   // no meeting time cannot satisfy a time filter
                if (after is not null && start < after) return false;
                if (before is not null && start > before) return false;
                return true;
            });

        return sections.ToList();
    }

    /// <summary>Instructors, ranked by how many sections they have.</summary>
    public IReadOnlyList<Instructor> SearchInstructors(string? query, string? term)
    {
        const string sql = @"
            SELECT instructor AS Name, COUNT(*) AS SectionCount
            FROM section_instructors
            WHERE (@Query = '' OR instructor LIKE @Like)
              AND (@Term = '' OR term = @Term)
            GROUP BY instructor
            ORDER BY SectionCount DESC, instructor
            LIMIT 100;";

        query ??= ""; term ??= "";
        using var database = Open();

        // COUNT(*) comes back as Int64, which will not bind to an int constructor.
        return database.Query(sql, new { Query = query, Like = $"%{query}%", Term = term })
            .Select(row => new Instructor((string)row.Name, (int)(long)row.SectionCount))
            .ToList();
    }

    /// <summary>Everything one instructor is teaching in a term.</summary>
    public IReadOnlyList<Section> GetInstructorSections(string term, string instructor)
    {
        const string sql = @"
            SELECT s.term, s.subject, s.course_number, s.section_number, c.title,
                   s.component, s.type, s.units, s.location, s.times,
                   s.seats_available, s.seats_updated, s.gpa_avg
            FROM section_instructors i
            JOIN sections s ON s.term = i.term AND s.subject = i.subject
                           AND s.course_number = i.course_number AND s.section_number = i.section_number
            JOIN courses c ON c.subject = s.subject AND c.course_number = s.course_number
            WHERE i.term = @Term AND i.instructor = @Instructor
            ORDER BY s.subject, s.course_number, s.section_number;";

        using var database = Open();
        return database.Query(sql, new { Term = term, Instructor = instructor }).Select(row => new Section(
            (string)row.term, (string)row.subject, (string)row.course_number, (string)row.section_number,
            (string)row.title, (string?)row.component, (string?)row.type, (int?)(long?)row.units,
            (string?)row.location, (string?)row.times, (int?)(long?)row.seats_available,
            (string?)row.seats_updated, (double?)row.gpa_avg, new[] { instructor })).ToList();
    }

    private static string SectionKey(string subject, string courseNumber, string sectionNumber) =>
        $"{subject}|{courseNumber}|{sectionNumber}";

    private static Dictionary<string, IReadOnlyList<string>> InstructorsFor(
        SqliteConnection database, string term, string subject, string courseNumber)
    {
        const string sql = @"
            SELECT subject, course_number, section_number, instructor
            FROM section_instructors
            WHERE term = @Term
              AND (@Subject = '' OR subject = @Subject)
              AND (@CourseNumber = '' OR course_number = @CourseNumber);";

        return database.Query(sql, new { Term = term, Subject = subject, CourseNumber = courseNumber })
            .GroupBy(row => SectionKey((string)row.subject, (string)row.course_number, (string)row.section_number))
            .ToDictionary(group => group.Key,
                          group => (IReadOnlyList<string>)group.Select(row => (string)row.instructor).ToList());
    }

    private static readonly Regex TimePattern =
        new Regex(@"(\d{1,2}):(\d{2})(AM|PM)-(\d{1,2}):(\d{2})(AM|PM)", RegexOptions.Compiled);

    /// <summary>A clock time to minutes past midnight, or null.</summary>
    internal static int? ToMinutes(string? clockTime)
    {
        if (string.IsNullOrWhiteSpace(clockTime)) return null;
        var match = Regex.Match(clockTime.Trim(), @"^(\d{1,2}):(\d{2})\s*(AM|PM)?$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var hour = int.Parse(match.Groups[1].Value);
        var minute = int.Parse(match.Groups[2].Value);
        var meridiem = match.Groups[3].Value.ToUpperInvariant();

        if (meridiem == "PM" && hour != 12) hour += 12;
        if (meridiem == "AM" && hour == 12) hour = 0;
        return hour * 60 + minute;
    }

    /// <summary>
    /// The earliest start across every meeting pattern in a times string. Sections
    /// can meet several times, separated by semicolons.
    /// </summary>
    internal static int? EarliestStart(string? times)
    {
        if (string.IsNullOrWhiteSpace(times)) return null;

        int? earliest = null;
        foreach (Match match in TimePattern.Matches(times))
        {
            var hour = int.Parse(match.Groups[1].Value);
            var minute = int.Parse(match.Groups[2].Value);
            if (match.Groups[3].Value == "PM" && hour != 12) hour += 12;
            if (match.Groups[3].Value == "AM" && hour == 12) hour = 0;

            var start = hour * 60 + minute;
            if (earliest is null || start < earliest) earliest = start;
        }
        return earliest;
    }
}
