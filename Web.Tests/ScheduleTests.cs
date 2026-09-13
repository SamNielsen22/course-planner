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
    public async Task ResultsComeInOrderOfRelevanceByDefault()
    {
        // "CS 2420" typed: that class first, then CS 2420-something, then anything else that matched.
        var doc = await site.Visitor().Page($"/builder?term={Term}&q=CS%202420&open=false&noClash=false");
        Assert.Equal("relevance", doc.DocumentNode.SelectSingleNode("//select[@name='sort']/option[@selected]")?.GetAttributeValue("value", ""));
        var codes = doc.DocumentNode.SelectNodes("//a[contains(@class,'sc-code')]")?.Select(n => System.Text.RegularExpressions.Regex.Replace(n.InnerText, @"\s+", " ").Trim()).ToList() ?? [];
        Assert.NotEmpty(codes);
        Assert.Equal("CS 2420", codes[0]);
        var firstOther = codes.FindIndex(c => c != "CS 2420");
        if (firstOther >= 0) Assert.DoesNotContain("CS 2420", codes.Skip(firstOther));
        // A title search puts a title that starts with the words before one that merely contains them.
        var calc = await site.Visitor().Page($"/builder?term={Term}&q=calculus%201&open=false&noClash=false");
        var titles = calc.DocumentNode.SelectNodes("//span[@class='sc-title']")?.Select(n => n.InnerText.Trim()).ToList() ?? [];
        Assert.StartsWith("Calculus I", titles[0]);
    }

    [Fact]
    public async Task AFullSectionSaysHowManyAreWaiting()
    {
        static List<string> Facts(HtmlDocument doc, string subject, string number, string section) =>
            doc.DocumentNode.SelectNodes($"//article[.//a[contains(@class,'sc-code')][contains(normalize-space(.), '{subject} {number}')]][.//*[contains(@class,'sc-section')][contains(normalize-space(.), '{section}')]]//span[@class='full']")
               ?.Select(n => System.Text.RegularExpressions.Regex.Replace(HtmlEntity.DeEntitize(n.InnerText), @"\s+", " ").Trim()).ToList() ?? [];

        // Full, with the registrar's count of those waiting - in the newest term anyone is enrolled in.
        var (waited, waiting) = site.Catalogue.FullWithWaitlist();
        var term = waited.Term;
        var doc = await site.Visitor().Page($"/builder?term={term}&q={Uri.EscapeDataString(waited.Code)}&open=false&noClash=false");
        Assert.Contains($"Full · waitlist {waiting}", Facts(doc, waited.Subject, waited.Number, waited.Number2));

        // Full, and the class list says it cannot be waited on.
        var closed = site.Catalogue.FullWithoutWaitlist(term);
        doc = await site.Visitor().Page($"/builder?term={term}&q={Uri.EscapeDataString(closed.Code)}&open=false&noClash=false");
        Assert.Contains("Full · no waitlist", Facts(doc, closed.Subject, closed.Number, closed.Number2));
    }

    [Fact]
    public async Task TheBuilderOffersABackToTopButtonAndOtherPagesDoNot()
    {
        var builder = await site.Visitor().Page($"/builder?term={Term}&open=false&noClash=false");
        var button = builder.DocumentNode.SelectSingleNode("//button[@id='to-top']");
        Assert.NotNull(button);
        Assert.Equal("Back to top", button.GetAttributeValue("aria-label", ""));
        Assert.Null((await site.Visitor().Page("/courses")).DocumentNode.SelectSingleNode("//button[@id='to-top']"));
    }

    [Fact]
    public async Task ALongListHasTheStepsAtBothEnds()
    {
        var doc = await site.Visitor().Page($"/builder?term={Term}&open=false&noClash=false");
        var pagers = doc.DocumentNode.SelectNodes("//nav[contains(@class,'pager')]") ?? new HtmlNodeCollection(null);
        Assert.Equal(2, pagers.Count);
        Assert.Contains("pager-top", pagers[0].GetAttributeValue("class", ""));   // the compact one, above the cards
        Assert.All(pagers, p => Assert.Equal(2, p.SelectNodes(".//button[@name='pg']")?.Count));
        // On the first page both "previous" steps are disabled and both "next" steps live.
        Assert.All(pagers, p => Assert.NotNull(p.SelectNodes(".//button[@name='pg']")![0].Attributes["disabled"]));
        Assert.All(pagers, p => Assert.Null(p.SelectNodes(".//button[@name='pg']")![1].Attributes["disabled"]));
        // The chevrons carry their meaning for a screen reader.
        Assert.Equal("Previous page", pagers[0].SelectNodes(".//button")![0].GetAttributeValue("aria-label", ""));
    }

    [Fact]
    public async Task ABlankSearchLeadsWithAFullyFurnishedCard()
    {
        var doc = await site.Visitor().Page($"/builder?term={Term}&open=false&noClash=false");
        var first = doc.DocumentNode.SelectSingleNode("//article[contains(@class,'section-card')]");
        Assert.NotNull(first);
        // A meeting time, prerequisites that name courses, and both grade figures.
        Assert.Matches(@"\d:\d\d", first.SelectSingleNode(".//p[@class='sc-when']")?.InnerText ?? "");
        Assert.NotEmpty(first.SelectNodes(".//a[contains(@class,'course-ref')]") ?? new HtmlNodeCollection(null));
        var figures = first.SelectNodes(".//span[@class='sc-value']")?.Select(n => n.InnerText.Trim()).ToList() ?? [];
        Assert.Equal(2, figures.Count);
        Assert.All(figures, f => Assert.Matches(@"^\d\.\d\d$", f));
    }

    [Fact]
    public async Task ABlankSearchFollowsTheKeptOrder()
    {
        // The order the user chose to keep, one key per line, is the order served - on every start.
        var kept = File.ReadLines(Path.Combine(Site.RepoRoot, "Web", "data", "spotlight-order.txt")).Where(l => l.Length > 0).ToList();
        Assert.NotEmpty(kept);
        var term = kept[0].Split('|')[0];
        var doc = await site.Visitor().Page($"/builder?term={term}&open=false&noClash=false");
        var served = doc.DocumentNode.SelectNodes("//article[contains(@class,'section-card')]")!
            .Select(a => (Code: System.Text.RegularExpressions.Regex.Replace(a.SelectSingleNode(".//a[contains(@class,'sc-code')]")!.InnerText, @"\s+", " ").Trim(),
                          Section: a.SelectSingleNode(".//span[@class='sc-section']")!.InnerText.Trim().Replace("Section ", "")))
            .Select(x => $"{term}|{x.Code.Substring(0, x.Code.LastIndexOf(' '))}|{x.Code.Substring(x.Code.LastIndexOf(' ') + 1)}|{x.Section}")
            .ToList();
        // Every served card is on the list in list order, allowing for a kept section that has since been cancelled.
        var expected = kept.Where(k => served.Contains(k)).Take(served.Count).ToList();
        Assert.Equal(expected, served);
    }

    [Fact]
    public async Task ABlankSearchIsShuffledOnceForTheServersLifetime()
    {
        static List<string> Codes(HtmlDocument doc) =>
            doc.DocumentNode.SelectNodes("//a[contains(@class,'sc-code')]")?.Select(n => System.Text.RegularExpressions.Regex.Replace(n.InnerText, @"\s+", " ").Trim()).ToList() ?? [];
        var once = Codes(await site.Visitor().Page($"/builder?term={Term}&open=false&noClash=false"));
        var again = Codes(await site.Visitor().Page($"/builder?term={Term}&open=false&noClash=false"));
        var byNumber = Codes(await site.Visitor().Page($"/builder?term={Term}&open=false&noClash=false&sort=name"));
        Assert.NotEmpty(once);
        Assert.Equal(once, again);               // the same order on every visit while the server is up
        Assert.NotEqual(byNumber, once);         // and not course-number order
    }

    [Fact]
    public async Task AGuestIsOnTheMainCampusAndCannotAddAClassFromAnother()
    {
        // The newest term the Asia Campus list is published for; the main list goes up first.
        var term = site.Catalogue.NewestTermOn("uac");
        var asia = site.Catalogue.OnAsiaCampus(term);
        var visitor = site.Visitor();
        var search = $"/builder?term={term}&q={Uri.EscapeDataString(asia.Code)}&open=false&noClash=false";

        // A guest's schedule is a main-campus one: no campus to pick, and the Incheon section is not offered.
        var page = await visitor.Page(search);
        Assert.Null(page.DocumentNode.SelectSingleNode("//select[@name='campus']"));
        Assert.DoesNotContain(asia.Number2, Offered(page, asia));
        Assert.DoesNotContain("Asia Campus", page.DocumentNode.SelectSingleNode("//p[@class='page-sub']")?.InnerText);
        // Asking for it in the address changes nothing.
        Assert.DoesNotContain(asia.Number2, Offered(await visitor.Page(search + "&campus=uac"), asia));

        // A key from another campus, posted straight at the handler, is not for this schedule.
        var slc = site.Catalogue.Timed(term);
        await visitor.Add(slc);
        await visitor.Add(asia);
        Assert.Equal([slc.Code], await visitor.Cart());
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
    public async Task RoomsAreShownAsWrittenAndTheCalendarButtonExplainsItselfOnAsk()
    {
        var s = site.Catalogue.InBuilding(Term, "WEB");
        var visitor = site.Visitor();
        await visitor.Add(s);
        var doc = await visitor.Page("/BuilderSchedule");
        // The room as the registrar writes it, with no tooltip on the building code.
        Assert.Null(doc.DocumentNode.SelectSingleNode("//abbr"));
        Assert.Contains(s.Location!.Trim(), doc.DocumentNode.SelectNodes("//td")!.Select(td => HtmlEntity.DeEntitize(td.InnerText).Trim()));
        // The export button, with its one line always under it; the dates paragraph that used to sit there is gone.
        Assert.Equal("Export to calendar", doc.DocumentNode.SelectSingleNode("//a[@id='ics-button']")?.InnerText.Trim());
        Assert.Contains("Google Calendar", doc.DocumentNode.SelectSingleNode("//p[@id='ics-note']")?.InnerText);
        Assert.DoesNotContain("Classes repeat weekly", doc.DocumentNode.InnerText);
    }

    [Fact]
    public async Task AnOffCampusPlaceGetsItsAddressInTheCalendar()
    {
        // The registrar's code names no University building; the site keeps the address itself.
        var golf = site.Catalogue.One("term = @term AND location = 'GLENDALE GOLF CRS' AND times LIKE '%/%'", new { term = Term });
        var visitor = site.Visitor();
        await visitor.Add(golf);
        var ics = await (await visitor.Send("/BuilderSchedule?handler=Ics")).Content.ReadAsStringAsync();
        Assert.Contains(@"LOCATION:Glendale Golf Course\, 1630 W 2100 S\, Salt Lake City\, UT 84119", ics.Replace("\r\n ", ""));
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
        // No description on the events themselves; the only ones belong to the reminders, which every class carries, half an hour before it starts.
        Assert.Equal(ics.Split("BEGIN:VALARM").Length - 1, ics.Split("DESCRIPTION:").Length - 1);
        Assert.Equal(ics.Split("BEGIN:VEVENT").Length - 1, ics.Split("TRIGGER:-PT30M").Length - 1);

        // One event per meeting DAY - the iPhone's Calendar takes a multi-day BYDAY as its first day only.
        var events = ics.Split("BEGIN:VEVENT").Skip(1).ToList();
        var meetingDays = new[] { range, rooms, online }.Sum(s => Meetings.Parse(s.Times).Sum(m => m.DayCodes.Count));
        Assert.Equal(meetingDays, events.Count);

        // The day range gets an event for every day in it, each repeating on that
        // day alone and skipping the term's days off that fall on it.
        var dates = AcademicCalendar.For(Term)!;
        var meeting = Meetings.Parse(range.Times)[0];
        var rangeEvents = events.Where(e => e.Contains($"SUMMARY:{range.Code}:")).ToList();
        Assert.Equal(meeting.DayCodes.Count, rangeEvents.Count);
        foreach (var (code, e) in meeting.DayCodes.Zip(rangeEvents))
        {
            Assert.Contains($"RRULE:FREQ=WEEKLY;BYDAY={code.ToUpperInvariant()};UNTIL=", e);
            var weekday = (DayOfWeek)((Array.IndexOf(Meeting.Week, code) + 1) % 7);
            var first = dates.FirstDay;
            while (first.DayOfWeek != weekday) first = first.AddDays(1);
            var expectedSkips = dates.DaysOff.Count(d => d >= first && d <= dates.LastDay && d.DayOfWeek == weekday);
            Assert.Equal(expectedSkips, e.Split("EXDATE;").Length - 1);
        }

        // A two-room section: the first meeting's days in the first room, the second's in the second.
        var roomEvents = events.Where(e => e.Contains($"SUMMARY:{rooms.Code}:")).ToList();
        var roomMeetings = Meetings.Parse(rooms.Times);
        Assert.Equal(roomMeetings.Sum(m => m.DayCodes.Count), roomEvents.Count);
        var roomNumbers = rooms.Location!.Split(',').Select(r => r.Trim().Split(' ').Last()).ToList();
        Assert.All(roomEvents.Take(roomMeetings[0].DayCodes.Count), e => Assert.Contains($"Room {roomNumbers[0]}", e.Replace("\r\n ", "")));
        Assert.All(roomEvents.Skip(roomMeetings[0].DayCodes.Count), e => Assert.Contains($"Room {roomNumbers[1]}", e.Replace("\r\n ", "")));

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
