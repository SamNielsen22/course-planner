using System.Net;

namespace Web.Tests;

/// <summary>
/// The schedule home, where "Schedule Builder" leads. Accounts are off in
/// this host, so what is covered is the guest's path: their one schedule,
/// and the way in. The signed-in list needs Google and Postgres.
/// </summary>
public class SchedulesTests(Site site) : IClassFixture<Site>
{
    [Fact]
    public async Task TheNavigationLeadsToTheScheduleHome()
    {
        var doc = await site.Visitor().Page("/");
        var link = doc.DocumentNode.SelectSingleNode("//a[@class='btn' and contains(., 'Schedule Builder')]");
        Assert.Equal("/schedules", link?.GetAttributeValue("href", ""));
        var nav = (await site.Visitor().Page("/courses")).DocumentNode.SelectSingleNode("//nav//a[contains(., 'Schedule Builder')]");
        Assert.Equal("/schedules", nav?.GetAttributeValue("href", ""));
    }

    [Fact]
    public async Task AGuestWithNothingGetsOneButtonThatAsksForTheCampus()
    {
        var doc = await site.Visitor().Page("/schedules");
        var button = doc.DocumentNode.SelectSingleNode("//div[@class='center']/button[@class='btn']");
        Assert.Equal("Continue as guest", button?.InnerText.Trim());
        Assert.Null(doc.DocumentNode.SelectSingleNode("//ul[@class='sched-list']"));
        Assert.Null(doc.DocumentNode.SelectSingleNode("//dialog[@id='create-dialog']"));   // the create dialog is for accounts
        Assert.DoesNotContain("No schedules yet", doc.DocumentNode.InnerText);
        // The button opens a choice of campus, main first, that posts to the guest handler.
        Assert.Equal("main", doc.DocumentNode.SelectSingleNode("//dialog[@id='guest-dialog']//select[@name='campus']/option[1]")?.GetAttributeValue("value", ""));
        Assert.Contains("handler=Guest", doc.DocumentNode.SelectSingleNode("//dialog[@id='guest-dialog']//form")?.GetAttributeValue("action", ""));
        // No Google configured in this host, so no sign-in is offered.
        Assert.DoesNotContain("to save your schedule", doc.DocumentNode.InnerText);
    }

