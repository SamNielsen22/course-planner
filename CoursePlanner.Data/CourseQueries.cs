using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Data;

public record Course(string Subject, string CourseNumber, string Title,
                     string? RequirementDesignation, double? GpaAvg);

public record Section(
    string Term,
    string Subject,
    string CourseNumber,
    string SectionNumber,
    string Campus,
    string Title,
    string? Component,
    string? Type,
    int? Units,
    string? Location,
    string? Times,
    int? SeatsAvailable,
    string? SeatsUpdated,
    double? GpaAvg,
    IReadOnlyList<SectionInstructor> Instructors,
    // Enrollment figures from the registrar's sections table; null until read.
    int? Waitlist = null,
    bool? HasWaitlist = null,
    int? EnrollmentCap = null,
    int? Enrolled = null,
    string? ClassNumber = null,
    // On a lab: the lecture section it registers you into, "*" for any, null
    // when the registrar published no pairing. Always null on a lecture.
    string? PairsWith = null);

// Unid is the identity. Names are not unique.
public record Instructor(string Unid, string Name, int SectionCount);

/// <summary>An instructor on a section. Links use the uNID, never the name.</summary>
public record SectionInstructor(string Unid, string Name);

/// <summary>Reads the catalogue database. Only the ingest projects write to it.</summary>
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

    /// <summary>"calculus 1" as "calculus I", since titles use Roman numerals. Null when the query ends in no small number.</summary>
    public static string? RomanTitle(string query)
    {
        var at = query.LastIndexOf(' ');
        if (at < 0 || !int.TryParse(query[(at + 1)..], out var n) || n is < 1 or > 10) return null;
        string[] roman = ["I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X"];
        return query[..at] + " " + roman[n - 1];
    }

    /// <summary>Courses matching text, across every term, with the average pooled over all their grades.</summary>
    public IReadOnlyList<Course> SearchAllCourses(string? query)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return [];
        var parts = query.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var head = parts.Length == 2 ? parts[0] : "";
        var tail = parts.Length == 2 ? parts[1] : "";

        using var database = Open();
        // An exact subject code returns that subject only; other text is ranked code matches first.
        return database.Query(@"
            SELECT c.subject AS Subject, c.course_number AS CourseNumber, c.title AS Title,
                   c.requirement_designation AS RequirementDesignation,
                   CAST(g.gpa AS REAL) AS GpaAvg
            FROM courses c
            LEFT JOIN (SELECT subject, course_number, SUM(gpa_avg * n) / SUM(n) AS gpa
                       FROM (SELECT subject, course_number, gpa_avg,
                                    COALESCE(grade_a,0)+COALESCE(grade_b,0)+COALESCE(grade_c,0)
                                    +COALESCE(grade_d,0)+COALESCE(grade_e,0) AS n
                             FROM course_term_grades WHERE gpa_avg IS NOT NULL)
                       WHERE n > 0 GROUP BY subject, course_number) g
              ON g.subject = c.subject AND g.course_number = c.course_number
            WHERE CASE WHEN EXISTS (SELECT 1 FROM courses WHERE subject = @Upper)
                       THEN c.subject = @Upper
                       ELSE c.subject LIKE @Like
                         OR c.course_number LIKE @Like
                         OR c.title LIKE @Like
                         OR c.title LIKE @RomanLike
                         OR (c.subject || ' ' || c.course_number) LIKE @Like
                         OR (@Head <> '' AND c.subject LIKE @HeadLike AND c.course_number LIKE @TailLike)
                  END
              -- Three-digit courses that never carried credit are non-credit shadows; skip them,
              -- unless another course lists them as a prerequisite (e.g. MATH 980).
              AND NOT (LENGTH(c.course_number) = 3
                       AND NOT EXISTS (SELECT 1 FROM sections s
                                       WHERE s.subject = c.subject AND s.course_number = c.course_number
                                         AND s.units > 0)
                       AND (c.subject || '|' || c.course_number) NOT IN @Prereqs)
            ORDER BY CASE WHEN c.subject LIKE @StartsLike THEN 0
                          WHEN (c.subject || ' ' || c.course_number) LIKE @StartsLike THEN 1
                          WHEN c.course_number LIKE @StartsLike THEN 2
                          ELSE 3 END,
                     c.subject, c.course_number;",
            new { Upper = query.ToUpperInvariant(), Like = $"%{query}%", RomanLike = $"%{RomanTitle(query) ?? query}%",
                  StartsLike = $"{query}%", Head = head, HeadLike = $"{head}%", TailLike = $"{tail}%",
                  Prereqs = PrereqCourses })
            .Select(r => new Course((string)r.Subject, (string)r.CourseNumber, (string)r.Title,
                                    (string?)r.RequirementDesignation,
                                    r.GpaAvg is double gpa ? gpa : null))
            .ToList();
    }

    private static string SectionKey(string subject, string courseNumber, string sectionNumber) =>
        $"{subject}|{courseNumber}|{sectionNumber}";

    private static Dictionary<string, IReadOnlyList<SectionInstructor>> InstructorsFor(
        SqliteConnection database, string term, string subject, string courseNumber)
    {
        const string sql = @"
            SELECT i.subject, i.course_number, i.section_number,
                   i.instructor_unid AS unid, p.display_name AS instructor
            FROM section_instructors i
            JOIN instructors p ON p.unid = i.instructor_unid
            WHERE i.term = @Term
              AND (@Subject = '' OR i.subject = @Subject)
              AND (@CourseNumber = '' OR i.course_number = @CourseNumber);";

        return database.Query(sql, new { Term = term, Subject = subject, CourseNumber = courseNumber })
            .GroupBy(row => SectionKey((string)row.subject, (string)row.course_number, (string)row.section_number))
            .ToDictionary(group => group.Key,
                          group => (IReadOnlyList<SectionInstructor>)group
                              .Select(row => new SectionInstructor((string)row.unid, (string)row.instructor))
                              .ToList());
    }

    private static readonly Regex TimePattern =
        new Regex(@"(\d{1,2}):(\d{2})(AM|PM)-(\d{1,2}):(\d{2})(AM|PM)", RegexOptions.Compiled);

    /// <summary>"9:10AM" as minutes past midnight, or null.</summary>
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

    /// <summary>The earliest start in a times string, which may hold several patterns.</summary>
    public static int? EarliestStart(string? times)
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

    // ------------------------------------------------------ grades
    /// <summary>Every term with published grades, in storage order.</summary>
    public IReadOnlyList<string> GradeTerms()
    {
        using var database = Open();
        return database.Query<string>("SELECT DISTINCT term FROM section_grades;").ToList();
    }

    /// <summary>Sections in a term, filtered by text, requirement, open seats and start time. Times are filtered in memory.</summary>
    public IReadOnlyList<Section> FindSections(
        string term,
        string? subject = null,
        string? courseNumber = null,
        string? query = null,
        string? requirement = null,
        bool openOnly = false,
        bool creditOnly = true,
        string? startAfter = null,
        string? startBefore = null,
        string? campus = null)
    {
        const string sql = @"
            SELECT s.term, s.subject, s.course_number, s.section_number, s.campus, c.title,
                   s.component, s.type, s.units, s.location, s.times,
                   s.seats_available, s.seats_updated, CAST(g.gpa_avg AS REAL) AS gpa_avg,
                   s.waitlist, s.has_waitlist, s.enrollment_cap, s.enrolled, s.class_number, s.pairs_with
            FROM sections s
            JOIN courses c ON c.subject = s.subject AND c.course_number = s.course_number
            -- LEFT JOIN: a section without grades still lists.
            LEFT JOIN section_grades g ON g.term = s.term AND g.subject = s.subject
                              AND g.course_number = s.course_number
                              AND g.section_number = s.section_number
            WHERE s.term = @Term
              AND (@Campus = '' OR s.campus = @Campus)
              AND (@Subject = '' OR s.subject = @Subject)
              AND (@CourseNumber = '' OR s.course_number = @CourseNumber)
              -- Same text rules as SearchAllCourses.
              AND (@Query = ''
                   OR s.subject LIKE @Like
                   OR s.course_number LIKE @Like
                   OR c.title LIKE @Like
                   OR c.title LIKE @RomanLike
                   OR (s.subject || ' ' || s.course_number) LIKE @Like
                   OR (@Head <> '' AND s.subject LIKE @HeadLike
                       AND s.course_number LIKE @TailLike))
              AND (@Requirement = '' OR c.requirement_designation LIKE @RequirementLike)
              AND (@OpenOnly = 0 OR s.seats_available > 0)
              -- Continuing Education is three digits AND zero credit; either test alone hides real classes.
              AND (@CreditOnly = 0
                   OR NOT (s.units = 0 AND LENGTH(s.course_number) = 3))
            ORDER BY s.subject, s.course_number, s.section_number;";

        subject ??= ""; courseNumber ??= ""; requirement ??= "";
        query = (query ?? "").Trim();

        // "CS 2420" is read as subject then number.
        var parts = query.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var head = parts.Length == 2 ? parts[0] : "";
        var tail = parts.Length == 2 ? parts[1] : "";

        using var database = Open();

        var rows = database.Query(sql, new
        {
            Term = term,
            Subject = subject,
            CourseNumber = courseNumber,
            Query = query,
            Like = $"%{query}%",
            RomanLike = $"%{RomanTitle(query) ?? query}%",
            Head = head,
            HeadLike = $"{head}%",
            TailLike = $"{tail}%",
            Requirement = requirement,
            RequirementLike = $"%{requirement}%",
            OpenOnly = openOnly ? 1 : 0,
            CreditOnly = creditOnly ? 1 : 0,
            Campus = campus ?? ""
        }).ToList();

        var instructors = InstructorsFor(database, term, subject, courseNumber);

        var sections = rows.Select(row => new Section(
            (string)row.term,
            (string)row.subject,
            (string)row.course_number,
            (string)row.section_number,
            (string)row.campus,
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
                ? names : Array.Empty<SectionInstructor>(),
            (int?)(long?)row.waitlist,
            (long?)row.has_waitlist is { } waitable ? waitable != 0 : null,
            (int?)(long?)row.enrollment_cap,
            (int?)(long?)row.enrolled,
            (string?)row.class_number,
            (string?)row.pairs_with
        ));

        var after = ToMinutes(startAfter);
        var before = ToMinutes(startBefore);
        if (after is not null || before is not null)
            sections = sections.Where(section =>
            {
                var start = EarliestStart(section.Times);
                if (start is null) return false;   // no meeting time, no match
                if (after is not null && start < after) return false;
                if (before is not null && start > before) return false;
                return true;
            });

        return sections.ToList();
    }

    /// <summary>One published grade row. A count is null where the registrar blanked it (under five students).</summary>
    public sealed record GradeRow(
        string Term, string Subject, string CourseNumber, string? SectionNumber,
        double Avg, double? P25, double? P50, double? P75, double? Sd,
        int? A, int? B, int? C, int? D, int? E, int Cr, int Nc, int W, int Other);

    /// <summary>Every graded row of one table, for <see cref="GradeIndex"/>. Rows without an average are left out.</summary>
    public IReadOnlyList<GradeRow> GradeRows(string table)
    {
        var (termColumn, sectionColumn) = table switch
        {
            "section_grades" => ("term", "section_number"),
            "course_term_grades" => ("term", "NULL"),
            "course_grades" => ("''", "NULL"),
            _ => throw new ArgumentException("not a grade grain", nameof(table)),
        };
        using var database = Open();
        return database.Query($@"
            SELECT {termColumn} AS Term, subject AS Subject, course_number AS CourseNumber,
                   {sectionColumn} AS SectionNumber,
                   gpa_avg AS Avg, gpa_p25 AS P25, gpa_p50 AS P50, gpa_p75 AS P75, gpa_std_dev AS Sd,
                   grade_a AS A, grade_b AS B, grade_c AS C, grade_d AS D, grade_e AS E,
                   COALESCE(grade_cr,0) AS Cr, COALESCE(grade_nc,0) AS Nc,
                   COALESCE(grade_w,0) AS W, COALESCE(grade_other,0) AS Other
            FROM {table} WHERE gpa_avg IS NOT NULL;")
            .Select(r => new GradeRow(
                (string)r.Term, (string)r.Subject, (string)r.CourseNumber, (string?)r.SectionNumber,
                (double)r.Avg, (double?)r.P25, (double?)r.P50, (double?)r.P75, (double?)r.Sd,
                Count(r.A), Count(r.B), Count(r.C), Count(r.D), Count(r.E),
                (int)(long)r.Cr, (int)(long)r.Nc, (int)(long)r.W, (int)(long)r.Other))
            .ToList();
    }

    private static int? Count(dynamic value) => value is null ? null : (int)(long)value;

    public sealed record SectionInstructorLink(string Term, string Subject, string CourseNumber, string SectionNumber, string Unid);

    /// <summary>Who taught which section.</summary>
    public IReadOnlyList<SectionInstructorLink> SectionInstructorLinks()
    {
        using var database = Open();
        return database.Query(@"
            SELECT term AS Term, subject AS Subject, course_number AS CourseNumber,
                   section_number AS SectionNumber, instructor_unid AS Unid
            FROM section_instructors;")
            .Select(r => new SectionInstructorLink((string)r.Term, (string)r.Subject, (string)r.CourseNumber,
                                                   (string)r.SectionNumber, (string)r.Unid))
            .ToList();
    }

    /// <summary>Terms in which this course has published grades, newest first.</summary>
    public IReadOnlyList<string> CourseTerms(string subject, string courseNumber)
    {
        using var database = Open();
        return database.Query<string>(@"
            SELECT DISTINCT term FROM course_term_grades
            WHERE subject = @Subject AND course_number = @Number
            ORDER BY term;", new { Subject = subject, Number = courseNumber }).ToList();
    }

    /// <summary>The builder's cards for a page of sections. Averages come from <see cref="GradeIndex"/> through the lookups passed in.</summary>
    public IReadOnlyList<SectionCard> Cards(
        IReadOnlyList<Section> sections,
        Func<string, string, double?> courseAverage,
        Func<string, string, string, double?> instructorCourseAverage)
    {
        if (sections.Count == 0) return [];

        var codes = sections.Select(s => s.Subject + "|" + s.CourseNumber).Distinct().ToList();

        using var database = Open();

        // Prerequisites are per course, so read once per course.
        var prereqs = database.Query(@"
            SELECT subject AS Subject, course_number AS Number, prerequisites AS Text
            FROM courses
            WHERE subject || '|' || course_number IN @Codes
              AND prerequisites IS NOT NULL AND TRIM(prerequisites) <> '';",
            new { Codes = codes })
            .ToDictionary(r => (string)r.Subject + "|" + (string)r.Number, r => (string)r.Text);

        // Every section of each course on this page, not just the ones shown. A
        // page is one slice of one filter, so a lecture's companion list would
        // otherwise grow and shrink with the page number.
        var courseSections = database.Query(@"
            SELECT term AS Term, campus AS Campus, subject AS Subject, course_number AS Number,
                   section_number AS SectionNumber, component AS Component, pairs_with AS PairsWith
            FROM sections
            WHERE term IN @Terms AND subject || '|' || course_number IN @Codes;",
            new { Terms = sections.Select(s => s.Term).Distinct().ToList(), Codes = codes })
            .GroupBy(r => ((string)r.Term, (string)r.Campus, (string)r.Subject, (string)r.Number))
            .ToDictionary(g => g.Key, g => g.Select(r =>
                new CourseSection((string)r.SectionNumber, (string?)r.Component, (string?)r.PairsWith)).ToList());
        var whole = courseSections.ToDictionary(kv => kv.Key, kv => Companions.Resolve(kv.Value));

        return sections.Select(s =>
        {
            var code = s.Subject + "|" + s.CourseNumber;
            // One average per professor, by uNID, so co-taught sections show one figure each.
            var averages = s.Instructors
                .GroupBy(t => t.Unid)
                .ToDictionary(g => g.Key, g => instructorCourseAverage(g.Key, s.Subject, s.CourseNumber));

            string? pairsWith = null;
            IReadOnlyList<string> labs = Array.Empty<string>();
            var requiresCompanion = false;
            string? companionKind = null;
            // A course with no lecture has nothing to pair: its lab is the course.
            var hasLecture = whole.TryGetValue((s.Term, s.Campus, s.Subject, s.CourseNumber), out var course) && course.HasLecture;
            if (hasLecture)
            {
                if (Companions.IsCompanion(s.Component)) pairsWith = course!.PairsWith.GetValueOrDefault(s.SectionNumber);
                else if (s.Component == "Lecture") labs = course!.Companions.GetValueOrDefault(s.SectionNumber, Array.Empty<string>());

                // A lecture requires a companion when one is paired to it. When the
                // course published no pairing at all we cannot tell which lectures
                // are self-contained, so every lecture asks. But once the pairing is
                // published, a lecture with none paired to it (an online or standalone
                // section) needs no lab, so it is left alone.
                var kinds = courseSections[(s.Term, s.Campus, s.Subject, s.CourseNumber)]
                    .Where(x => Companions.IsCompanion(x.Component)).Select(x => x.Component).ToList();
                var anyPublished = course!.PairsWith.Values.Any(v => v is not null);
                if (s.Component == "Lecture" && kinds.Count > 0 && (labs.Count > 0 || !anyPublished))
                {
                    requiresCompanion = true;
                    companionKind = Companions.Kind(kinds);
                }
            }
            return new SectionCard(s.Subject, s.CourseNumber, s.SectionNumber, s.Campus, s.Title,
                                   s.Times, s.Location, s.SeatsAvailable, s.Units, s.Instructors,
                                   courseAverage(s.Subject, s.CourseNumber), averages,
                                   prereqs.GetValueOrDefault(code), s.Waitlist, s.HasWaitlist,
                                   s.Component, pairsWith, labs, hasLecture, requiresCompanion, companionKind);
        }).ToList();
    }

    /// <summary>Every course, in order. Used by the sitemap.</summary>
    public IReadOnlyList<(string Subject, string Number)> AllCourses()
    {
        using var database = Open();
        return database.Query("SELECT subject, course_number FROM courses ORDER BY subject, course_number")
            .Select(r => ((string)r.subject, (string)r.course_number)).ToList();
    }

    /// <summary>One course's title, description and prerequisites.</summary>
    public CourseInfo? Course(string subject, string courseNumber)
    {
        using var database = Open();
        return database.QueryFirstOrDefault<CourseInfo>(@"
            SELECT subject AS Subject, course_number AS CourseNumber, title AS Title,
                   description AS Description, prerequisites AS Prerequisites,
                   requirement_designation AS RequirementDesignation
            FROM courses WHERE subject = @Subject AND course_number = @Number;",
            new { Subject = subject, Number = courseNumber });
    }

    /// <summary>The stored display name for a uNID.</summary>
    public string? InstructorName(string unid)
    {
        using var database = Open();
        return database.QueryFirstOrDefault<string>(
            "SELECT display_name FROM instructors WHERE unid = @Unid", new { Unid = unid });
    }

    /// <summary>Every course title keyed "SUBJ NUMBER", fetched once for the prerequisite markup.</summary>
    public Dictionary<string, string> CourseTitles()
    {
        using var database = Open();
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in database.Query(
            "SELECT subject, course_number, title FROM courses WHERE title IS NOT NULL"))
            titles[$"{row.subject} {row.course_number}"] = (string)row.title;
        return titles;
    }

    /// <summary>The requirement designations in use, for the filter.</summary>
    public IReadOnlyList<string> Designations()
    {
        using var database = Open();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in database.Query<string>(@"
            SELECT requirement_designation FROM courses
            WHERE requirement_designation IS NOT NULL AND TRIM(requirement_designation) <> ''"))
            // One field can hold several designations; the filter offers each one.
            foreach (var part in row.Split([',', '/', '&'], StringSplitOptions.RemoveEmptyEntries))
            {
                var atom = part.Trim();
                if (atom.Length > 2)
                    counts[atom] = counts.GetValueOrDefault(atom) + 1;
            }

        return counts.Where(p => p.Value >= 10)
                     .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(p => p.Key).ToList();
    }

    /// <summary>Row counts and the first and last terms. Terms are ordered by date, not alphabetically.</summary>
    public CoverageCounts Coverage()
    {
        using var db = Open();
        var terms = db.Query<string>(
            "SELECT DISTINCT term FROM section_grades;").ToList();
        terms.Sort((a, b) => TermKey(a).CompareTo(TermKey(b)));

        var schedule = db.Query<string>(
            "SELECT DISTINCT term FROM sections;").ToList();
        schedule.Sort((a, b) => TermKey(a).CompareTo(TermKey(b)));

        return new CoverageCounts(
            db.ExecuteScalar<int>("SELECT COUNT(*) FROM courses;"),
            db.ExecuteScalar<int>("SELECT COUNT(*) FROM sections;"),
            db.ExecuteScalar<int>("SELECT COUNT(*) FROM instructors;"),
            db.ExecuteScalar<int>("SELECT COUNT(*) FROM section_grades;"),
            terms.FirstOrDefault(), terms.LastOrDefault(),
            schedule.LastOrDefault());
    }

    /// <summary>"Fall2026" as (2026, 2), for date order.</summary>
    private static (int Year, int Season) TermKey(string term)
    {
        var year = term.Length >= 4 && int.TryParse(term[^4..], out var y) ? y : 0;
        var season = term.Length > 4
            ? Array.IndexOf(new[] { "Spring", "Summer", "Fall" }, term[..^4])
            : -1;
        return (year, season < 0 ? 3 : season);
    }

    /// <summary>"SUBJ|NUMBER" to the course's requirement designation.</summary>
    public Dictionary<string, string?> RequirementDesignations()
    {
        using var database = Open();
        return database.Query(@"
            SELECT subject AS Subject, course_number AS Number,
                   requirement_designation AS Designation
            FROM courses;")
            .ToDictionary(r => (string)r.Subject + "|" + (string)r.Number,
                          r => (string?)r.Designation);
    }

    // Computed once from the courses table; a search reuses it rather than re-scanning.
    private HashSet<string>? _prereqCourses;

    /// <summary>The prerequisite set, cached for the life of the process.</summary>
    public HashSet<string> PrereqCourses => _prereqCourses ??= PrerequisiteCourses();

    /// <summary>
    /// Every course named in another course's prerequisite text, as "SUBJECT|NUMBER".
    /// Used to keep a prerequisite visible even when it carries no credit. A bare
    /// number inherits the subject before it ("MATH 1050 OR 1060"), as on the course page.
    /// </summary>
    public HashSet<string> PrerequisiteCourses()
    {
        using var database = Open();
        var subjects = database.Query<string>("SELECT DISTINCT subject FROM courses;")
            .OrderByDescending(s => s.Length).Select(Regex.Escape);
        var reference = new Regex(
            @"\b(?<subj>" + string.Join("|", subjects) + @")\s*(?<num>\d{3,4}[A-Z]?)\b|\b(?<bare>\d{3,4}[A-Z]?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in database.Query<string>(
            "SELECT prerequisites FROM courses WHERE prerequisites IS NOT NULL AND TRIM(prerequisites) <> '';"))
        {
            string? carried = null;
            foreach (Match m in reference.Matches(text))
            {
                if (m.Groups["subj"].Success)
                {
                    carried = m.Groups["subj"].Value.ToUpperInvariant();
                    referenced.Add($"{carried}|{m.Groups["num"].Value}");
                }
                else if (carried is not null)   // a bare number inherits the last subject
                    referenced.Add($"{carried}|{m.Groups["bare"].Value}");
            }
        }
        return referenced;
    }

    /// <summary>Every subject code with a section.</summary>
    public IReadOnlyList<string> Departments()
    {
        using var database = Open();
        return database.Query<string>(
            "SELECT DISTINCT subject FROM sections ORDER BY subject;").ToList();
    }

    /// <summary>
    /// Components that count as teaching for the section count. Supervised
    /// work (clinical, practicum, thesis) and TA-run labs are left out.
    /// </summary>
    private const string TeachingComponents =
        "('Lecture','Seminar','Studio','Workshop','Special Topics','Ensemble'," +
        "'Private Instruction','Activity')";

    /// <summary>Every instructor with their section and graded-class counts. Read once at startup by <see cref="InstructorIndex"/>.</summary>
    public IReadOnlyList<InstructorIndex.Entry> InstructorAverages()
    {
        using var database = Open();
        // Mapped by hand: SQLite counts are Int64 and Dapper will not narrow them to int.
        return database.Query(@"
            WITH load AS (
                SELECT i.instructor_unid AS unid, COUNT(*) AS n
                FROM section_instructors i
                JOIN sections s ON s.term = i.term AND s.subject = i.subject
                  AND s.course_number = i.course_number AND s.section_number = i.section_number
                WHERE s.component IN " + TeachingComponents + @"
                GROUP BY i.instructor_unid
            ),
            -- Distinct classes with letter grades, the same set the search card shows.
            graded AS (
                SELECT i.instructor_unid AS unid,
                       COUNT(DISTINCT i.subject || '|' || i.course_number) AS k
                FROM section_instructors i
                JOIN section_grades g ON g.term = i.term AND g.subject = i.subject
                  AND g.course_number = i.course_number AND g.section_number = i.section_number
                WHERE COALESCE(g.grade_a,0)+COALESCE(g.grade_b,0)+COALESCE(g.grade_c,0)
                      +COALESCE(g.grade_d,0)+COALESCE(g.grade_e,0) > 0
                GROUP BY i.instructor_unid
            )
            SELECT p.unid          AS Unid,
                   p.display_name  AS Name,
                   p.department    AS Department,
                   COALESCE(load.n, 0) AS SectionCount,
                   COALESCE(gr.k, 0)   AS GradedCourses,
                   NULL AS AvgGpa   -- InstructorIndex fills this from GradeIndex
            FROM instructors p
            LEFT JOIN load ON load.unid = p.unid
            LEFT JOIN graded gr ON gr.unid = p.unid;")
            .Select(r => new InstructorIndex.Entry(
                (string)r.Unid,
                (string)r.Name,
                (string?)r.Department,
                (int)(long)r.SectionCount,
                (int)(long)r.GradedCourses,
                r.AvgGpa is double gpa ? gpa : null))
            .ToList();
    }

    /// <summary>For each instructor, the courses they have published grades in. The average is filled in by <see cref="InstructorIndex"/>.</summary>
    public Dictionary<string, IReadOnlyList<InstructorCourse>> InstructorCourseAverages(
        IReadOnlyList<string> unids)
    {
        if (unids.Count == 0) return [];
        using var database = Open();
        return database.Query(@"
            SELECT i.instructor_unid AS Unid, i.subject AS Subject,
                   i.course_number AS CourseNumber, c.title AS Title,
                   NULL AS Gpa   -- InstructorIndex fills this from GradeIndex
            FROM section_instructors i
            JOIN courses c ON c.subject = i.subject AND c.course_number = i.course_number
            LEFT JOIN section_grades g ON g.term = i.term AND g.subject = i.subject
              AND g.course_number = i.course_number AND g.section_number = i.section_number
              AND g.gpa_avg IS NOT NULL
            LEFT JOIN (SELECT term, subject, course_number, section_number,
                              COALESCE(grade_a,0)+COALESCE(grade_b,0)+COALESCE(grade_c,0)
                              +COALESCE(grade_d,0)+COALESCE(grade_e,0) AS c
                       FROM section_grades) n
              ON n.term = g.term AND n.subject = g.subject
             AND n.course_number = g.course_number AND n.section_number = g.section_number
             AND n.c > 0
            WHERE i.instructor_unid IN @Unids
            GROUP BY i.instructor_unid, i.subject, i.course_number
            -- Graded classes only, whatever the component.
            HAVING SUM(n.c) > 0;", new { Unids = unids })
            .GroupBy(r => (string)r.Unid)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<InstructorCourse>)g
                .Select(r => new InstructorCourse((string)r.Subject, (string)r.CourseNumber,
                                                  (string)r.Title,
                                                  r.Gpa is double gpa ? gpa : null))
                // Graded classes first, then catalogue order. The card decides how many to show.
                .OrderBy(c => c.AvgGpa is null)
                .ThenBy(c => c.Subject, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.CourseNumber, StringComparer.OrdinalIgnoreCase).ToList());
    }
}

// ------------------------------------------------------------ grade shapes

/// <summary>
/// A grade distribution at any grain. Percentiles and the deviation are the
/// dashboard's own for a single row and null when rows are pooled.
/// </summary>
public record GradeDistribution(
    int A, int B, int C, int D, int E,
    int Cr, int Nc, int W, int Other, double? AvgGpa,
    double? P25 = null, double? P50 = null, double? P75 = null, double? StdDev = null)
{
    public static readonly GradeDistribution Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, null);

    public int Graded => A + B + C + D + E;

    /// <summary>Everyone with a mark. "Other" is lab enrolment already counted through the lecture, so it is left out.</summary>
    public int Total => Graded + Cr + Nc + W;
    public bool Any => Total > 0;

    /// <summary>The bars the chart draws. "Other" is left out for the same reason as in <see cref="Total"/>.</summary>
    public IReadOnlyList<(string Label, int Count)> Bars =>
        [("A", A), ("B", B), ("C", C), ("D", D), ("E", E),
         ("CR", Cr), ("NC", Nc), ("W", W)];

    /// <summary>The average the bars alone imply. Its distance from the published one shows how much is hidden.</summary>
    public double? ImpliedAvg =>
        Graded == 0 ? null : (A * 4.0 + B * 3.0 + C * 2.0 + D * 1.0) / Graded;

    /// <summary>Whether the bars are well above the published average. Coarse buckets add about 0.17 on their own; 0.5 means grades were suppressed.</summary>
    public bool HasHiddenGrades =>
        AvgGpa is { } published && ImpliedAvg is { } shown && shown - published > 0.5;
}

/// <summary>A professor search card.</summary>
public record InstructorCard(
    string Unid,
    string Name,
    string? Department,
    int SectionCount,
    double? AvgGpa,
    IReadOnlyList<InstructorCourse> Courses);

public record InstructorCourse(
    string Subject, string CourseNumber, string Title, double? AvgGpa);

/// <summary>A builder card: the section, its professors, the course average and each professor's average, by uNID.</summary>
public record SectionCard(
    string Subject, string CourseNumber, string SectionNumber, string Campus, string Title,
    string? Times, string? Location, int? SeatsAvailable, int? Units,
    IReadOnlyList<SectionInstructor> Instructors,
    double? CourseAvgGpa, IReadOnlyDictionary<string, double?> InstructorAverages,
    string? Prerequisites,
    int? Waitlist = null, bool? HasWaitlist = null,
    string? Component = null,
    // A lab, discussion or field-work section: the lecture it registers you
    // into, "*" for any, null if unknown. A lecture: the sections that register you into it.
    string? PairsWith = null,
    IReadOnlyList<string>? PairedSections = null,
    // Whether the course has a lecture at all. False for a course whose lab is
    // the course, where there is no pairing to report either way.
    bool CourseHasLecture = false,
    // On a lecture whose course has companion sections: that a companion must be
    // chosen too, and the word for it ("lab", "discussion", "field work").
    bool RequiresCompanion = false,
    string? CompanionKind = null);

/// <summary>One course's text from the catalogue.</summary>
public record CourseInfo(
    string Subject, string CourseNumber, string? Title,
    string? Description, string? Prerequisites, string? RequirementDesignation);

/// <summary>How much data there is.</summary>
public record CoverageCounts(
    int Courses, int Sections, int Instructors, int GradedSections,
    string? FirstGradeTerm, string? LastGradeTerm, string? LastScheduleTerm);
