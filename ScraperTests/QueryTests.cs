using CoursePlanner.Data;
using Microsoft.Data.Sqlite;
using Dapper;

namespace ScraperTests;

/// <summary>
/// A throwaway database built from the real schema.sql, with a handful of rows
/// chosen to cover the cases that have actually broken: over-enrolled sections,
/// sections meeting at more than one time, courses taught by two instructors,
/// and midday boundaries.
/// </summary>
public class TestDatabase : IDisposable
{
    public string ConnectionString { get; }
    private readonly string path;

    public TestDatabase()
    {
        path = Path.Combine(Path.GetTempPath(), $"courseplanner-test-{Guid.NewGuid():N}.db");
        ConnectionString = $"Data Source={path}";

        using var db = new SqliteConnection(ConnectionString);
        db.Open();
        db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema.sql")));

        db.Execute("""
            INSERT INTO courses (subject, course_number, title, description, prerequisites, requirement_designation) VALUES
              ('CS',   '2420', 'Intro Alg & Data Struct', 'algorithms', 'C- in CS 1410', NULL),
              ('CS',   '3100', 'Models Of Computation',   'models',     'C- in CS 2100', 'Methods Requirement: Quantitative Intensive'),
              ('ANTH', '1010', 'Culture & Human Exper',   'culture',    '',              'Social/Behavioral Science Exploration');
        """);

        db.Execute("""
            INSERT INTO sections (term, subject, course_number, section_number, component, type, units, location, times, seats_available) VALUES
              ('Fall2026','CS','2420','001','Lecture','In Person',4,'WEB L103','TuTh/02:00PM-03:20PM',  12),
              ('Fall2026','CS','2420','002','Lecture','In Person',4,'WEB L104','MoWe/09:00AM-10:20AM',   0),
              ('Fall2026','CS','2420','003','Laboratory','In Person',NULL,'WEB L130','Fr/09:40AM-10:30AM', -2),
              ('Fall2026','CS','3100','001','Lecture','In Person',3,'WEB 1230','Mo/09:00AM-10:00AM; We/01:00PM-02:00PM', 5),
              ('Fall2026','ANTH','1010','001','Lecture','In Person',3,'GC 1900','MoWe/12:00PM-01:00PM',  7),
              ('Fall2026','ANTH','1010','090','Lecture','Online',   3, NULL,     NULL,                    9),
              ('Spring2026','CS','2420','001','Lecture','In Person',4,'WEB L103','TuTh/02:00PM-03:20PM', 30);
        """);

        // The enrollment side, as the seats pass writes it: CS 2420-002 is full
        // with four waiting; the lab has never had its table read.
        db.Execute("""
            UPDATE sections SET class_number = '4034', enrollment_cap = 32, enrolled = 32, waitlist = 4, has_waitlist = 1
             WHERE term = 'Fall2026' AND subject = 'CS' AND course_number = '2420' AND section_number = '002';
        """);

        // One table per grain. CS 2420-003 has no grades at all - a lab
        // publishes none - which is what the LEFT JOIN is for.
        db.Execute("""
            INSERT INTO section_grades (term, subject, course_number, section_number, gpa_avg) VALUES
              ('Fall2026','CS','2420','001',3.10),
              ('Fall2026','CS','2420','002',2.90),
              ('Fall2026','CS','3100','001',3.40),
              ('Fall2026','ANTH','1010','001',3.30),
              ('Fall2026','ANTH','1010','090',3.20),
              ('Spring2026','CS','2420','001',3.05);
        """);

        // The rollups are NOT the sum of the finer grain - suppression hides
        // groups under five students at whichever level is on screen.
        db.Execute("""
            INSERT INTO course_term_grades (term, subject, course_number, gpa_avg, grade_a) VALUES
              ('Fall2026','CS','2420',3.02,118),
              ('Spring2026','CS','2420',3.05,101);
        """);

        db.Execute("""
            INSERT INTO course_grades (subject, course_number, gpa_avg, grade_a) VALUES
              ('CS','2420',3.03,219);
        """);

        db.Execute("""
            INSERT INTO instructors (unid, display_name) VALUES
              ('u0011111','Kopta, Daniel'),
              ('u0022222','Parker, Erin'),
              ('u0033333','Brown, Noelle'),
              -- Two different people, one name. Nothing but the uNID separates
              -- them, and both teach the same subject.
              ('u0044444','Nguyen, Khoi'),
              ('u0055555','Nguyen, Khoi');
        """);

        db.Execute("""
            INSERT INTO section_instructors (term, subject, course_number, section_number, instructor_unid) VALUES
              ('Fall2026','CS','2420','001','u0011111'),
              ('Fall2026','CS','2420','001','u0022222'),
              ('Fall2026','CS','2420','002','u0022222'),
              ('Fall2026','CS','3100','001','u0011111'),
              ('Fall2026','ANTH','1010','001','u0033333'),
              -- Same name, same section: keyed on the name these would collide
              -- and one would be lost. Not on ANTH 1010-090 - that section is
              -- deliberately instructor-free for FindSections' empty-list test.
              ('Fall2026','CS','2420','002','u0044444'),
              ('Fall2026','CS','2420','002','u0055555');
        """);
    }

