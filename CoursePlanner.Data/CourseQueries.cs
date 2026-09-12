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
    IReadOnlyList<SectionInstructor> Instructors);

// Unid is the identity; Name is only for display. Two people can share a
// name, so anything that looks an instructor up must use the Unid.
public record Instructor(string Unid, string Name, int SectionCount);

/// <summary>
/// An instructor as attached to a section. The uNID travels with the name so a
/// link out of a section list can address the person rather than the spelling -
/// routing by name merges the two "Nguyen, Khoi" who both teach MATH.
/// </summary>
public record SectionInstructor(string Unid, string Name);

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

    /// <summary>Every section of one course in one term.</summary>
    /// <summary>
    /// Courses matching text, from the whole catalogue rather than one term.
    /// The lookup page: you find the class first, then choose the term and
    /// section on its page, so a course that last ran in 2021 still has to
    /// be findable. Average is pooled across every term it has grades in.
    /// </summary>
    public IReadOnlyList<Course> SearchAllCourses(string? query)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return [];
        var parts = query.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var head = parts.Length == 2 ? parts[0] : "";
        var tail = parts.Length == 2 ? parts[1] : "";

        using var database = Open();
        // "cs" is a subject, not a substring: left as text it matched Analytics,
        // Ethics and Genetics, sorted them alphabetically, and hit the row cap
        // before a single CS course. An exact subject code returns that subject
        // and nothing else; anything else is ranked so that subject and code
        // matches come before title matches.
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
                         OR (c.subject || ' ' || c.course_number) LIKE @Like
                         OR (@Head <> '' AND c.subject LIKE @HeadLike AND c.course_number LIKE @TailLike)
                  END
              -- The builder's credit rule, applied at the course grain: a
              -- three-digit number whose sections have never carried credit
              -- is the non-credit shadow of a real course (CS 140 is CS 1400
              -- on Canvas for no units), not something to plan a degree around.
              AND NOT (LENGTH(c.course_number) = 3
                       AND NOT EXISTS (SELECT 1 FROM sections s
                                       WHERE s.subject = c.subject AND s.course_number = c.course_number
                                         AND s.units > 0))
            ORDER BY CASE WHEN c.subject LIKE @StartsLike THEN 0
                          WHEN (c.subject || ' ' || c.course_number) LIKE @StartsLike THEN 1
                          WHEN c.course_number LIKE @StartsLike THEN 2
                          ELSE 3 END,
                     c.subject, c.course_number;",
            new { Upper = query.ToUpperInvariant(), Like = $"%{query}%", StartsLike = $"{query}%",
                  Head = head, HeadLike = $"{head}%", TailLike = $"{tail}%" })
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
        bool creditOnly = true,
        string? startAfter = null,
        string? startBefore = null,
        string? campus = null)
    {
        const string sql = @"
            SELECT s.term, s.subject, s.course_number, s.section_number, s.campus, c.title,
                   s.component, s.type, s.units, s.location, s.times,
                   s.seats_available, s.seats_updated, CAST(g.gpa_avg AS REAL) AS gpa_avg
            FROM sections s
            JOIN courses c ON c.subject = s.subject AND c.course_number = s.course_number
            -- Grades are a separate source and do not cover every term, so this
            -- is a LEFT JOIN: a section with no published grades still lists.
            LEFT JOIN section_grades g ON g.term = s.term AND g.subject = s.subject
                              AND g.course_number = s.course_number
                              AND g.section_number = s.section_number
            WHERE s.term = @Term
              -- One of the registrar's three schedules, or all of them.
              AND (@Campus = '' OR s.campus = @Campus)
              AND (@Subject = '' OR s.subject = @Subject)
              AND (@CourseNumber = '' OR s.course_number = @CourseNumber)
              -- The same split SearchCourses does. A code like CS 2420 matches
              -- nothing column by column, because no single column holds it.
              AND (@Query = ''
                   OR s.subject LIKE @Like
                   OR s.course_number LIKE @Like
                   OR c.title LIKE @Like
                   OR (s.subject || ' ' || s.course_number) LIKE @Like
                   OR (@Head <> '' AND s.subject LIKE @HeadLike
                       AND s.course_number LIKE @TailLike))
              AND (@Requirement = '' OR c.requirement_designation LIKE @RequirementLike)
              AND (@OpenOnly = 0 OR s.seats_available > 0)
              -- Continuing Education: zero credit, three-digit course numbers,
              -- taught at UUCE and the satellite campuses. No 3-digit course has
              -- ever appeared in the grade data, and they count toward nothing,
              -- so they are noise for anyone planning a degree. Both halves of
              -- the test are needed: 3-digit alone would hide real labs like
              -- MATH 225, and units=0 alone would hide thesis Continuing
              -- Registration and the zero-credit labs attached to real courses.
              AND (@CreditOnly = 0
                   OR NOT (s.units = 0 AND LENGTH(s.course_number) = 3))
            ORDER BY s.subject, s.course_number, s.section_number;";

        subject ??= ""; courseNumber ??= ""; requirement ??= "";
        query = (query ?? "").Trim();

        // A two-part query is read as subject then number: "CS 2420", "math 1210".
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
                ? names : Array.Empty<SectionInstructor>()
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

    /// <summary>One published grade row as stored. Bucket counts are null where the registrar blanked them (under five).</summary>
    public sealed record GradeRow(
        string Term, string Subject, string CourseNumber, string? SectionNumber,
        double Avg, double? P25, double? P50, double? P75, double? Sd,
        int? A, int? B, int? C, int? D, int? E, int Cr, int Nc, int W, int Other);

    /// <summary>
    /// Every graded row of one grain, for <see cref="GradeIndex"/>: sections,
    /// terms of a course, or the all-terms row. Rows without an average are
    /// non-graded components - labs, discussions - and are left out here as
    /// they are everywhere else.
    /// </summary>
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

    /// <summary>Who taught which section - the join <see cref="GradeIndex"/> pools a professor's rows through.</summary>
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

    /// <summary>
    /// The builder's cards for a page of sections. The two averages - the
    /// course's, and THIS professor's in it - come from <see cref="GradeIndex"/>
    /// through the lookups passed in, so a card and a page never disagree.
    /// </summary>
    public IReadOnlyList<SectionCard> Cards(
        IReadOnlyList<Section> sections,
        Func<string, string, double?> courseAverage,
        Func<string, string, string, double?> instructorCourseAverage)
    {
        if (sections.Count == 0) return [];

        var codes = sections.Select(s => s.Subject + "|" + s.CourseNumber).Distinct().ToList();

        using var database = Open();

        // Prerequisites belong to the course, not the section, so they are read
        // once per course rather than once per row.
        var prereqs = database.Query(@"
            SELECT subject AS Subject, course_number AS Number, prerequisites AS Text
            FROM courses
            WHERE subject || '|' || course_number IN @Codes
              AND prerequisites IS NOT NULL AND TRIM(prerequisites) <> '';",
            new { Codes = codes })
            .ToDictionary(r => (string)r.Subject + "|" + (string)r.Number, r => (string)r.Text);

        return sections.Select(s =>
        {
            var code = s.Subject + "|" + s.CourseNumber;
            // Each professor's own average in this course, by uNID: a co-taught
            // section shows one figure per person rather than one person's
            // number under a label that says "this professor".
            var averages = s.Instructors
                .GroupBy(t => t.Unid)
                .ToDictionary(g => g.Key, g => instructorCourseAverage(g.Key, s.Subject, s.CourseNumber));
            return new SectionCard(s.Subject, s.CourseNumber, s.SectionNumber, s.Campus, s.Title,
                                   s.Times, s.Location, s.SeatsAvailable, s.Units, s.Instructors,
                                   courseAverage(s.Subject, s.CourseNumber), averages,
                                   prereqs.GetValueOrDefault(code));
        }).ToList();
    }

    /// <summary>Someone who has taught a course in a section with published grades: how often, and in which terms.</summary>
    public sealed record CourseTeacher(string Unid, string Name, int Sections, IReadOnlyList<string> Terms);

    /// <summary>Everyone who has taught the course in a graded section - the course page's "who teaches it".</summary>
    public IReadOnlyList<CourseTeacher> CourseTeachers(string subject, string courseNumber)
    {
        using var database = Open();
        return database.Query(@"
            SELECT si.instructor_unid AS Unid, p.display_name AS Name, COUNT(*) AS Sections,
                   GROUP_CONCAT(DISTINCT sg.term) AS Terms
            FROM section_grades sg
            JOIN section_instructors si ON si.term = sg.term AND si.subject = sg.subject
              AND si.course_number = sg.course_number AND si.section_number = sg.section_number
            JOIN instructors p ON p.unid = si.instructor_unid
            WHERE sg.subject = @Subject AND sg.course_number = @Number AND sg.gpa_avg IS NOT NULL
            GROUP BY si.instructor_unid, p.display_name;", new { Subject = subject, Number = courseNumber })
            .Select(r => new CourseTeacher((string)r.Unid, (string)r.Name, (int)(long)r.Sections, ((string)r.Terms).Split(',')))
            .ToList();
    }

    /// <summary>Every course in the catalogue, in order - the sitemap's list.</summary>
    public IReadOnlyList<(string Subject, string Number)> AllCourses()
    {
        using var database = Open();
        return database.Query("SELECT subject, course_number FROM courses ORDER BY subject, course_number")
            .Select(r => ((string)r.subject, (string)r.course_number)).ToList();
    }

    /// <summary>Catalogue prose, independent of any term.</summary>
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

    /// <summary>
    /// Course titles keyed "SUBJ NUMBER". Fetched whole and held by the caller
    /// so marking up a prerequisite string is a dictionary hit per reference
    /// rather than a query - 13,000 short rows, and the catalogue only changes
    /// when the crawler runs.
    /// </summary>
    public Dictionary<string, string> CourseTitles()
    {
        using var database = Open();
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in database.Query(
            "SELECT subject, course_number, title FROM courses WHERE title IS NOT NULL"))
            titles[$"{row.subject} {row.course_number}"] = (string)row.title;
        return titles;
    }

    /// <summary>The requirement designations actually in use, most common first.</summary>
    public IReadOnlyList<string> Designations()
    {
        using var database = Open();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in database.Query<string>(@"
            SELECT requirement_designation FROM courses
            WHERE requirement_designation IS NOT NULL AND TRIM(requirement_designation) <> ''"))
            // The registrar packs several into one field, separated by commas,
            // slashes or ampersands. A LIKE on one atom still finds the combined
            // strings, so the atoms are what the filter offers.
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


    /// <summary>
    /// How much data the site is standing on, counted rather than written down.
    /// Terms sort alphabetically in storage - every Fall, then every Spring -
    /// so the first and last graded term are picked in real time order here
    /// rather than by MIN and MAX.
    /// </summary>
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

    /// <summary>"Fall2026" as (2026, 2), so terms order chronologically.</summary>
    private static (int Year, int Season) TermKey(string term)
    {
        var year = term.Length >= 4 && int.TryParse(term[^4..], out var y) ? y : 0;
        var season = term.Length > 4
            ? Array.IndexOf(new[] { "Spring", "Summer", "Fall" }, term[..^4])
            : -1;
        return (year, season < 0 ? 3 : season);
    }

    /// <summary>
    /// "SUBJ|NUMBER" to the course's requirement designation, so the section
    /// index can apply the requirement filter without a join per search.
    /// </summary>
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

    /// <summary>Every subject code that has a section, for the department filter.</summary>
    public IReadOnlyList<string> Departments()
    {
        using var database = Open();
        return database.Query<string>(
            "SELECT DISTINCT subject FROM sections ORDER BY subject;").ToList();
    }

    /// <summary>
    /// The components that are a class in the sense a student means: something
    /// you enrol in, attend, and are graded on by the person running it.
    /// Lecture, Seminar, Studio, Workshop, Special Topics, Ensemble, Private
    /// Instruction, and Activity (fitness and dance, graded mostly CR/NC).
    ///
    /// Used only for the section count behind the busiest-first tiebreak. The
    /// search card and the graded-class count do NOT use it: they require
    /// letter grades instead, which excludes the same rotations while keeping
    /// a graded practicum - 924 graded classes sit in components outside this
    /// list, and hiding them contradicted the professor's own page.
    ///
    /// Out: anything the instructor of record supervises rather than teaches -
    /// Clinical, Practicum, Field Work, Independent Study, Research, Thesis -
    /// and the TA-run halves of a lecture, Laboratory and Discussion, whose
    /// grade lives on the lecture. Without this a pharmacy coordinator who is
    /// instructor of record on 1,283 clinical rotations showed as teaching 89
    /// classes and topped the busiest-first sort.
    /// </summary>
    private const string TeachingComponents =
        "('Lecture','Seminar','Studio','Workshop','Special Topics','Ensemble'," +
        "'Private Instruction','Activity')";

    /// <summary>
    /// Every instructor with their headcount-weighted average GPA, in one pass.
    /// Weighted so a six-student seminar cannot outvote a three-hundred-student
    /// lecture.
    /// </summary>
    /// <remarks>
    /// Read once, at startup, by <see cref="InstructorIndex"/>. It is the whole
    /// table by design: run per request to fill a page of twenty-four it cost
    /// about 450ms, because the work is the same either way - join every
    /// section assignment to every graded section and group the lot. The only
    /// saving available is to stop doing it per request.
    /// </remarks>
    public IReadOnlyList<InstructorIndex.Entry> InstructorAverages()
    {
        using var database = Open();
        // Mapped by hand rather than by constructor. SQLite hands COUNT(*) back
        // as Int64 and Dapper will not narrow it to the record's int, so
        // automatic materialisation fails at runtime rather than at compile
        // time. The same trap as SearchCourses.
        return database.Query(@"
            WITH load AS (
                SELECT i.instructor_unid AS unid, COUNT(*) AS n
                FROM section_instructors i
                JOIN sections s ON s.term = i.term AND s.subject = i.subject
                  AND s.course_number = i.course_number AND s.section_number = i.section_number
                WHERE s.component IN " + TeachingComponents + @"
                GROUP BY i.instructor_unid
            ),
            -- Distinct classes with published letter grades: exactly the set
            -- the search card draws a tile for, so the sort and the card agree.
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

    /// <summary>
    /// For each of the given instructors, every course they teach AND have
    /// published grades in, with
    /// THIS professor's average in that course - not the course's overall
    /// average, which is the same number on every professor's card and so says
    /// nothing about the person whose card you are reading.
    /// </summary>
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
            -- Graded classes only, of ANY component. A tile with no number on
            -- it says nothing about how the person grades, which is the only
            -- thing it is for; and a practicum with twenty-six letter grades
            -- in it is a class however the registrar files it. Requiring
            -- grades is what keeps a coordinator's rotations off the card.
            HAVING SUM(n.c) > 0;", new { Unids = unids })
            .GroupBy(r => (string)r.Unid)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<InstructorCourse>)g
                .Select(r => new InstructorCourse((string)r.Subject, (string)r.CourseNumber,
                                                  (string)r.Title,
                                                  r.Gpa is double gpa ? gpa : null))
                // Graded classes first - the card exists to show how this person
                // grades - then the rest; each group in catalogue order. Every
                // class is returned; the card decides how many to draw and says
                // how many it left out, rather than silently keeping the first
                // four alphabetically and dropping a graded KINES class behind
                // an ungraded CTLE one.
                .OrderBy(c => c.AvgGpa is null)
                .ThenBy(c => c.Subject, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.CourseNumber, StringComparer.OrdinalIgnoreCase).ToList());
    }
}

// ------------------------------------------------------------ grade shapes

/// <summary>
/// A grade distribution, at whatever grain the caller asked for. Cr/Nc/W are
/// kept apart from Other because a withdrawal is a different decision from an
/// unusual mark, even though the chart pools them.
///
/// P25/P50/P75 are GPA-point percentiles and StdDev the spread. For one term
/// or one section they are the dashboard's own. Pooled across terms or
/// sections they come from <see cref="GradeIndex.Pool"/>: the deviation
/// combined exactly, the percentiles read off reconstructed lists.
/// </summary>
public record GradeDistribution(
    int A, int B, int C, int D, int E,
    int Cr, int Nc, int W, int Other, double? AvgGpa,
    double? P25 = null, double? P50 = null, double? P75 = null, double? StdDev = null)
{
    public static readonly GradeDistribution Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, null);

    public int Graded => A + B + C + D + E;

    /// <summary>
    /// Everyone who received a mark - a letter grade, CR/NC, or a W. "Other"
    /// is excluded: it is lab and discussion enrolment, and those students are
    /// already counted once through the lecture section they belong to.
    /// </summary>
    public int Total => Graded + Cr + Nc + W;
    public bool Any => Total > 0;

    /// <summary>
    /// Every bucket the dashboard publishes, in order.
    ///
    /// Note what Other means at the course grain: it is the headcount of
    /// non-graded components. CS 2420's six laboratory sections contribute 260
    /// students who are the same people already counted in the lecture's letter
    /// grades, so on any course with a lab this bar roughly duplicates the
    /// enrolment rather than adding a new outcome.
    /// </summary>
    /// <summary>
    /// What the chart draws. "Other" is left out: it is lab and discussion
    /// enrolment rather than a grade, and on any course with a lab it towered
    /// over every real grade and flattened the distribution people came to
    /// read. It still counts toward <see cref="Total"/>.
    /// </summary>
    public IReadOnlyList<(string Label, int Count)> Bars =>
        [("A", A), ("B", B), ("C", C), ("D", D), ("E", E),
         ("CR", Cr), ("NC", Nc), ("W", W)];

    /// <summary>
    /// The average implied by the bars alone. Compared with the published
    /// AvgGpa it reveals how much of the class the chart is not showing.
    /// </summary>
    public double? ImpliedAvg =>
        Graded == 0 ? null : (A * 4.0 + B * 3.0 + C * 2.0 + D * 1.0) / Graded;

    /// <summary>
    /// Whether the bars are materially at odds with the published average.
    ///
    /// The threshold is 0.5, not zero, because two things push them apart and
    /// only one is worth mentioning. Every section is inflated a little by the
    /// coarse buckets - the "A" bar counts A and A- alike, scored 4.0 - which is
    /// a median of +0.17 across all 37,279 graded sections. Suppression of
    /// sub-five grade groups is what produces the large gaps: 14.6% clear 0.5.
    /// </summary>
    public bool HasHiddenGrades =>
        AvgGpa is { } published && ImpliedAvg is { } shown && shown - published > 0.5;
}

