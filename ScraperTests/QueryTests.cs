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
            INSERT INTO sections (term, subject, course_number, section_number, component, type, units, location, times, seats_available, gpa_avg) VALUES
              ('Fall2026','CS','2420','001','Lecture','In Person',4,'WEB L103','TuTh/02:00PM-03:20PM',  12,  3.10),
              ('Fall2026','CS','2420','002','Lecture','In Person',4,'WEB L104','MoWe/09:00AM-10:20AM',   0,  2.90),
              ('Fall2026','CS','2420','003','Laboratory','In Person',NULL,'WEB L130','Fr/09:40AM-10:30AM', -2, NULL),
              ('Fall2026','CS','3100','001','Lecture','In Person',3,'WEB 1230','Mo/09:00AM-10:00AM; We/01:00PM-02:00PM', 5, 3.40),
              ('Fall2026','ANTH','1010','001','Lecture','In Person',3,'GC 1900','MoWe/12:00PM-01:00PM',  7,  3.30),
              ('Fall2026','ANTH','1010','090','Lecture','Online',   3, NULL,     NULL,                    9,  3.20),
              ('Spring2026','CS','2420','001','Lecture','In Person',4,'WEB L103','TuTh/02:00PM-03:20PM', 30, 3.05);
        """);

        db.Execute("""
            INSERT INTO section_instructors (term, subject, course_number, section_number, instructor) VALUES
              ('Fall2026','CS','2420','001','Kopta, Daniel'),
              ('Fall2026','CS','2420','001','Parker, Erin'),
              ('Fall2026','CS','2420','002','Parker, Erin'),
              ('Fall2026','CS','3100','001','Kopta, Daniel'),
              ('Fall2026','ANTH','1010','001','Brown, Noelle');
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
    public void SearchCourses_OnlyReturnsCoursesOfferedThatTerm()
    {
        // ANTH 1010 runs in Fall2026 only, so a Spring2026 search must not see it.
        var fall = queries.SearchCourses("Fall2026", null, null);
        var spring = queries.SearchCourses("Spring2026", null, null);

        Assert.Contains(fall, c => c.Subject == "ANTH");
        Assert.DoesNotContain(spring, c => c.Subject == "ANTH");
    }

    [Fact]
    public void SearchCourses_FiltersByRequirementDesignation()
    {
        var quantitative = queries.SearchCourses("Fall2026", null, "Quantitative");

        Assert.Single(quantitative);
        Assert.Equal("3100", quantitative[0].CourseNumber);
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
        Assert.Contains("Kopta, Daniel", lecture.Instructors);
        Assert.Contains("Parker, Erin", lecture.Instructors);
    }

    [Fact]
    public void FindSections_SectionWithNoInstructorGetsAnEmptyList()
    {
        var online = Assert.Single(queries.FindSections("Fall2026", subject: "ANTH"), s => s.SectionNumber == "090");

        Assert.Empty(online.Instructors);
    }

    [Fact]
    public void SearchInstructors_CountsSectionsPerInstructor()
    {
        // COUNT(*) arrives as Int64 and will not bind to an int constructor - this
        // failed at runtime once already.
        var instructors = queries.SearchInstructors(null, "Fall2026");

        Assert.Equal(2, Assert.Single(instructors, i => i.Name == "Parker, Erin").SectionCount);
        Assert.Equal(2, Assert.Single(instructors, i => i.Name == "Kopta, Daniel").SectionCount);
        Assert.Equal(1, Assert.Single(instructors, i => i.Name == "Brown, Noelle").SectionCount);
    }

    [Fact]
    public void SearchInstructors_RanksByLoadThenAlphabetically()
    {
        var instructors = queries.SearchInstructors(null, "Fall2026");

        // Two sections each, so the tie breaks on name; one section sorts last.
        Assert.Equal(new[] { "Kopta, Daniel", "Parker, Erin", "Brown, Noelle" },
                     instructors.Select(i => i.Name));
    }

    [Fact]
    public void SearchInstructors_MatchesPartialNamesCaseInsensitively()
    {
        var found = queries.SearchInstructors("kopta", null);

        Assert.Single(found);
        Assert.Equal("Kopta, Daniel", found[0].Name);
    }

    [Fact]
    public void GetInstructorSections_ReturnsOnlyThatInstructorsSectionsInThatTerm()
    {
        var teaching = queries.GetInstructorSections("Fall2026", "Kopta, Daniel");

        Assert.Equal(2, teaching.Count);
        Assert.All(teaching, s => Assert.Contains("Kopta, Daniel", s.Instructors));
        Assert.All(teaching, s => Assert.Equal("Fall2026", s.Term));
    }

    [Fact]
    public void GetSections_CarriesGradeAndSeatData()
    {
        var lecture = Assert.Single(
            queries.GetSections("Fall2026", "CS", "2420"), s => s.SectionNumber == "001");

        Assert.Equal(3.10, lecture.GpaAvg);
        Assert.Equal(12, lecture.SeatsAvailable);
        Assert.Equal("Intro Alg & Data Struct", lecture.Title);
    }
}
