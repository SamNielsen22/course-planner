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
    public async Task AGuestWithNothingGetsOneButtonIntoTheBuilder()
    {
        var doc = await site.Visitor().Page("/schedules");
        var button = doc.DocumentNode.SelectSingleNode("//div[@class='center']/a[@class='btn']");
        Assert.Equal("Continue as guest", button?.InnerText.Trim());
        Assert.Equal("/builder", button?.GetAttributeValue("href", ""));
        Assert.Null(doc.DocumentNode.SelectSingleNode("//ul[@class='sched-list']"));
        Assert.Null(doc.DocumentNode.SelectSingleNode("//dialog"));           // the create dialog is for accounts
        Assert.DoesNotContain("No schedules yet", doc.DocumentNode.InnerText);
        // No Google configured in this host, so no sign-in is offered.
        Assert.DoesNotContain("Sign in with Google", doc.DocumentNode.InnerHtml);
    }

    [Fact]
    public async Task AGuestWithAScheduleInProgressIsToldSoAndContinues()
    {
        var term = site.Catalogue.NewestTerm();
        var visitor = site.Visitor();
        await visitor.Add(site.Catalogue.Timed(term));
        var doc = await visitor.Page("/schedules");
        Assert.Equal("Continue as guest", doc.DocumentNode.SelectSingleNode("//div[@class='sched-bar']//a[contains(@class,'btn')]")?.InnerText.Trim());
        // Their one schedule is listed with its term and its facts, and a way in.
        var row = doc.DocumentNode.SelectSingleNode("//li[contains(@class,'sched-row')]");
        var meta = System.Text.RegularExpressions.Regex.Replace(HtmlAgilityPack.HtmlEntity.DeEntitize(row?.SelectSingleNode(".//span[@class='sched-meta']")?.InnerText ?? ""), @"\s+", " ");
        Assert.Contains(Pages.Terms.Display(term), meta);
        Assert.Matches(@"1 class · \d+ units", meta);
        Assert.Equal("/builder", row?.SelectSingleNode(".//a[@class='sched-hit']")?.GetAttributeValue("href", ""));
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
