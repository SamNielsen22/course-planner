using System.Net;
using System.Text;
using HtmlAgilityPack;
using Web.Pages;
using Web.Schedule;

namespace Web.Tests;

/// <summary>
/// The schedule builder end to end: add sections through the handler the
/// page's script uses, then read the cart, the week grid, the conflicts and
/// the calendar file the way a student would.
/// </summary>
public class ScheduleTests(Site site) : IClassFixture<Site>
{
    private string Term => site.Catalogue.NewestTerm();

    private static List<string> DayHeads(HtmlDocument doc) =>
        doc.DocumentNode.SelectNodes("//span[@class='day-short']")?.Select(n => n.InnerText.Trim()).ToList() ?? [];

    /// <summary>Block labels per day column, in the grid's order.</summary>
    private static Dictionary<string, List<string>> Blocks(HtmlDocument doc)
    {
        var heads = DayHeads(doc);
        var cols = doc.DocumentNode.SelectNodes("//div[@class='week']/div[@class='col']") ?? new HtmlNodeCollection(null);
        return heads.Zip(cols).ToDictionary(
            pair => pair.First,
            pair => pair.Second.SelectNodes(".//span[@class='block-label']")?.Select(n => n.InnerText.Trim()).ToList() ?? []);
    }

    private static List<string> Conflicts(HtmlDocument doc) =>
        doc.DocumentNode.SelectNodes("//ul[@class='clash-list']/li")?.Select(n => System.Text.RegularExpressions.Regex.Replace(n.InnerText, @"\s+", " ").Trim()).ToList() ?? [];

    [Fact]
    public async Task AnAddedSectionIsInTheCartAndOnTheGrid()
    {
        var s = site.Catalogue.OnWeekdaysOnly(Term);
        var visitor = site.Visitor();
        await visitor.Add(s);
        Assert.Equal([s.Code], await visitor.Cart());
        var doc = await visitor.Page("/BuilderSchedule");
        var blocks = Blocks(doc);
        Assert.Contains(s.Code, blocks["Tue"]);
        Assert.Contains(s.Code, blocks["Thu"]);
        Assert.DoesNotContain(s.Code, blocks["Mon"]);
        Assert.Equal(Terms.Display(Term), doc.DocumentNode.SelectSingleNode("//p[@class='page-sub']")?.InnerText.Trim());
    }

    /// <summary>The section numbers of one course the results offer to add.</summary>
    private static List<string> Offered(HtmlDocument doc, Catalogue.Section s) =>
        doc.DocumentNode.SelectNodes($"//button[contains(@class,'add-section') and @data-subject='{s.Subject}' and @data-number='{s.Number}']")
            ?.Select(b => b.GetAttributeValue("data-section", "")).ToList() ?? [];

    [Fact]
    public async Task AGuestBrowsesTheMainCampusUnlessTheyPickAnother()
    {
        var asia = site.Catalogue.OnAsiaCampus(Term);
        var visitor = site.Visitor();
        var search = $"/builder?term={Term}&q={Uri.EscapeDataString(asia.Code)}&open=false&noClash=false";

        // Its course, searched as a guest arrives: main campus, so the Incheon section is not offered.
        var main = await visitor.Page(search);
        Assert.Equal("main", main.DocumentNode.SelectSingleNode("//select[@name='campus']/option[@selected]")?.GetAttributeValue("value", ""));
        Assert.DoesNotContain(asia.Number2, Offered(main, asia));
        Assert.DoesNotContain("Asia Campus", main.DocumentNode.SelectSingleNode("//p[@class='page-sub']")?.InnerText);

        // Picked, the Asia Campus offers its own sections and none of Salt Lake's.
        var uac = await visitor.Page(search + "&campus=uac");
        Assert.Contains(asia.Number2, Offered(uac, asia));
        Assert.All(Offered(uac, asia), n => Assert.StartsWith("3", n));
        Assert.Contains("Asia Campus", uac.DocumentNode.SelectSingleNode("//p[@class='page-sub']")?.InnerText);

        // One schedule is one campus: a Salt Lake class already in it gives way.
        await visitor.Add(site.Catalogue.Timed(Term));
        await visitor.Add(asia);
        Assert.Equal([asia.Code], await visitor.Cart());
        // And the builder now browses the Asia Campus on its own.
        var again = await visitor.Page("/builder");
        Assert.Contains("Asia Campus", again.DocumentNode.SelectSingleNode("//p[@class='page-sub']")?.InnerText);
        Assert.Contains("Asia Campus", (await visitor.Page("/BuilderSchedule")).DocumentNode.SelectSingleNode("//p[@class='page-sub']")?.InnerText);
    }