    /// <summary>The guest dialog's form, posted as the page would post it.</summary>
    private static async Task<HttpResponseMessage> ContinueAsGuest(Browser visitor, string campus, string? term = null)
    {
        var token = System.Text.RegularExpressions.Regex.Match(await visitor.Get("/schedules"), @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
        var fields = new List<KeyValuePair<string, string>> { new("campus", campus), new("__RequestVerificationToken", token) };
        if (term is not null) fields.Add(new("term", term));
        var request = new HttpRequestMessage(HttpMethod.Post, "/schedules?handler=Guest") { Content = new FormUrlEncodedContent(fields) };
        return await visitor.Client.SendAsync(request);
    }

    [Fact]
    public async Task AGuestCanPickTheTermOnTheWayIn()
    {
        var newest = site.Catalogue.NewestTerm();
        var older = site.Catalogue.One("term <> @term AND times LIKE '%/%'", new { term = newest }).Term;
        var visitor = site.Visitor();
        // The dialog offers every term, newest first and so by default.
        var home = await visitor.Page("/schedules");
        Assert.Equal(newest, home.DocumentNode.SelectSingleNode("//dialog[@id='guest-dialog']//select[@name='term']/option[@selected]")?.GetAttributeValue("value", ""));
        await ContinueAsGuest(visitor, "main", older);
        Assert.Contains("Browsing " + Pages.Terms.Display(older), (await visitor.Page("/builder")).DocumentNode.SelectSingleNode("//p[@class='page-sub']")?.InnerText);
        // Their term is what the dialog offers next time; a class added stays with it.
        Assert.Equal(older, (await visitor.Page("/schedules")).DocumentNode.SelectSingleNode("//dialog[@id='guest-dialog']//select[@name='term']/option[@selected]")?.GetAttributeValue("value", ""));
        await visitor.Add(site.Catalogue.Timed(older));
        await ContinueAsGuest(visitor, "main", older);
        Assert.Single(await visitor.Cart());
        // Another term starts afresh: one schedule is one term.
        await ContinueAsGuest(visitor, "main", newest);
        Assert.Empty(await visitor.Cart());
        Assert.Contains("Browsing " + Pages.Terms.Display(newest), (await visitor.Page("/builder")).DocumentNode.SelectSingleNode("//p[@class='page-sub']")?.InnerText);
    }

    [Fact]
    public async Task AGuestWhoPicksTheAsiaCampusBuildsThereUntilTheyPickAnother()
    {
        var visitor = site.Visitor();
        // The newest term the Asia Campus list is published for; the main list goes up first.
        var term = site.Catalogue.NewestTermOn("uac");
        var response = await ContinueAsGuest(visitor, "uac", term);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/builder", response.Headers.Location?.ToString());
        Assert.Contains("Asia Campus", (await visitor.Page("/builder")).DocumentNode.SelectSingleNode("//p[@class='page-sub']")?.InnerText);
        var asia = site.Catalogue.OnAsiaCampus(term);
        await visitor.Add(asia);
        Assert.Equal([asia.Code], await visitor.Cart());
        // Back on the home page the dialog offers their campus first; choosing the same one keeps the schedule.
        Assert.Equal("uac", (await visitor.Page("/schedules")).DocumentNode.SelectSingleNode("//dialog[@id='guest-dialog']//select[@name='campus']/option[@selected]")?.GetAttributeValue("value", ""));
        await ContinueAsGuest(visitor, "uac");
        Assert.Equal([asia.Code], await visitor.Cart());
        // Choosing another starts afresh: one schedule is one campus.
        await ContinueAsGuest(visitor, "main");
        Assert.Empty(await visitor.Cart());
        Assert.DoesNotContain("Asia Campus", (await visitor.Page("/builder")).DocumentNode.SelectSingleNode("//p[@class='page-sub']")?.InnerText);
    }

    [Fact]
    public async Task AGuestWithAClassAddedStillGetsJustTheButton()
    {
        var visitor = site.Visitor();
        await visitor.Add(site.Catalogue.Timed(site.Catalogue.NewestTerm()));
        var doc = await visitor.Page("/schedules");
        // Their schedule lives on in the builder, but is never listed here as one they made.
        Assert.Equal("Continue as guest", doc.DocumentNode.SelectSingleNode("//div[@class='center']/button[@class='btn']")?.InnerText.Trim());
        Assert.Null(doc.DocumentNode.SelectSingleNode("//li[contains(@class,'sched-row')]"));
        Assert.Null(doc.DocumentNode.SelectSingleNode("//div[@class='sched-bar']"));
        Assert.Single(await visitor.Cart());
    }

    [Fact]
    public async Task AnOnlineScheduleHasNoHoursGridOrCalendar()
    {
        var visitor = site.Visitor();
        await ContinueAsGuest(visitor, "online");
        var term = site.Catalogue.NewestTerm();
        var online = site.Catalogue.OnUOnline(term);
        // The card names no meeting time: there never is one.
        var cards = await visitor.Page($"/builder?term={term}&q={Uri.EscapeDataString(online.Code)}&open=false");
        Assert.NotNull(cards.DocumentNode.SelectSingleNode("//button[contains(@class,'add-section')]"));
        Assert.Null(cards.DocumentNode.SelectSingleNode("//p[@class='sc-when']"));
        Assert.Null(cards.DocumentNode.SelectSingleNode("//div[contains(@class,'cart-breaks')]"));   // nor breaks to keep free
        await visitor.Add(online);
        // The schedule page is the list alone: no week, no conflicts, no hours, no calendar file.
        var page = await visitor.Page("/BuilderSchedule");
        Assert.Contains(online.Code, page.DocumentNode.InnerText);
        Assert.Null(page.DocumentNode.SelectSingleNode("//div[@class='week']"));
        Assert.Null(page.DocumentNode.SelectSingleNode("//th[contains(., 'time')]"));
        Assert.Null(page.DocumentNode.SelectSingleNode("//a[@id='ics-button']"));
        Assert.DoesNotContain("Calendar export", page.DocumentNode.InnerText);
    }

    [Fact]
    public async Task TheScheduleHomeIsPersonalAndNotCached()
    {
        Assert.Contains("no-store", (await site.Visitor().Send("/schedules")).Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task AGuestPostingToTheAccountOnlyHandlersIsSimplyRedirected()
    {
        var visitor = site.Visitor();
        // A guest's schedule page has no form, so the token comes from the builder, as any page's would.
        var page = await visitor.Get("/builder");
        var token = System.Text.RegularExpressions.Regex.Match(page, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
        var request = new HttpRequestMessage(HttpMethod.Post, "/schedules?handler=New")
        {
            Content = new FormUrlEncodedContent([new("name", "Plan B"), new("__RequestVerificationToken", token)]),
        };
        var response = await visitor.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/builder", response.Headers.Location?.ToString());
    }
}
