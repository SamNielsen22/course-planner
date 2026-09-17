using System.Net;
using HtmlAgilityPack;

namespace Web.Tests;

/// <summary>The public pages: they render, they find things, and they cache the way they should.</summary>
public class PagesTests(Site site) : IClassFixture<Site>
{
    [Fact]
    public async Task HomeRenders()
    {
        var html = await site.Visitor().Get("/");
        Assert.Contains("Utah Course Compass", html);
        Assert.Contains("Schedule Builder", html);
    }

    [Fact]
    public async Task CourseSearchListsTheSubject()
    {
        var (subject, _, _) = site.Catalogue.GradedCourse();
        var doc = await site.Visitor().Page("/courses?q=" + Uri.EscapeDataString(subject));
        var codes = doc.DocumentNode.SelectNodes("//span[@class='course-code']")?.Select(n => n.InnerText.Trim()).ToList() ?? [];
        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.StartsWith(subject + " ", code));
    }

    [Fact]
    public async Task ANumberTypedInArabicFindsATitleNumberedInRoman()
    {
        // Course search: "calculus 1" lists Calculus I.
        var doc = await site.Visitor().Page("/courses?q=" + Uri.EscapeDataString("calculus 1"));
        var titles = doc.DocumentNode.SelectNodes("//span[@class='course-title']")?.Select(n => n.InnerText.Trim()).ToList() ?? [];
        Assert.Contains("Calculus I", titles);
        // And the builder, which searches in memory: the same words offer Calculus I sections.
        var term = site.Catalogue.NewestTerm();
        var cards = await site.Visitor().Page($"/builder?term={term}&q=" + Uri.EscapeDataString("calculus 1") + "&open=false&noClash=false");
        Assert.Contains(cards.DocumentNode.SelectNodes("//span[@class='sc-title']")?.Select(n => n.InnerText.Trim()) ?? [], t => t == "Calculus I");
    }

    [Fact]
    public async Task ALabThatIsItsOwnCourseStillShowsInSearch()
    {
        // A lab with no lecture in its course is the course, so it is not hidden.
        var term = site.Catalogue.NewestTerm();
        var lab = site.Catalogue.StandaloneLab(term);
        var doc = await site.Visitor().Page($"/builder?term={term}&q=" + Uri.EscapeDataString(lab.Code) + "&open=false&noClash=false");

        var sections = doc.DocumentNode.SelectNodes("//span[@class='sc-section']")?.Select(n => n.InnerText.Trim()).ToList() ?? [];
        Assert.Contains(sections, t => t.EndsWith(lab.Number2));
    }

    // The "no pairing published, so every section is offered" path is covered by
    // the unit test CompanionTests.ChoicesForOffersEveryCompanionWhenNothingIsPublished.
    // It has no builder integration test because the only term with unpaired courses
    // (Spring 2027) is hidden from the builder, and the visible term is fully paired.

    [Fact]
    public async Task CoursePageShowsTheRegistrarsAllTermsFigures()
    {
        var (subject, number, _) = site.Catalogue.GradedCourse();
        var doc = await site.Visitor().Page($"/course/{Uri.EscapeDataString(subject)}/{number}");
        Assert.NotNull(doc.DocumentNode.SelectSingleNode("//p[@class='subtitle']"));
        var values = doc.DocumentNode.SelectNodes("//span[@class='stat-value']")?.Select(n => n.InnerText.Trim()).ToList() ?? [];
        Assert.Equal(6, values.Count);                       // students, average, three quartiles, deviation
        Assert.All(values, v => Assert.NotEqual("—", v));    // the all-terms row is published, so none is blank
    }

    [Fact]
    public async Task CoursePageHidesTheSectionPickerForTheBuilder()
    {
        var (subject, number, term) = site.Catalogue.GradedCourse();
        var url = $"/course/{Uri.EscapeDataString(subject)}/{number}?term={term}";
        var fromSearch = await site.Visitor().Page(url);
        var fromBuilder = await site.Visitor().Page(url + "&from=builder");
        Assert.NotNull(fromSearch.DocumentNode.SelectSingleNode("//select[@name='section']"));
        Assert.Null(fromBuilder.DocumentNode.SelectSingleNode("//select[@name='section']"));
    }

    [Fact]
    public async Task ProfessorSearchFindsByName()
    {
        var name = site.Catalogue.AnInstructorName();
        var family = name[..name.IndexOf(',')];
        var doc = await site.Visitor().Page("/Instructors?handler=Rows&q=" + Uri.EscapeDataString(family));
        var names = doc.DocumentNode.SelectNodes("//a[@class='prof-name']")?.Select(n => n.InnerText.Trim()).ToList() ?? [];
        Assert.NotEmpty(names);
        Assert.Contains(names, n => n.Contains(family, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AboutRenders()
    {
        var html = await site.Visitor().Get("/About");
        // The prose is the user's to write; the page is the heading and the coverage figures.
        Assert.Contains("<h1>About</h1>", html);
        Assert.Contains("Graded sections", html);
    }

    [Fact]
    public async Task AccountPagesAnswer404WhenAccountsAreOff()
    {
        var visitor = site.Visitor();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.Send("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.Send("/account/login")).StatusCode);
    }

    [Fact]
    public async Task PublicPagesCacheAndPersonalPagesDoNot()
    {
        var (subject, number, _) = site.Catalogue.GradedCourse();
        var visitor = site.Visitor();
        Assert.Contains("max-age=60", (await visitor.Send($"/course/{Uri.EscapeDataString(subject)}/{number}")).Headers.CacheControl?.ToString());
        Assert.Contains("no-store", (await visitor.Send("/builder")).Headers.CacheControl?.ToString());
        Assert.Contains("no-store", (await visitor.Send("/BuilderSchedule")).Headers.CacheControl?.ToString());
    }
}