    [Fact]
    public async Task ASaturdayClassGetsASaturdayColumnAndSitsOnlyThere()
    {
        var weekday = site.Visitor();
        await weekday.Add(site.Catalogue.OnWeekdaysOnly(Term));
        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri"], DayHeads(await weekday.Page("/BuilderSchedule")));

        var s = site.Catalogue.OnSaturday(Term);
        var visitor = site.Visitor();
        await visitor.Add(s);
        var blocks = Blocks(await visitor.Page("/BuilderSchedule"));
        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri", "Sat"], blocks.Keys.ToList());
        Assert.Contains(s.Code, blocks["Sat"]);
        Assert.All(blocks.Where(b => b.Key != "Sat"), b => Assert.DoesNotContain(s.Code, b.Value));
    }

    [Fact]
    public async Task ADayRangeLandsOnEveryDayInIt()
    {
        var s = site.Catalogue.WithDayRange(Term);   // "Mo-Th/..."
        var visitor = site.Visitor();
        await visitor.Add(s);
        var blocks = Blocks(await visitor.Page("/BuilderSchedule"));
        foreach (var day in new[] { "Mon", "Tue", "Wed", "Thu" }) Assert.Contains(s.Code, blocks[day]);
        Assert.DoesNotContain(s.Code, blocks["Fri"]);
    }

