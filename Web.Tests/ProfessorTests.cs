using System.Net;
using HtmlAgilityPack;

namespace Web.Tests;

/// <summary>The professor page's dropdowns - term, class, and the section picker - and the sections behind them, all in the page.</summary>
public class ProfessorTests(Site site) : IClassFixture<Site>
{
    private static string Chosen(HtmlDocument doc, string select) =>
        doc.DocumentNode.SelectSingleNode($"//select[@name='{select}']/option[@selected]")?.GetAttributeValue("value", "") ?? "";

    private static List<string> Options(HtmlDocument doc, string select) =>
        doc.DocumentNode.SelectNodes($"//select[@name='{select}']/option")?.Select(o => o.GetAttributeValue("value", "")).ToList() ?? [];

    private static string Url(string unid, string? term = null, string? course = null, string? section = null)
    {
        var q = new List<string>();
        if (term is not null) q.Add("term=" + Uri.EscapeDataString(term));
        if (course is not null) q.Add("course=" + Uri.EscapeDataString(course));
        if (section is not null) q.Add("section=" + Uri.EscapeDataString(section));
        return $"/professor/{unid}" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
    }

    [Fact]
    public async Task PickerListsEveryGradedSectionOfTheClass()
    {
        var p = site.Catalogue.ProfessorWithMultiSectionClasses();
        var doc = await site.Visitor().Page(Url(p.Unid, p.Term, p.ClassA));
        Assert.Equal(p.Term, Chosen(doc, "term"));
        Assert.Equal(p.ClassA, Chosen(doc, "course"));
        Assert.Equal(p.SectionsA, Options(doc, "section").Count);
    }

    [Fact]
    public async Task EverySectionIsInThePageAndOnlyTheChosenOneShows()
    {
        var p = site.Catalogue.ProfessorWithMultiSectionClasses();
        var doc = await site.Visitor().Page(Url(p.Unid, p.Term, p.ClassA));
        var blocks = doc.DocumentNode.SelectNodes("//div[@class='prof-section']")?.ToList() ?? [];
        // At least both classes' sections in this term; every other term's too.
        Assert.True(blocks.Count >= p.SectionsA + p.SectionsB, $"only {blocks.Count} blocks");
        var showing = blocks.Where(b => !b.Attributes.Contains("hidden")).ToList();
        Assert.Single(showing);
        Assert.Equal(Chosen(doc, "section"), showing[0].GetAttributeValue("data-key", ""));
        // Each block carries its own chart and the six published figures.
        Assert.Equal(blocks.Count, doc.DocumentNode.SelectNodes("//div[@class='prof-section']//div[@class='stat-strip']")?.Count);
        // And the pickers' data is in the page for the scripts.
        Assert.Contains(p.ClassA, doc.DocumentNode.SelectSingleNode("//script[@id='prof-data']")?.InnerText);
    }

    [Fact]
    public async Task SwitchingClassRebuildsThePickerAndIgnoresTheStaleSection()
    {
        var p = site.Catalogue.ProfessorWithMultiSectionClasses();
        var first = await site.Visitor().Page(Url(p.Unid, p.Term, p.ClassA));
        var stale = Chosen(first, "section");
        // The browser submits the old class's section key along with the new class.
        var doc = await site.Visitor().Page(Url(p.Unid, p.Term, p.ClassB, stale));
        Assert.Equal(p.ClassB, Chosen(doc, "course"));
        Assert.Equal(p.SectionsB, Options(doc, "section").Count);
        var chosen = Chosen(doc, "section");
        Assert.StartsWith($"{p.Term}|{p.ClassB.Replace(' ', '|')}|", chosen);
        Assert.NotEqual(stale, chosen);
    }

    [Fact]
    public async Task AChosenSectionStaysChosen()
    {
        var p = site.Catalogue.ProfessorWithMultiSectionClasses();
        var first = await site.Visitor().Page(Url(p.Unid, p.Term, p.ClassA));
        var second = Options(first, "section")[1];
        var doc = await site.Visitor().Page(Url(p.Unid, p.Term, p.ClassA, second));
        Assert.Equal(second, Chosen(doc, "section"));
    }

