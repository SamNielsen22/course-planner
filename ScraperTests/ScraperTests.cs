using HtmlAgilityPack;

namespace ScraperTests;

public class ScraperTests
{
    [Fact]
    public void SubjectScraper_FindsKnownSubjects()
    {
        var doc = LoadSample("index.html");

        var subjects = SubjectScraper.Scrape(doc);

        Assert.Contains("subject=CS", subjects);
        Assert.Contains("subject=ACCTG", subjects);
        Assert.True(subjects.Count > 50);
    }

    [Fact]
    public void DescriptionScraper_ParsesDetails()
    {
        var doc = LoadSample("CS2420.html");

        var details = DescriptionScraper.Scrape(doc);

        Assert.Contains("computational efficiency", details.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CS 1410", details.Prerequisites, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainSearchScraper_ParsesSections()
    {
        var doc = LoadSample("cs.html");

        var sections = MainSearchScraper.Scrape(doc);

        Assert.NotEmpty(sections);
        Assert.Contains(sections, s => s.Term == "Spring2026");
        Assert.Contains(sections, s => s.Subject == "CS" && s.CourseNumber == "2420");
    }

    [Fact]
    public void MainSearchScraper_ParsesSeatsAvailable()
    {
        var sections = MainSearchScraper.Scrape(LoadSample("cs.html"));

        // Seats are rendered twice per card, once per breakpoint - the parser must
        // not be confused by the duplicate, and must read the number not the flag.
        Assert.Contains(sections, s => s.SeatsAvailable is > 0);

        // Over-enrolled sections report a negative count, so "open" means > 0
        // rather than "a number is present".
        Assert.Contains(sections, s => s.SeatsAvailable is < 0);
    }

    [Fact]
    public void MainSearchScraper_ReadsWhetherASectionHasAWaitList()
    {
        var sections = MainSearchScraper.Scrape(LoadSample("cs.html"));

        // The list says yes or no; the count of those waiting lives on the sections table.
        Assert.Contains(sections, s => s.HasWaitlist == true);
        Assert.Contains(sections, s => s.HasWaitlist == false);
        Assert.DoesNotContain(sections, s => s.HasWaitlist is null);
    }

    [Fact]
    public void MainSearchScraper_PairsEachLabWithTheLectureWhoseNoteNamesIt()
    {
        // Fall 2026 Math. The registrar writes the note several ways on this one
        // page: "Sections 14, 15, and 19 belong to this lecture", "31-32, 51-52",
        // "2-3, 7" and "2 - 3" with spaces.
        var sections = MainSearchScraper.Scrape(LoadSample("math.html"));
        string? Lecture(string course, string lab) =>
            sections.Single(s => s.Subject == "MATH" && s.CourseNumber == course && s.SectionNumber == lab).PairsWith;

        // Math 2250: lab 019 belongs to lecture 013, not 016, though 016 meets
        // at the same hour. The registration service confirmed this pairing.
        Assert.Equal("013", Lecture("2250", "019"));
        Assert.Equal("013", Lecture("2250", "014"));
        Assert.Equal("016", Lecture("2250", "017"));
        Assert.Equal("001", Lecture("2250", "002"));    // "Sections 2 - 3"

        // Math 1210: a block added late, numbered after another lecture's labs.
        Assert.Equal("015", Lecture("1210", "040"));
        Assert.Equal("015", Lecture("1210", "041"));
        Assert.Equal("033", Lecture("1210", "034"));

        // Math 1010: two ranges in one note.
        Assert.Equal("030", Lecture("1010", "051"));
        Assert.Equal("033", Lecture("1010", "038"));

        // Lectures are never paired themselves.
        Assert.Null(Lecture("2250", "013"));
        Assert.DoesNotContain(sections, s => s.Component == "Lecture" && s.PairsWith is not null);
    }

    [Fact]
    public void MainSearchScraper_PairsDiscussionSectionsTheSameWayAsLabs()
    {
        // Fall 2026 Biology. BIOL 1620 has two lectures and no lab at all: you
        // register for a discussion and the lecture comes with it. Lecture 001
        // names 002-009, lecture 011 names 012-018, and 019 exists but is named
        // by neither, so it stays unpaired rather than guessed.
        var sections = MainSearchScraper.Scrape(LoadSample("biol.html"));
        SectionRecord Sec(string n) => sections.Single(s => s.CourseNumber == "1620" && s.SectionNumber == n);

        Assert.Equal("Discussion", Sec("002").Component);
        Assert.Equal("001", Sec("002").PairsWith);
        Assert.Equal("001", Sec("009").PairsWith);
        Assert.Equal("011", Sec("012").PairsWith);
        Assert.Equal("011", Sec("018").PairsWith);
        Assert.Null(Sec("019").PairsWith);
    }

    [Fact]
    public void MainSearchScraper_LeavesALabUnpairedWhenNoLectureNamesIt()
    {
        // Spring 2026 CS: CS 2420 has two lectures and its notes name no labs.
        // No guessing from numbering or counts - the lab stays unpaired.
        var sections = MainSearchScraper.Scrape(LoadSample("cs.html"));
        var labs = sections.Where(s => s.CourseNumber == "2420" && s.Component == "Laboratory").ToList();
        Assert.NotEmpty(labs);
        Assert.All(labs, lab => Assert.Null(lab.PairsWith));
    }

    [Fact]
    public void CompanionNotes_ReadsTheTemplateSentenceAndItsNumberFormats()
    {
        Assert.Equal(new[] { "016", "017", "040", "041" }, CompanionNotes.Owned("Sections 16, 17, 40, and 41 belong to this lecture."));
        Assert.Equal(new[] { "031", "032", "051", "052" }, CompanionNotes.Owned("Sections 31-32, 51-52 belong to this lecture."));
        Assert.Equal(new[] { "002", "003" }, CompanionNotes.Owned("Sections 2 - 3 belong to this section."));
        Assert.Equal(new[] { "002", "003", "004", "005", "006" }, CompanionNotes.Owned("Sections 002-006 belong to this section."));
        Assert.Equal(new[] { "022", "023", "024", "025" }, CompanionNotes.Owned("Lab sections 022 - 025 correspond to this lecture section."));

        // Nothing named, or text about another course, pairs nothing.
        Assert.Null(CompanionNotes.Owned("This course requires registration for a lab section."));
        Assert.Null(CompanionNotes.Owned("Students will be registered when registering for one of the lab sections."));
        Assert.Null(CompanionNotes.Owned("ECE 2240 together with ECE 3960-003 are equivalent to ECE 3500."));
        Assert.Null(CompanionNotes.Owned(null));

        // Two sentences in one note are read whole, and a repeat is counted once.
        Assert.Equal(new[] { "002", "003", "007", "008" },
            CompanionNotes.Owned("Sections 2-3 belong to this lecture. Sections 7 and 8 belong to this section. Sections 2-3 belong to this lecture."));

        // Rarer phrasings, deliberately not read: on four terms of data none of
        // them changed a course's answer. Kept here so they are not re-added by accident.
        Assert.Null(CompanionNotes.Owned("Sections 002-003 are associated with this lecture section."));
        Assert.Null(CompanionNotes.Owned("This course requires registration for lab section 006."));
        Assert.Null(CompanionNotes.Owned("This course requires registration for a once-a-week, in-person discussion section (022 or 023)."));
    }

    [Fact]
    public void SubjectScraper_ReadsTheSubjectOutOfAQueryAsTheDatabaseSpellsIt()
    {
        Assert.Equal("CS", SubjectScraper.Subject("subject=CS"));
        Assert.Equal("ME EN", SubjectScraper.Subject("subject=ME%20EN"));
        Assert.Equal("CS", SubjectScraper.Subject("subject=CS&type=AOCE"));
        Assert.Equal("ME EN", SubjectScraper.Subject("type=AOCE&subject=ME%20EN"));
        Assert.Equal("", SubjectScraper.Subject("type=AOCE"));

        // Every link on a real index resolves to a plain subject code.
        var queries = SubjectScraper.Scrape(LoadSample("index.html"));
        Assert.All(queries, q => Assert.Matches(@"^[A-Z][A-Z ]{0,6}$", SubjectScraper.Subject(q)));
        Assert.Contains("ME EN", queries.Select(SubjectScraper.Subject));
    }

    [Fact]
    public void SubjectScraper_FindsBothHalvesOfACreditNoncreditMenu()
    {
        // Some subjects answer with a menu instead of cards. Its two links are
        // absolute paths with extra query fields, and both the crawl and the
        // pairing refresh rely on the scraper reading them off this page.
        var doc = LoadSample("menu_essf.html");
        Assert.NotNull(doc.DocumentNode.SelectSingleNode("//div[contains(@class,'alert') and contains(.,'divided by credit and noncredit')]"));

        var parts = SubjectScraper.Scrape(doc);

        Assert.Equal(2, parts.Count);
        Assert.All(parts, q => Assert.Equal("ESSF", SubjectScraper.Subject(q)));
        Assert.Contains(parts, q => q.Contains("credit=Y"));
        Assert.Contains(parts, q => q.Contains("credit=N"));
        // And each is a query the crawl can append to class_list.html? directly.
        Assert.All(parts, q => Assert.StartsWith("subject=ESSF&", q));
    }

    [Fact]
    public void SectionsTableScraper_ReadsEverySectionOfASubject()
    {
        // sections.html with the catalogue number left blank: the whole subject, one row each.
        var doc = LoadSample("sections_games.html");

        Assert.Equal("Fall2026", SectionsTableScraper.Term(doc));
        var rows = SectionsTableScraper.Scrape(doc);
        Assert.Equal(87, rows.Count);
        Assert.All(rows, r => Assert.Equal("GAMES", r.Subject));

        // The lecture students reach through a lab has a class number here, though the list leaves it blank.
        var lecture = Assert.Single(rows, r => r.CourseNumber == "1010" && r.SectionNumber == "001");
        Assert.Equal("7519", lecture.ClassNumber);
        Assert.Equal(140, lecture.EnrollmentCap);
        Assert.Equal(117, lecture.Enrolled);
        Assert.Equal(0, lecture.Waitlist);
        Assert.Equal(23, lecture.SeatsAvailable);

        // A full lab with someone waiting.
        var lab = Assert.Single(rows, r => r.CourseNumber == "1010" && r.SectionNumber == "006");
        Assert.Equal(0, lab.SeatsAvailable);
        Assert.Equal(1, lab.Waitlist);
    }

    [Fact]
    public void SectionsTableScraper_IgnoresAPageThatIsNotATable()
    {
        var doc = LoadSample("CS2420.html");   // a description page: a heading with no term, no table

        Assert.Null(SectionsTableScraper.Term(doc));
        Assert.Empty(SectionsTableScraper.Scrape(doc));
    }

    [Fact]
    public void DescriptionScraper_ParsesRequirementDesignation()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("""
            <div class="card">
              <div class="card-header">Enrollment Information</div>
              <div class="card-body">
                <div class="row mt-3">
                  <div class="col-md-2 fw-bold">Enrollment Requirement:</div>
                  <div class="col"><span>Prerequisites: 'C-' or better in CS 1410</span></div>
                </div>
                <div class="row mt-3">
                  <div class="col-md-2 fw-bold">Requirement Designation:</div>
                  <div class="col"><div><span>Methods Requirement: Quantitative Intensive</span> <br/></div></div>
                </div>
              </div>
            </div>
            """);

        var details = DescriptionScraper.Scrape(doc);

        Assert.Equal("Methods Requirement: Quantitative Intensive", details.RequirementDesignation);
        Assert.Equal(" 'C-' or better in CS 1410", details.Prerequisites);
    }

    [Fact]
    public void DescriptionScraper_JoinsMultipleDesignations()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("""
            <div class="card">
              <div class="card-header">Enrollment Information</div>
              <div class="card-body">
                <div class="row mt-3">
                  <div class="col-md-2 fw-bold">Requirement Designation:</div>
                  <div class="col"><div><span>Life Sciences</span> <br/><span>Diversity</span> <br/></div></div>
                </div>
              </div>
            </div>
            """);

        Assert.Equal("Life Sciences; Diversity", DescriptionScraper.Scrape(doc).RequirementDesignation);
    }

