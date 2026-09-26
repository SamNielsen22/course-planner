using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Web.Tests;

/// <summary>What a search engine sees: the sitemaps, the canonical addresses, the descriptions, and the pages kept out.</summary>
public class SeoTests(Site site) : IClassFixture<Site>
{
    private static int Locs(string xml) => Regex.Matches(xml, "<loc>").Count;

    [Fact]
    public async Task TheSitemapIndexNamesThreeSitemapsOnTheRequestsOwnHost()
    {
        var response = await site.Visitor().Send("/sitemap.xml");
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        var xml = await response.Content.ReadAsStringAsync();
        Assert.Contains("<sitemapindex", xml);
        Assert.Equal(3, Locs(xml));
        Assert.Contains("<loc>http://localhost/sitemap-courses.xml</loc>", xml);
    }

    [Fact]
    public async Task EveryCourseAndEveryProfessorIsInTheSitemap()
    {
        var visitor = site.Visitor();
        var courses = await visitor.Get("/sitemap-courses.xml");
        Assert.Equal(site.Catalogue.CourseCount(), Locs(courses));
        var (subject, number, _) = site.Catalogue.GradedCourse();
        Assert.Contains($"<loc>http://localhost/course/{Uri.EscapeDataString(subject)}/{number}</loc>", courses);

        var professors = await visitor.Get("/sitemap-professors.xml");
        Assert.Equal(site.Catalogue.InstructorCount(), Locs(professors));
        Assert.Matches(new Regex(@"<loc>http://localhost/professor/u\d+/[a-z0-9-]+</loc>"), professors);
        // A professor's classes live on their page, not at addresses of their own.
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.Send("/sitemap-classes.xml")).StatusCode);
    }

    [Fact]
    public async Task AProfessorsPageNamesTheirClassesAndHasOneAddress()
    {
        var p = site.Catalogue.ProfessorWithMultiSectionClasses();
        var doc = await site.Visitor().Page($"/professor/{p.Unid}");

        // The title and description name the classes, so a search for the person or
        // for a class finds the one page. The most-taught class leads and is always named.
        var title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "";
        Assert.EndsWith("grades", title);
        var lead = Regex.Match(title, @"[A-Z][A-Z ]*? \d{3,4}[A-Z]?").Value;
        Assert.False(string.IsNullOrEmpty(lead), $"no class named in the title: {title}");
        Assert.Contains(lead!, doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", ""));
        Assert.Contains(lead!, doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", ""));

        // A personal page's tab reads the plain name; the site's name is for the site's own pages.
        Assert.DoesNotContain("Utah Course Compass", title);
        Assert.Equal("Utah Course Compass", (await site.Visitor().Page("/builder")).DocumentNode.SelectSingleNode("//title")?.InnerText.Trim());

        // One canonical address for the person, whatever the visitor typed to get here.
        var canonical = doc.DocumentNode.SelectSingleNode("//link[@rel='canonical']")?.GetAttributeValue("href", "") ?? "";
        Assert.Matches(new Regex($"^http://localhost/professor/{p.Unid}/[a-z0-9-]+$"), canonical);
    }

    [Fact]
    public async Task BehindTheTunnelTheSitemapUsesTheSitesOwnDomain()
    {
        var response = await site.Visitor().Send("/sitemap-pages.xml", ("X-Forwarded-Proto", "https"), ("Host", "utahcoursecompass.com"));
        Assert.Contains("<loc>https://utahcoursecompass.com/</loc>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RobotsPointsAtTheSitemapAndKeepsOutThePersonalPages()
    {
        var robots = await site.Visitor().Send("/robots.txt", ("X-Forwarded-Proto", "https"), ("Host", "utahcoursecompass.com"));
        var text = await robots.Content.ReadAsStringAsync();
        Assert.Contains("Sitemap: https://utahcoursecompass.com/sitemap.xml", text);
        Assert.Contains("Disallow: /builder", text);
        Assert.Contains("Disallow: /account", text);
    }

    [Fact]
    public async Task UnlistedTheSiteRefusesEveryCrawlerAndServesNoSitemap()
    {
        // The shipped default: the site answers anyone with the address, but
        // asks to stay out of search results.
        using var hidden = new Site { Unlisted = true };
        var visitor = hidden.Visitor();

        // Only the front page and About may be read, so a search on the name finds the site.
        var robots = await visitor.Get("/robots.txt");
        Assert.Equal("User-agent: *\nAllow: /$\nAllow: /About\nDisallow: /\n", robots);

        foreach (var path in new[] { "/sitemap.xml", "/sitemap-pages.xml", "/sitemap-courses.xml", "/sitemap-professors.xml", "/sitemap-classes.xml" })
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.Send(path)).StatusCode);

        // Every other page asks not to be indexed, the public ones included.
        foreach (var path in new[] { "/courses", "/Instructors", "/course/CS/2420" })
            Assert.NotNull((await visitor.Page(path)).DocumentNode.SelectSingleNode("//meta[@name='robots'][@content='noindex']"));
        foreach (var path in new[] { "/", "/About" })
            Assert.Null((await visitor.Page(path)).DocumentNode.SelectSingleNode("//meta[@name='robots']"));
    }

    [Fact]
    public async Task ACoursePageDescribesItselfWithItsFigures()
    {
        var (subject, number, term) = site.Catalogue.GradedCourse();
        var doc = await site.Visitor().Page($"/course/{Uri.EscapeDataString(subject)}/{number}?term={term}");
        var description = doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", "");
        Assert.Contains($"{subject} {number}", description);
        Assert.Contains("average GPA", description);
        Assert.Contains("University of Utah", description);
        // One address for the course, whatever term the page is showing.
        Assert.Equal($"http://localhost/course/{Uri.EscapeDataString(subject)}/{number}",
            doc.DocumentNode.SelectSingleNode("//link[@rel='canonical']")?.GetAttributeValue("href", ""));
        Assert.Contains($"{subject} {number}", doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", ""));
        Assert.Null(doc.DocumentNode.SelectSingleNode("//meta[@name='robots']"));
    }

    [Fact]
    public async Task AProfessorAnswersOnANamedAddressAndDescribesTheirGrading()
    {
        var (unid, name) = site.Catalogue.AnInstructor();
        var slug = Pages.Names.Slug(name);
        var doc = await site.Visitor().Page($"/professor/{unid}/{slug}");
        Assert.Contains(Pages.Names.Natural(name), doc.DocumentNode.SelectSingleNode("//h1")?.InnerText);
        Assert.Equal($"http://localhost/professor/{unid}/{slug}",
            doc.DocumentNode.SelectSingleNode("//link[@rel='canonical']")?.GetAttributeValue("href", ""));
        var description = doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", "");
        Assert.Contains("University of Utah", description);
        Assert.Contains(Pages.Names.Natural(name), description);
        // The slug is for reading; the uNID is the key, so a wrong slug still lands.
        var wrongSlug = await site.Visitor().Send($"/professor/{unid}/someone-else");
        Assert.Equal(HttpStatusCode.OK, wrongSlug.StatusCode);
    }

    [Fact]
    public async Task SearchCardsLinkToTheNamedAddress()
    {
        var name = site.Catalogue.AnInstructorName();
        var family = name[..name.IndexOf(',')];
        var doc = await site.Visitor().Page("/Instructors?handler=Rows&q=" + Uri.EscapeDataString(family));
        var href = doc.DocumentNode.SelectSingleNode("//a[@class='prof-name']")?.GetAttributeValue("href", "");
        Assert.Matches(new Regex(@"^/professor/u\d+/[a-z0-9-]+$"), href);
    }

    [Fact]
    public async Task ThePersonalPagesAreKeptOutOfTheIndex()
    {
        foreach (var url in new[] { "/builder", "/BuilderSchedule", "/schedules" })
        {
            var doc = await site.Visitor().Page(url);
            Assert.Equal("noindex", doc.DocumentNode.SelectSingleNode("//meta[@name='robots']")?.GetAttributeValue("content", ""));
        }
    }
}
