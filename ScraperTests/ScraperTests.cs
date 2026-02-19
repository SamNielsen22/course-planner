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

    private static HtmlDocument LoadSample(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", fileName);
        Assert.True(File.Exists(path), $"Sample file not found: {path}");

        var doc = new HtmlDocument();
        doc.Load(path);
        return doc;
    }
}
