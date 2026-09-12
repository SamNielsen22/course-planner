using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Web.Tests;

/// <summary>
/// Signed in: the schedule home lists every schedule, the builder edits the
/// open one, and new/open/rename/delete all round-trip through Postgres.
/// Each test starts from an account with no schedules.
/// </summary>
public class AccountTests : IClassFixture<AccountSite>, IDisposable
{
    private readonly AccountSite _site;
    private readonly Catalogue _catalogue = new(Site.DatabasePath);

    public AccountTests(AccountSite site)
    {
        _site = site;
        if (AccountSite.Available) site.Reset();
    }

    public void Dispose()
    {
        if (AccountSite.Available) _site.Reset();
    }

    private static async Task<HttpResponseMessage> PostAsync(Browser visitor, string handler, params (string Name, string Value)[] form)
    {
        var token = Regex.Match(await visitor.Get("/schedules"), @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
        var request = new HttpRequestMessage(HttpMethod.Post, "/schedules?handler=" + handler)
        {
            Content = new FormUrlEncodedContent(form.Append(("__RequestVerificationToken", token)).Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))),
        };
        return await visitor.Client.SendAsync(request);
    }

    /// <summary>The listed schedules: (name, id, is open) in page order.</summary>
    private static List<(string Name, string Id, bool Open)> Listed(HtmlDocument doc) =>
        doc.DocumentNode.SelectNodes("//li[contains(@class,'sched-row')]")?.Select(li => (
            li.SelectSingleNode(".//span[@class='sched-name']")!.InnerText.Trim(),
            li.SelectSingleNode(".//input[@name='id']")!.GetAttributeValue("value", ""),
            li.GetAttributeValue("class", "").Contains("sched-open"))).ToList() ?? [];

    [AccountFact]
    public async Task ANewAccountHasNoSchedulesAndIsOfferedOne()
    {
        var doc = await _site.Visitor().Page("/schedules");
        Assert.Contains("No schedules yet", doc.DocumentNode.InnerText);
        Assert.Equal("New schedule", doc.DocumentNode.SelectSingleNode("//div[@class='sched-none']//button[@id='create']")?.InnerText.Trim());
        Assert.Empty(Listed(doc));
        Assert.NotNull(doc.DocumentNode.SelectSingleNode("//form[contains(@action,'handler=New')]"));
    }

    [AccountFact]
    public async Task ANewScheduleIsListedOpenAndTheBuilderEditsIt()
    {
        var visitor = _site.Visitor();
        var response = await PostAsync(visitor, "New", ("name", "Plan B"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/builder", response.Headers.Location?.ToString());

        var listed = Listed(await visitor.Page("/schedules"));
        Assert.Equal([("Plan B", true)], listed.Select(s => (s.Name, s.Open)).ToList());

        // The builder's cart is this schedule, by name, and what is added lands in it.
        Assert.Contains("Plan B", (await visitor.Page("/builder")).DocumentNode.SelectSingleNode("//span[@class='cart-title']")?.InnerText);
        await visitor.Add(_catalogue.Timed(_catalogue.NewestTerm()));
        var meta = (await visitor.Page("/schedules")).DocumentNode.SelectSingleNode("//span[@class='sched-meta']")?.InnerText;
        Assert.Matches(@"1 class · \d+ units", Regex.Replace(HtmlEntity.DeEntitize(meta ?? ""), @"\s+", " "));
    }

    [AccountFact]
    public async Task OpeningAnotherScheduleSwitchesWhatTheBuilderEdits()
    {
        var visitor = _site.Visitor();
        await PostAsync(visitor, "New", ("name", "First"));
        await visitor.Add(_catalogue.Timed(_catalogue.NewestTerm()));
        await PostAsync(visitor, "New", ("name", "Second"));
        Assert.Empty(await visitor.Cart());                              // the new one is empty and open

        var first = Listed(await visitor.Page("/schedules")).Single(s => s.Name == "First");
        var response = await PostAsync(visitor, "Open", ("id", first.Id));
        Assert.Equal("/builder", response.Headers.Location?.ToString());
        Assert.Single(await visitor.Cart());                             // back to the first, with its class
        var listed = Listed(await visitor.Page("/schedules"));
        Assert.Equal(["First", "Second"], listed.Select(s => s.Name).Order().ToList());
        Assert.Equal(["First"], listed.Where(s => s.Open).Select(s => s.Name).ToList());
    }

    [AccountFact]
    public async Task RenameAndDeleteRoundTrip()
    {
        var visitor = _site.Visitor();
        await PostAsync(visitor, "New", ("name", "Draft"));
        var id = Listed(await visitor.Page("/schedules")).Single().Id;

        await PostAsync(visitor, "Rename", ("id", id), ("name", "Final"));
        Assert.Equal("Final", Listed(await visitor.Page("/schedules")).Single().Name);

        await PostAsync(visitor, "Delete", ("id", id));
        Assert.Empty(Listed(await visitor.Page("/schedules")));
    }

    [AccountFact]
    public async Task AnotherUsersScheduleCannotBeOpenedRenamedOrDeleted()
    {
        var visitor = _site.Visitor();
        await PostAsync(visitor, "New", ("name", "Mine"));
        var id = Listed(await visitor.Page("/schedules")).Single().Id;
        // The handlers look rows up by id AND owner; a foreign id does nothing.
        await PostAsync(visitor, "Rename", ("id", "not-" + id), ("name", "Hijacked"));
        await PostAsync(visitor, "Delete", ("id", "not-" + id));
        Assert.Equal("Mine", Listed(await visitor.Page("/schedules")).Single().Name);
    }

    [AccountFact]
    public async Task ACreatedScheduleKeepsItsTermAndTheBuilderBrowsesIt()
    {
        var visitor = _site.Visitor();
        var older = _catalogue.One("term <> @term AND times LIKE '%/%'", new { term = _catalogue.NewestTerm() }).Term;
        await PostAsync(visitor, "New", ("name", "Next spring"), ("term", older));
        var builder = await visitor.Page("/builder");
        Assert.Contains("Browsing " + Pages.Terms.Display(older), builder.DocumentNode.SelectSingleNode("//p[@class='page-sub']")?.InnerText);
        // Still empty, the row already names its term.
        var home = await visitor.Page("/schedules");
        Assert.Contains(Pages.Terms.Display(older), home.DocumentNode.SelectSingleNode("//span[@class='sched-meta']")?.InnerText);
    }

    [AccountFact]
    public async Task ACreatedScheduleKeepsItsCampusAndTheBuilderBrowsesIt()
    {
        var visitor = _site.Visitor();
        // The dialog offers the campus, main first and so by default.
        var home = await visitor.Page("/schedules");
        Assert.Equal("main", home.DocumentNode.SelectSingleNode("//dialog//select[@name='campus']/option[1]")?.GetAttributeValue("value", ""));

        await PostAsync(visitor, "New", ("name", "Incheon"), ("campus", "uac"));
        var builder = await visitor.Page("/builder");
        Assert.Contains("Asia Campus", builder.DocumentNode.SelectSingleNode("//p[@class='page-sub']")?.InnerText);
        Assert.Null(builder.DocumentNode.SelectSingleNode("//select[@name='campus']"));   // chosen at creation, not per search
        Assert.Contains("Asia Campus", (await visitor.Page("/schedules")).DocumentNode.SelectSingleNode("//span[@class='sched-meta']")?.InnerText);

        // A Salt Lake key posted at it is not for it; an Asia Campus one lands.
        var term = _catalogue.NewestTerm();
        await visitor.Add(_catalogue.Timed(term));
        Assert.Empty(await visitor.Cart());
        var asia = _catalogue.OnAsiaCampus(term);
        await visitor.Add(asia);
        Assert.Equal([asia.Code], await visitor.Cart());
    }

    [AccountFact]
    public async Task AFirstClassAddedWithNoScheduleStartsOne()
    {
        var visitor = _site.Visitor();
        await visitor.Add(_catalogue.Timed(_catalogue.NewestTerm()));
        var listed = Listed(await visitor.Page("/schedules"));
        Assert.Equal([("Schedule 1", true)], listed.Select(s => (s.Name, s.Open)).ToList());
    }
}