    [Fact]
    public async Task AClassInThePathIsSentToThePersonsPageOnThatClass()
    {
        var p = site.Catalogue.ProfessorWithMultiSectionClasses();
        var slug = Pages.Names.CourseSlug(p.ClassA);
        // Each class once had an address of its own; it now names the class on the person's page.
        var response = await site.Visitor().Client.GetAsync($"/professor/{p.Unid}/anyone/{slug}?term={Uri.EscapeDataString(p.Term)}");
        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Matches(new System.Text.RegularExpressions.Regex($"^/professor/{p.Unid}/[a-z0-9-]+#{slug}$"), response.Headers.Location?.ToString());
        // The pickers rebuild the address from the form's action, which is the person's own.
        var page = await site.Visitor().Page(Url(p.Unid, p.Term, p.ClassA));
        var action = page.DocumentNode.SelectSingleNode("//form[contains(@class,'inline-form')]")?.GetAttributeValue("action", "");
        Assert.Matches(new System.Text.RegularExpressions.Regex($"^/professor/{p.Unid}/[a-z0-9-]+$"), action);
        var switched = await site.Visitor().Page($"{action}?term={Uri.EscapeDataString(p.Term)}&course={Uri.EscapeDataString(p.ClassB)}");
        Assert.Equal(p.ClassB, Chosen(switched, "course"));
        Assert.Equal(p.SectionsB, Options(switched, "section").Count);
    }

    [Fact]
    public async Task PickerIsHiddenForAClassWithOneSection()
    {
        var (unid, term, cls) = site.Catalogue.ProfessorWithSingleSectionClass();
        var doc = await site.Visitor().Page(Url(unid, term, cls));
        Assert.Equal(cls, Chosen(doc, "course"));
        Assert.Null(doc.DocumentNode.SelectSingleNode("//select[@name='section']"));
        Assert.NotNull(doc.DocumentNode.SelectSingleNode("//div[@class='prof-section' and not(@hidden)]//div[@class='stat-strip']"));
    }

    [Fact]
    public async Task ASummerIsNotTheDefaultForSomeoneWhoUsuallyTeachesFallAndSpring()
    {
        var p = site.Catalogue.ProfessorWhoseLatestTermIsSummer();
        // Planning a fall or spring - or nothing in particular - lands on their latest fall or spring term.
        Assert.Equal(p.LatestRegularTerm, Chosen(await site.Visitor().Page(Url(p.Unid)), "term"));
        Assert.Equal(p.LatestRegularTerm, Chosen(await site.Visitor().Page(Url(p.Unid) + "?planning=Fall2026"), "term"));
        // Planning a summer, their summer is what matters.
        Assert.Equal(p.SummerTerm, Chosen(await site.Visitor().Page(Url(p.Unid) + "?planning=Summer2026"), "term"));
        // Asked for outright, the summer term shows like any other.
        Assert.Equal(p.SummerTerm, Chosen(await site.Visitor().Page(Url(p.Unid, p.SummerTerm)), "term"));
        // The builder's cards say which term is being planned, and name the class.
        var term = site.Catalogue.NewestTerm();
        var cards = await site.Visitor().Page($"/builder?term={term}&open=false&noClash=false");
        var link = cards.DocumentNode.SelectSingleNode("//a[contains(@href,'/professor/')]")?.GetAttributeValue("href", "");
        Assert.Contains("planning=" + term, link);
        Assert.Matches(@"#[a-z0-9-]+$", link);
    }

    [Fact]
    public async Task AProfessorWithNoPublishedGradesGetsANoteAndNoPickers()
    {
        var doc = await site.Visitor().Page(Url(site.Catalogue.ProfessorWithoutGrades()));
        Assert.Null(doc.DocumentNode.SelectSingleNode("//select"));
        Assert.Contains("No published grades for this professor", doc.DocumentNode.SelectSingleNode("//p[@class='note']")?.InnerText);
        Assert.Null(doc.DocumentNode.SelectSingleNode("//div[@class='stat-strip']"));
    }

    [Fact]
    public async Task ATileLinkLandsOnTheTermWhereThatClassHasGrades()
    {
        var p = site.Catalogue.ProfessorWithMultiSectionClasses();
        // Only the class, as a search tile links it: the term is chosen for it.
        var doc = await site.Visitor().Page(Url(p.Unid, course: p.ClassA));
        Assert.Equal(p.ClassA, Chosen(doc, "course"));
        Assert.NotEqual("", Chosen(doc, "term"));
    }
}
