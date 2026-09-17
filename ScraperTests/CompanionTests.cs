using CoursePlanner.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace ScraperTests;

/// <summary>
/// Companion-to-lecture pairing on the builder's cards. The one derivation allowed is
/// a course with a single lecture; everything else comes from pairs_with or is
/// left null. Its own fixture, since these tests write to the database.
/// </summary>
public class CompanionTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase database;
    private readonly CourseQueries queries;

    public CompanionTests(TestDatabase database)
    {
        this.database = database;
        queries = database.Queries();
    }

    private IReadOnlyList<SectionCard> Cards(string subject) =>
        queries.Cards(queries.FindSections("Fall2026", subject: subject), (_, _) => null, (_, _, _) => null);

    private SectionCard Card(string subject, string number, string section) =>
        Cards(subject).Single(c => c.CourseNumber == number && c.SectionNumber == section);

    private void Set(string course, string section, string? pairsWith)
    {
        using var db = new SqliteConnection(database.ConnectionString);
        db.Execute("UPDATE sections SET pairs_with = @pairsWith WHERE term = 'Fall2026' AND subject = 'CS' AND course_number = @course AND section_number = @section",
                   new { pairsWith, course, section });
    }

    [Fact]
    public void ALabThatIsTheWholeCourseReportsNoPairingEitherWay()
    {
        // 328 sections this fall are a lab with no lecture anywhere in the course:
        // the lab IS the course. There is nothing to pair, so the card must stay
        // silent rather than say a pairing was not published.
        using (var db = new SqliteConnection(database.ConnectionString))
            db.Execute("""
                INSERT OR IGNORE INTO courses (subject, course_number, title) VALUES ('ANTH','1011','Field Lab');
                INSERT OR IGNORE INTO sections (term, subject, course_number, section_number, component, type, units, location, times, seats_available)
                VALUES ('Fall2026','ANTH','1011','001','Laboratory','In Person',3,'GC 1900','Fr/01:00PM-03:00PM', 5);
            """);

        var lab = Cards("ANTH").Single(c => c.CourseNumber == "1011");
        Assert.Equal("Laboratory", lab.Component);
        Assert.False(lab.CourseHasLecture);
        Assert.Null(lab.PairsWith);

        // And a lab that does sit under a lecture still reports one.
        Assert.True(Card("CS", "2420", "003").CourseHasLecture);
    }

    [Fact]
    public void AStoredPairingToALectureThatNoLongerExistsIsNotShown()
    {
        // Sections get renumbered between crawls. A lab still stamped with a
        // lecture that is gone must not send anyone to it.
        Set("2420", "003", "099");

        Assert.Null(Card("CS", "2420", "003").PairsWith);
        Assert.Empty(Card("CS", "2420", "001").PairedSections!);
        Assert.Empty(Card("CS", "2420", "002").PairedSections!);
    }

    [Fact]
    public void TheAnswerDoesNotDependOnWhichSectionsAreOnScreen()
    {
        // A search result is one page of one filter. Resolving from it would make
        // a lecture's companion list grow and shrink with the page number, so the
        // pairing is read from the course's whole section list instead.
        Set("2420", "003", "002");

        var whole = Cards("CS");
        var lectureWhole = whole.Single(c => c.CourseNumber == "2420" && c.SectionNumber == "002");
        var labWhole = whole.Single(c => c.CourseNumber == "2420" && c.SectionNumber == "003");

        // The same two sections asked for one at a time, as a narrow page would.
        var all = queries.FindSections("Fall2026", subject: "CS");
        SectionCard Alone(string section) =>
            queries.Cards(all.Where(s => s.CourseNumber == "2420" && s.SectionNumber == section).ToList(),
                          (_, _) => null, (_, _, _) => null).Single();

        Assert.Equal(lectureWhole.PairedSections, Alone("002").PairedSections);
        Assert.Equal(new[] { "003" }, Alone("002").PairedSections);
        Assert.Equal(labWhole.PairsWith, Alone("003").PairsWith);
        Assert.Equal("002", Alone("003").PairsWith);
    }

    [Fact]
    public void ALabInACourseWithSeveralLecturesStaysUnpairedWithoutANote()
    {
        // CS 2420 has lectures 001 and 002 and lab 003, and nothing published.
        // Nothing is guessed from numbering, counts or times.
        Set("2420", "003", null);

        var lab = Card("CS", "2420", "003");
        Assert.Equal("Laboratory", lab.Component);
        Assert.Null(lab.PairsWith);

        Assert.Empty(Card("CS", "2420", "001").PairedSections!);
        Assert.Empty(Card("CS", "2420", "002").PairedSections!);
        Assert.Equal("Lecture", Card("CS", "2420", "001").Component);
    }

    [Fact]
    public void APublishedPairingReachesTheLabAndTheLectureListsIt()
    {
        Set("2420", "003", "002");

        Assert.Equal("002", Card("CS", "2420", "003").PairsWith);
        Assert.Equal(new[] { "003" }, Card("CS", "2420", "002").PairedSections);
        Assert.Empty(Card("CS", "2420", "001").PairedSections!);
        // A lecture is never itself paired.
        Assert.Null(Card("CS", "2420", "002").PairsWith);
    }

    [Fact]
    public void ALabInACourseWithOneLectureNeedsNoNote()
    {
        // CS 3100 has one lecture, 001. A lab there can only register you into it.
        using (var db = new SqliteConnection(database.ConnectionString))
            db.Execute("""
                INSERT OR IGNORE INTO sections (term, subject, course_number, section_number, component, type, units, location, times, seats_available)
                VALUES ('Fall2026','CS','3100','002','Laboratory','In Person',NULL,'WEB 1250','Fr/10:45AM-11:35AM', 5);
            """);

        Assert.Equal("001", Card("CS", "3100", "002").PairsWith);
        // Contains, not Equal: the fixture is shared and another test adds a discussion to the same course.
        Assert.Contains("002", Card("CS", "3100", "001").PairedSections!);
    }

    [Fact]
    public void ChoicesForOffersThePublishedCompanionsOfALecture()
    {
        // Two lectures, each with its own published labs.
        var course = new[]
        {
            new CourseSection("001", "Lecture", null),
            new CourseSection("002", "Laboratory", "001"),
            new CourseSection("003", "Laboratory", "001"),
            new CourseSection("011", "Lecture", null),
            new CourseSection("012", "Laboratory", "011"),
        };
        Assert.Equal(new[] { "002", "003" }, Companions.ChoicesFor(course, "001"));
        Assert.Equal(new[] { "012" }, Companions.ChoicesFor(course, "011"));
    }

    [Fact]
    public void ChoicesForOffersEveryCompanionWhenNothingIsPublished()
    {
        // Two lectures, no pairing published: the student may pick any lab.
        var course = new[]
        {
            new CourseSection("001", "Lecture", null),
            new CourseSection("011", "Lecture", null),
            new CourseSection("002", "Laboratory", null),
            new CourseSection("003", "Laboratory", null),
            new CourseSection("004", "Laboratory", null),
        };
        Assert.Equal(new[] { "002", "003", "004" }, Companions.ChoicesFor(course, "001"));
        Assert.Equal(new[] { "002", "003", "004" }, Companions.ChoicesFor(course, "011"));
    }

    [Fact]
    public void KindNamesTheComponentAndCombinesAMixedCourse()
    {
        Assert.Equal("lab", Companions.Kind(new[] { "Laboratory", "Laboratory" }));
        Assert.Equal("discussion", Companions.Kind(new[] { "Discussion" }));
        Assert.Equal("field work", Companions.Kind(new[] { "Field Work" }));
        Assert.Equal("discussion or lab", Companions.Kind(new[] { "Laboratory", "Discussion" }));
        Assert.Null(Companions.Kind(System.Array.Empty<string?>()));
    }

    [Fact]
    public void ADiscussionIsPairedLikeALab()
    {
        using (var db = new SqliteConnection(database.ConnectionString))
            db.Execute("""
                INSERT OR IGNORE INTO sections (term, subject, course_number, section_number, component, type, units, location, times, seats_available)
                VALUES ('Fall2026','CS','3100','003','Discussion','In Person',NULL,'WEB 1250','Th/10:45AM-11:35AM', 5);
            """);

        Assert.Equal("001", Card("CS", "3100", "003").PairsWith);
        Assert.Contains("003", Card("CS", "3100", "001").PairedSections!);
    }

    [Fact]
    public void ASeminarBesideALectureIsNotPaired()
    {
        // A Seminar or Studio sharing a course with a lecture is rare and has
        // never been checked against counts, so it is neither paired nor listed.
        using (var db = new SqliteConnection(database.ConnectionString))
            db.Execute("""
                INSERT OR IGNORE INTO sections (term, subject, course_number, section_number, component, type, units, location, times, seats_available)
                VALUES ('Fall2026','CS','3100','004','Seminar','In Person',3,'WEB 1250','Mo/03:00PM-04:00PM', 5),
                       ('Fall2026','CS','3100','005','Field Work','In Person',NULL,NULL,NULL, 5);
            """);

        Assert.Null(Card("CS", "3100", "004").PairsWith);
        Assert.DoesNotContain("004", Card("CS", "3100", "001").PairedSections!);
        // Field work is one of the four companion components, and pairs.
        Assert.Equal("001", Card("CS", "3100", "005").PairsWith);
    }

    [Fact]
    public void AnyLectureIsKeptAsIsAndListedOnEveryLecture()
    {
        Set("2420", "003", Companions.Any);

        Assert.Equal(Companions.Any, Card("CS", "2420", "003").PairsWith);
        Assert.Equal(new[] { "003" }, Card("CS", "2420", "001").PairedSections);
        Assert.Equal(new[] { "003" }, Card("CS", "2420", "002").PairedSections);
    }
}
