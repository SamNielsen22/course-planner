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

    private static HtmlDocument LoadSample(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", fileName);
        Assert.True(File.Exists(path), $"Sample file not found: {path}");

        var doc = new HtmlDocument();
        doc.Load(path);
        return doc;
    }
}