    [Fact]
    public void DescriptionScraper_NoDesignationIsEmpty()
    {
        // CS 2420 carries no gen ed designation, so the row is absent entirely.
        var details = DescriptionScraper.Scrape(LoadSample("CS2420.html"));

        Assert.Equal("", details.RequirementDesignation);
        Assert.Contains("CS 1410", details.Prerequisites);
    }

    [Fact]
    public void DescriptionScraper_CapturesPrerequisitesWithoutThePrefix()
    {
        // Some courses label the span "Prerequisites: ..." and some just state the
        // requirement. Matching on the prefix silently dropped the second kind.
        var doc = new HtmlDocument();
        doc.LoadHtml("""
            <div class="card">
              <div class="card-header">Enrollment Information</div>
              <div class="card-body">
                <div class="row mt-3">
                  <div class="col-md-2 fw-bold">Enrollment Requirement:</div>
                  <div class="col"><span>'C-' or better in (CS2100 OR MATH2200) AND Foundational Courses complete</span></div>
                </div>
              </div>
            </div>
            """);

        var details = DescriptionScraper.Scrape(doc);

        Assert.Equal("'C-' or better in (CS2100 OR MATH2200) AND Foundational Courses complete",
                     details.Prerequisites);
    }

    [Fact]
    public void DescriptionScraper_StripsThePrefixWhenPresent()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("""
            <div class="card">
              <div class="card-header">Enrollment Information</div>
              <div class="card-body">
                <div class="row mt-3">
                  <div class="col-md-2 fw-bold">Enrollment Requirement:</div>
                  <div class="col"><span>Prerequisites: 'C-' or better in CS 1410</span></div>
                </div>
              </div>
            </div>
            """);