    public CourseQueries Queries() => new(ConnectionString);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(path); } catch { /* a temp file, not worth failing a test over */ }
        GC.SuppressFinalize(this);
    }
}

public class QueryTests : IClassFixture<TestDatabase>
{
    private readonly CourseQueries queries;

    public QueryTests(TestDatabase database) => queries = database.Queries();

    [Fact]
    public void GetTerms_ReturnsEachTermOnce()
    {
        var terms = queries.GetTerms();

        Assert.Equal(new[] { "Fall2026", "Spring2026" }, terms);
    }

    [Fact]
    public void FindSections_OpenOnly_ExcludesFullAndOverEnrolled()
    {
        // 002 has exactly 0 seats and 003 is over-enrolled at -2; neither is "open".
        var open = queries.FindSections("Fall2026", subject: "CS", openOnly: true);

        Assert.All(open, section => Assert.True(section.SeatsAvailable > 0));
        Assert.DoesNotContain(open, s => s.SectionNumber is "002" or "003");
    }

    [Fact]
    public void FindSections_CarriesTheEnrollmentFigures()
    {
        var sections = queries.FindSections("Fall2026", subject: "CS", courseNumber: "2420");

        var full = Assert.Single(sections, s => s.SectionNumber == "002");
        Assert.Equal(4, full.Waitlist);
        Assert.True(full.HasWaitlist);
        Assert.Equal(32, full.EnrollmentCap);
        Assert.Equal(32, full.Enrolled);
        Assert.Equal("4034", full.ClassNumber);

        var lab = Assert.Single(sections, s => s.SectionNumber == "003");
        Assert.Null(lab.Waitlist);
        Assert.Null(lab.HasWaitlist);
        Assert.Null(lab.ClassNumber);
    }

    [Fact]
    public void FindSections_KeepsOverEnrolledWhenNotFilteringOnSeats()
    {
        var all = queries.FindSections("Fall2026", subject: "CS");

        Assert.Contains(all, s => s.SeatsAvailable == -2);
    }

    [Fact]
    public void FindSections_TimeWindowUsesEarliestMeetingOfTheSection()
    {
        // CS 3100 meets Mo 09:00 and We 13:00. Asking for classes starting after
        // noon must not return it - its first meeting is in the morning.
        var afternoon = queries.FindSections("Fall2026", startAfter: "12:00");

        Assert.DoesNotContain(afternoon, s => s.CourseNumber == "3100");
        Assert.Contains(afternoon, s => s.CourseNumber == "2420" && s.SectionNumber == "001");
    }

    [Fact]
    public void FindSections_NoonIsMidday_NotMidnight()
    {
        // ANTH 1010-001 meets at 12:00PM. A 12PM/12AM mix-up would put it at 00:00
        // and make it match "starts before 09:00".
        var earlyBirds = queries.FindSections("Fall2026", startBefore: "09:00");

        Assert.DoesNotContain(earlyBirds, s => s.Subject == "ANTH");
    }

    [Fact]
    public void FindSections_SectionsWithoutTimesCannotSatisfyATimeFilter()
    {
        // The online section has no meeting time at all.
        var timed = queries.FindSections("Fall2026", startAfter: "00:00");

        Assert.DoesNotContain(timed, s => s.SectionNumber == "090");
    }

    [Fact]
    public void FindSections_ReturnsOneRowPerSectionWithAllItsInstructors()
    {
        // CS 2420-001 is co-taught. Joining instructors naively would duplicate the row.
        var sections = queries.FindSections("Fall2026", subject: "CS", courseNumber: "2420");

        var lecture = Assert.Single(sections, s => s.SectionNumber == "001");
        Assert.Equal(2, lecture.Instructors.Count);
        Assert.Contains(lecture.Instructors, i => i.Name == "Kopta, Daniel");
        Assert.Contains(lecture.Instructors, i => i.Name == "Parker, Erin");
    }

    [Fact]
    public void FindSections_SectionWithNoInstructorGetsAnEmptyList()
    {
        var online = Assert.Single(queries.FindSections("Fall2026", subject: "ANTH"), s => s.SectionNumber == "090");

        Assert.Empty(online.Instructors);
    }

}