/// <summary>An instructor plus the courses they teach, for the search cards.</summary>
public record InstructorCard(
    string Unid,
    string Name,
    string? Department,
    int SectionCount,
    double? AvgGpa,
    IReadOnlyList<InstructorCourse> Courses);

public record InstructorCourse(
    string Subject, string CourseNumber, string Title, double? AvgGpa);

/// <summary>
/// A section as the schedule builder shows it: the class, who teaches it, how
/// the course grades on average, and how each of its professors grades it,
/// by uNID. The last two are the comparison the card exists to make.
/// </summary>
public record SectionCard(
    string Subject, string CourseNumber, string SectionNumber, string Campus, string Title,
    string? Times, string? Location, int? SeatsAvailable, int? Units,
    IReadOnlyList<SectionInstructor> Instructors,
    double? CourseAvgGpa, IReadOnlyDictionary<string, double?> InstructorAverages,
    string? Prerequisites);

/// <summary>Catalogue prose for one course.</summary>
public record CourseInfo(
    string Subject, string CourseNumber, string? Title,
    string? Description, string? Prerequisites, string? RequirementDesignation);

/// <summary>What the About page reports: the size and reach of the data.</summary>
public record CoverageCounts(
    int Courses, int Sections, int Instructors, int GradedSections,
    string? FirstGradeTerm, string? LastGradeTerm, string? LastScheduleTerm);