        Assert.Equal(" 'C-' or better in CS 1410", DescriptionScraper.Scrape(doc).Prerequisites);
    }

    [Fact]
    public void DescriptionScraper_DoesNotMistakeADesignationForAPrerequisite()
    {
        // ANTH 1010 has a designation but no enrollment requirement. Scanning every
        // span in the card would store "Social/Behavioral Science Exploration" as
        // that course's prerequisites.
        var doc = new HtmlDocument();
        doc.LoadHtml("""
            <div class="card">
              <div class="card-header">Enrollment Information</div>
              <div class="card-body">
                <div class="row mt-3">
                  <div class="col-md-2 fw-bold">Requirement Designation:</div>
                  <div class="col"><div><span>Social/Behavioral Science Exploration</span> <br/></div></div>
                </div>
              </div>
            </div>
            """);

        var details = DescriptionScraper.Scrape(doc);

        Assert.Equal("", details.Prerequisites);
        Assert.Equal("Social/Behavioral Science Exploration", details.RequirementDesignation);
    }

    [Fact]
    public void DescriptionScraper_ParsesTheRealPageThatUsedToLoseItsPrerequisites()
    {
        // CS 3100's live page states its requirement without a "Prerequisites:"
        // prefix, and carries a designation in the same card. Both must come out.
        var details = DescriptionScraper.Scrape(LoadSample("CS3100.html"));

        Assert.Contains("CS2100", details.Prerequisites);
        Assert.Equal("Methods Requirement: Quantitative Intensive", details.RequirementDesignation);
        Assert.Contains("models of computation", details.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainSearchScraper_ReturnsEverySectionOfACourse()
    {
        // A course's lecture and its labs are separate sections. Collapsing them to
        // one row per course is how most of the catalogue went missing before.
        var sections = MainSearchScraper.Scrape(LoadSample("cs.html"));

        var cs2420 = sections.Where(s => s.CourseNumber == "2420").ToList();

        Assert.True(cs2420.Count > 1, $"expected several sections of CS 2420, got {cs2420.Count}");
        Assert.Equal(cs2420.Count, cs2420.Select(s => s.SectionNumber).Distinct().Count());
        Assert.Contains(cs2420, s => s.Component == "Lecture");
        Assert.Contains(cs2420, s => s.Component == "Laboratory");
    }

    [Fact]
    public void MainSearchScraper_TimesAlwaysHaveDaysAndAStartAndEnd()
    {
        var sections = MainSearchScraper.Scrape(LoadSample("cs.html"));

        Assert.Contains(sections, s => s.Times != null);
        Assert.All(sections.Where(s => s.Times != null),
                   s => Assert.Matches(@"^[A-Za-z-]+/\d{2}:\d{2}[AP]M-\d{2}:\d{2}[AP]M", s.Times!));
    }

    // The Asia Campus and UOnline schedules are the same page with a different
    // heading: sections 3xx in UAC rooms, and 29x with no room at all.
    [Fact]
    public void MainSearchScraper_ParsesAnAsiaCampusPage()
    {
        var sections = MainSearchScraper.Scrape(LoadSample("uac_cs.html"));

        var lab = Assert.Single(sections, s => s.Subject == "CS" && s.CourseNumber == "1400" && s.SectionNumber == "302");
        Assert.Equal("Fall2026", lab.Term);
        Assert.Equal("UAC 513", lab.Location);
        Assert.Contains(lab.Instructors, i => i.Unid == "u6024385");
        Assert.All(sections, s => Assert.StartsWith("3", s.SectionNumber));
    }

    [Fact]
    public void MainSearchScraper_ParsesAUOnlinePage()
    {
        var sections = MainSearchScraper.Scrape(LoadSample("online_econ.html"));

        var history = Assert.Single(sections, s => s.CourseNumber == "1740" && s.SectionNumber == "290");
        Assert.Equal("Fall2026", history.Term);
        Assert.Null(history.Location);
        Assert.Equal(2, history.Instructors.Count);
        Assert.Equal(19, history.SeatsAvailable);
    }

    // The registrar's server cuts a few pages off partway through a card, the
    // same pages every time. The cards before the cut are whole and kept; the
    // one the cut fell in has no lines and is dropped rather than stored empty.
    [Fact]
    public void ACutOffPageKeepsItsWholeCardsAndDropsTheUnfinishedOne()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "uac_film_cutoff.html"));
        Assert.DoesNotContain("</html>", html);                       // it really is cut off
        var doc = new HtmlDocument();
        doc.LoadHtml(CutOffPage.DropUnfinishedCard(html));

        var sections = MainSearchScraper.Scrape(doc);

        Assert.Equal(11, sections.Count);
        Assert.DoesNotContain(sections, s => s.CourseNumber == "2920");   // the card the cut fell in
        Assert.Contains(sections, s => s.CourseNumber == "2650" && s.SectionNumber == "301");
    }

    private static HtmlDocument LoadSample(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", fileName);
        Assert.True(File.Exists(path), $"Sample file not found: {path}");

        var doc = new HtmlDocument();
        doc.Load(path);
        return doc;
    }
}
