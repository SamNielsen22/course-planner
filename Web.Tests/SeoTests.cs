using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Web.Tests;

/// <summary>What a search engine sees: the sitemaps, the canonical addresses, the descriptions, and the pages kept out.</summary>
public class SeoTests(Site site) : IClassFixture<Site>
{
    private static int Locs(string xml) => Regex.Matches(xml, "<loc>").Count;

    [Fact]
    public async Task TheSitemapIndexNamesFourSitemapsOnTheRequestsOwnHost()
    {
        var response = await site.Visitor().Send("/sitemap.xml");
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        var xml = await response.Content.ReadAsStringAsync();
        Assert.Contains("<sitemapindex", xml);
        Assert.Equal(4, Locs(xml));
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

        // And a page for every professor-and-class pair with grades.
        var classes = await visitor.Get("/sitemap-classes.xml");
        Assert.Equal(site.Catalogue.GradedClassCount(), Locs(classes));
        Assert.Matches(new Regex(@"<loc>http://localhost/professor/u\d+/[a-z0-9-]+/[a-z][a-z-]*-\d{3,4}[a-z]?</loc>"), classes);
    }

    [Fact]
    public async Task AProfessorsClassHasItsOwnAddressTitleAndDescription()
    {
        var p = site.Catalogue.ProfessorWithMultiSectionClasses();
        var slug = Pages.Names.CourseSlug(p.ClassA);
        var doc = await site.Visitor().Page($"/professor/{p.Unid}/anyone/{slug}");
        Assert.Contains(p.ClassA, doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", ""));
        Assert.Contains(p.ClassA, doc.DocumentNode.SelectSingleNode("//p[@class='page-sub']")?.InnerText);
        Assert.Contains(p.ClassA, doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", ""));
        // The tab and the search headline are the plain name and class; a personal page reads the site's name.
        Assert.Contains(p.ClassA, doc.DocumentNode.SelectSingleNode("//title")?.InnerText);
        Assert.DoesNotContain("Utah Course Compass", doc.DocumentNode.SelectSingleNode("//title")?.InnerText);
        Assert.Equal("Utah Course Compass", (await site.Visitor().Page("/builder")).DocumentNode.SelectSingleNode("//title")?.InnerText.Trim());
        Assert.EndsWith("/" + slug, doc.DocumentNode.SelectSingleNode("//link[@rel='canonical']")?.GetAttributeValue("href", ""));
        Assert.Equal(p.ClassA, doc.DocumentNode.SelectSingleNode("//select[@name='course']/option[@selected]")?.GetAttributeValue("value", ""));
        // Picked from the dropdown instead, the same class still claims the same address.
        var viaQuery = await site.Visitor().Page($"/professor/{p.Unid}?course=" + Uri.EscapeDataString(p.ClassA));
        Assert.EndsWith("/" + slug, viaQuery.DocumentNode.SelectSingleNode("//link[@rel='canonical']")?.GetAttributeValue("href", ""));
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

        var robots = await visitor.Get("/robots.txt");
        Assert.Contains("Disallow: /", robots);
        Assert.DoesNotContain("Sitemap:", robots);
        Assert.DoesNotContain("Allow:", robots);

        foreach (var path in new[] { "/sitemap.xml", "/sitemap-pages.xml", "/sitemap-courses.xml", "/sitemap-professors.xml", "/sitemap-classes.xml" })
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.Send(path)).StatusCode);

        // Every page asks not to be indexed, the public ones included.
        foreach (var path in new[] { "/", "/courses", "/Instructors" })
            Assert.NotNull((await visitor.Page(path)).DocumentNode.SelectSingleNode("//meta[@name='robots'][@content='noindex']"));
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