    [Fact]
    public async Task TwoSectionsOfOneCourseAtTheSameTimeAreReportedBySection()
    {
        var (a, b) = site.Catalogue.SameTimePair(Term);
        var visitor = site.Visitor();
        await visitor.Add(a);
        await visitor.Add(b);
        var lines = Conflicts(await visitor.Page("/BuilderSchedule"));
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.StartsWith($"{a.Code} section {a.Number2} and {b.Code} section {b.Number2} overlap on", l));
    }

    [Fact]
    public async Task BuildingCodesCarryTheirFullNames()
    {
        var s = site.Catalogue.InBuilding(Term, "WEB");
        var visitor = site.Visitor();
        await visitor.Add(s);
        var doc = await visitor.Page("/BuilderSchedule");
        var abbr = doc.DocumentNode.SelectSingleNode("//abbr[@class='building']");
        Assert.NotNull(abbr);
        Assert.Equal("WEB", abbr.InnerText.Trim());
        Assert.Contains("Warnock Engineering", abbr.GetAttributeValue("data-title", ""));
    }

    [Fact]
    public async Task OneTermPerSchedule()
    {
        var current = site.Catalogue.Timed(Term);
        var other = site.Catalogue.One("term <> @term AND times LIKE '%/%'", new { term = Term });
        var visitor = site.Visitor();
        await visitor.Add(current);
        await visitor.Add(other);
        Assert.Equal([other.Code], await visitor.Cart());   // the newer add starts a new schedule
        var doc = await visitor.Page("/BuilderSchedule");
        Assert.Equal(Terms.Display(other.Term), doc.DocumentNode.SelectSingleNode("//p[@class='page-sub']")?.InnerText.Trim());
    }

    [Fact]
    public async Task BreaksAreCappedSoTheCookieCannotOverflow()
    {
        var visitor = site.Visitor();
        for (var i = 0; i < 25; i++)
            Assert.Equal(HttpStatusCode.OK, await visitor.AddBreak(i == 0 ? new string('x', 100) : $"b{i}", "Mo", "12:00", "13:00"));
        var names = (await visitor.Page("/builder?handler=Cart")).DocumentNode
            .SelectNodes("//div[contains(@class,'cart-breaks')]//span[@class='cart-code']")!.Select(n => n.InnerText.Trim()).ToList();
        Assert.Equal(12, names.Count);
        Assert.Equal(40, names[0].Length);
    }

    [Fact]
    public async Task TheCalendarFileIsInlineAndWellFormed()
    {
        var range = site.Catalogue.WithDayRange(Term);
        var rooms = site.Catalogue.WithTwoRooms(Term);
        var online = site.Catalogue.Online(Term);
        var visitor = site.Visitor();
        foreach (var s in new[] { range, rooms, online }) await visitor.Add(s);

        var response = await visitor.Send("/BuilderSchedule?handler=Ics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(Terms.Display(Term).Replace(' ', '-') + "-classes.ics", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var ics = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.DoesNotContain("\n", ics.Replace("\r\n", ""));                       // CRLF only
        Assert.All(ics.Split("\r\n"), line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line));
        Assert.Contains("BEGIN:VTIMEZONE\r\nTZID:America/Denver", ics);
        Assert.DoesNotContain("DESCRIPTION:", ics);

        var events = ics.Split("BEGIN:VEVENT").Skip(1).ToList();
        var meetings = new[] { range, rooms, online }.Sum(s => Meetings.Parse(s.Times).Count);
        Assert.Equal(meetings, events.Count);

        // The day range recurs on every day in it, and skips the term's days off on those days.
        var dates = AcademicCalendar.For(Term)!;
        var rangeEvent = events.First(e => e.Contains($"SUMMARY:{range.Code}:"));
        var meeting = Meetings.Parse(range.Times)[0];
        var byDay = string.Join(",", meeting.DayCodes.Select(d => d.ToUpperInvariant()));
        Assert.Contains($"RRULE:FREQ=WEEKLY;BYDAY={byDay};UNTIL=", rangeEvent);
        var weekdays = meeting.DayCodes.Select(d => Array.IndexOf(Meeting.Week, d)).Select(i => (DayOfWeek)((i + 1) % 7)).ToHashSet();
        var first = dates.FirstDay;
        while (!weekdays.Contains(first.DayOfWeek)) first = first.AddDays(1);
        var expectedSkips = dates.DaysOff.Count(d => d >= first && d <= dates.LastDay && weekdays.Contains(d.DayOfWeek));
        Assert.Equal(expectedSkips, rangeEvent.Split("EXDATE;").Length - 1);

        // A two-room section: one event per meeting, each in its own room.
        var roomEvents = events.Where(e => e.Contains($"SUMMARY:{rooms.Code}:")).ToList();
        Assert.Equal(2, roomEvents.Count);
        var roomNumbers = rooms.Location!.Split(',').Select(r => r.Trim().Split(' ').Last()).ToList();
        Assert.Contains($"Room {roomNumbers[0]}", roomEvents[0].Replace("\r\n ", ""));
        Assert.Contains($"Room {roomNumbers[1]}", roomEvents[1].Replace("\r\n ", ""));

        // Online sections say so rather than printing CANVAS.
        Assert.Contains("LOCATION:Online", events.First(e => e.Contains($"SUMMARY:{online.Code}:")));
    }

    [Fact]
    public async Task TheCalendarFileLinksBackOverHttpsBehindTheTunnel()
    {
        var visitor = site.Visitor();
        await visitor.Add(site.Catalogue.Timed(Term));
        var behindTunnel = await visitor.Send("/BuilderSchedule?handler=Ics", ("X-Forwarded-Proto", "https"), ("Host", "utahcoursecompass.com"));
        Assert.Contains("URL:https://utahcoursecompass.com/course/", await behindTunnel.Content.ReadAsStringAsync());
        var direct = await visitor.Send("/BuilderSchedule?handler=Ics");
        Assert.Contains("URL:http://localhost/course/", await direct.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ATermWithoutCalendarDatesCannotBeExported()
    {
        var undated = site.Catalogue.One("term = 'Fall2020' AND times LIKE '%/%'");
        Assert.Null(AcademicCalendar.For(undated.Term));
        var visitor = site.Visitor();
        await visitor.Add(undated);
        var html = await visitor.Get("/BuilderSchedule");
        Assert.Contains("not on file", html);
        Assert.DoesNotContain("handler=Ics", html);
    }
}
