using System.Text.Json;
using System.Text.RegularExpressions;
using Web.Pages;
using CoursePlanner.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Web.Schedule;

/// <summary>A section the student has put in their schedule.</summary>
public record PickedSection(
    string Term, string Subject, string CourseNumber, string SectionNumber, string Campus,
    string Title, string? Times, string? Location, IReadOnlyList<SectionInstructor> Instructors, int? Units,
    string? Component = null)
{
    public string Key => $"{Term}|{Subject}|{CourseNumber}|{SectionNumber}";
    public string Code => $"{Subject} {CourseNumber}";

    /// <summary>The professors' names, first name first.</summary>
    public IReadOnlyList<string> Professors => Instructors.Select(i => Names.Natural(i.Name)).ToList();

    /// <summary>The professors on one line.</summary>
    public string? Professor => Names.Line(Professors);
}

/// <summary>An hour the student wants kept free.</summary>
public record Break(string Id, string Name, string Days, string From, string Until);

/// <summary>Everything the builder is holding for one visitor.</summary>
public record BuiltSchedule
{
    /// <summary>The term, as chosen or taken from the first class.</summary>
    public string? Term { get; init; }

    /// <summary>The campus: main, uac or online. One schedule is one campus.</summary>
    public string Campus { get; init; } = Web.Pages.Campus.Main;
    public List<PickedSection> Sections { get; init; } = [];
    public List<Break> Breaks { get; init; } = [];

    /// <summary>Credit hours across the classes.</summary>
    public int Units => Sections.Sum(s => s.Units ?? 0);

    /// <summary>How many classes overlap another class or a break.</summary>
    public int Conflicts => Meetings.Conflicting(this).Count;
}

/// <summary>
/// Where the schedules live: a signed cookie holding section keys for each
/// term and campus the visitor has built in, the breaks (shared by all), and
/// which term and campus the builder is on. The details are looked up fresh on
/// every read, so a re-crawl changes what the cart shows and never what it holds.
/// </summary>
public class ScheduleStore(IHttpContextAccessor accessor, IDataProtectionProvider protection, SectionIndex sections)
{
    private const string CookieName = "schedule";

    // Caps keep the cookie under the browser's 4KB limit. Sections are counted across every term.
    private const int MaxSections = 40;
    private const int MaxBreaks = 12;
    private const int MaxBreakName = 40;

    // Rewritten on every change, so the clock restarts each time the schedule is touched.
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(180);

    // Bumping "v1" retires every old cookie at once.
    private readonly IDataProtector _protector = protection.CreateProtector("Web.Schedule.v1");

    /// <summary>
    /// The cookie's contents: the section keys of each schedule under "term|campus",
    /// the breaks, and the term and campus the builder is on. S is the one list
    /// cookies held before schedules were kept per term (2026-10-19); it is read
    /// once and moved into P.
    /// </summary>
    private sealed record Jar(Dictionary<string, List<string>> P, List<Break> B, string? T = null, string? C = null,
                              List<string>? S = null);

    /// <summary>One schedule as the rest of this class works on it: its sections, the breaks, its term and campus.</summary>
    private sealed record Stored(List<string> S, List<Break> B, string? T, string C);

    // The cookie as read, or as written earlier in this request (Request.Cookies still holds what the browser sent).
    private Jar? _jar;

    private HttpContext Http =>
        accessor.HttpContext ?? throw new InvalidOperationException("no request in progress");

    private static string Slot(string? term, string campus) => $"{term}|{campus}";

    private Jar ReadJar()
    {
        if (_jar is not null) return _jar;
        var jar = new Jar([], []);
        var raw = Http.Request.Cookies[CookieName];
        // An unreadable cookie is an empty schedule, not an error.
        if (!string.IsNullOrEmpty(raw))
        {
            try { jar = JsonSerializer.Deserialize<Jar>(_protector.Unprotect(raw)) ?? jar; }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or JsonException) { }
        }
        // A field the cookie lacks (P, on cookies from before) deserialises as null.
        jar = jar with { P = jar.P ?? [], B = jar.B ?? [] };
        if (jar.S is { Count: > 0 })
        {
            // A cookie from before: its one list belongs to the term and campus it was built in.
            var first = jar.S.FirstOrDefault(k => !IsMarker(k));
            var term = jar.T ?? (first is null ? null : TermOf(first));
            var campus = jar.C ?? (first is null ? null : sections.Find(first)?.Campus) ?? Web.Pages.Campus.Main;
            jar.P[Slot(term, campus)] = jar.S;
            jar = jar with { T = term, C = campus, S = null };
        }
        return _jar = jar;
    }

    /// <summary>The schedule for a term on the current campus; the builder's own term when none is named.</summary>
    private Stored Read(string? term = null)
    {
        var jar = ReadJar();
        var campus = jar.C ?? Web.Pages.Campus.Main;
        term ??= jar.T;
        return new Stored(jar.P.GetValueOrDefault(Slot(term, campus)) ?? [], jar.B, term, campus);
    }

    /// <summary>Put a schedule back and make its term and campus the builder's.</summary>
    private void Write(Stored stored)
    {
        var jar = ReadJar();
        var slot = Slot(stored.T, stored.C);
        if (stored.S.Count == 0) jar.P.Remove(slot); else jar.P[slot] = stored.S;
        WriteJar(jar with { B = stored.B, T = stored.T, C = stored.C });
    }

    private void WriteJar(Jar jar)
    {
        _jar = jar;
        Http.Response.Cookies.Append(CookieName, _protector.Protect(JsonSerializer.Serialize(jar)), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = Http.Request.IsHttps,
            MaxAge = Lifetime,
            Path = "/",
            IsEssential = true,
        });
    }

    private static string TermOf(string key) => key[..key.IndexOf('|')];

    /// <summary>The term and campus to build for. Each keeps its own schedule, so switching loses nothing.</summary>
    public void Start(string campus, string? term)
    {
        var jar = ReadJar();
        WriteJar(jar with { C = campus, T = term ?? jar.T });
    }

    /// <summary>The schedule the builder is on, filled in from the current catalogue.</summary>
    public BuiltSchedule Current => Hydrate(Read());

    /// <summary>Keys to sections, with today's titles, times, rooms and professors.</summary>
    private BuiltSchedule Hydrate(Stored stored)
    {
        var picked = new List<PickedSection>(stored.S.Count);
        foreach (var key in stored.S)
        {
            if (sections.Find(key) is not { } s) continue;
            picked.Add(new PickedSection(
                s.Term, s.Subject, s.CourseNumber, s.SectionNumber, s.Campus, s.Title, s.Times, s.Location,
                s.Instructors, s.Units, s.Component));
        }
        return new BuiltSchedule
        {
            Term = stored.T ?? picked.FirstOrDefault()?.Term,
            Campus = stored.C,
            Sections = picked,
            Breaks = stored.B,
        };
    }

    // A lecture and the companion a student picked for it are one unit: added
    // together, removed together. The link is a marker "link:<companion>\t<lecture>"
    // stored alongside the section keys, so it rides in the cookie with no
    // schema change and is skipped on read (Find returns null for it).
    // The separator is a tab, which never appears in a key; a space would split
    // wrongly on subjects that contain one, such as "ME EN".
    private const string LinkPrefix = "link:";
    private const char LinkSep = '\t';
    private static string Marker(string companion, string lecture) => $"{LinkPrefix}{companion}{LinkSep}{lecture}";
    private static bool IsMarker(string entry) => entry.StartsWith(LinkPrefix, StringComparison.Ordinal);
    private static (string Companion, string Lecture)? ParseMarker(string entry)
    {
        if (!IsMarker(entry)) return null;
        var rest = entry[LinkPrefix.Length..];
        var sep = rest.IndexOf(LinkSep);
        return sep < 0 ? null : (rest[..sep], rest[(sep + 1)..]);
    }

    /// <summary>How many real sections the cookie holds across every term, markers aside.</summary>
    private int SectionCount() => ReadJar().P.Values.Sum(s => s.Count(k => !IsMarker(k)));

    /// <summary>Add a section to its term's schedule, which the builder then shows.</summary>
    public void Add(string key)
    {
        if (sections.Find(key) is not { } section) return;
        var stored = Read(TermOf(key));
        if (stored.S.Contains(key)) return;
        // One schedule is one campus; a key from another is ignored.
        if (stored.C != section.Campus) return;
        if (SectionCount() >= MaxSections) return;
        stored.S.Add(key);
        Write(stored);
    }

    /// <summary>
    /// Add a lecture and the companion section chosen for it, as one unit. If the
    /// lecture is already in the schedule its old companion is replaced, so this
    /// also serves as "change my lab".
    /// </summary>
    public void AddPair(string lectureKey, string companionKey)
    {
        if (sections.Find(lectureKey) is not { } lecture || sections.Find(companionKey) is null) return;
        var stored = Read(TermOf(lectureKey));
        if (stored.C != lecture.Campus) return;

        // Drop any earlier choice for this lecture, then add the two together.
        DetachLecture(stored, lectureKey);
        if (SectionCount() >= MaxSections) return;
        if (!stored.S.Contains(lectureKey)) stored.S.Add(lectureKey);
        if (!stored.S.Contains(companionKey)) stored.S.Add(companionKey);
        stored.S.Add(Marker(companionKey, lectureKey));
        Write(stored);
    }

    /// <summary>Remove a section, and the section it is paired with, and their marker.</summary>
    public void RemoveSection(string key)
    {
        var stored = Read();
        var links = stored.S.Select(ParseMarker).OfType<(string Companion, string Lecture)>().ToList();

        var drop = new HashSet<string> { key };
        // Removing a lecture takes its companion; removing a companion takes its lecture.
        foreach (var (companion, lecture) in links)
            if (key == lecture) drop.Add(companion);
            else if (key == companion) drop.Add(lecture);

        var before = stored.S.Count;
        stored.S.RemoveAll(e =>
        {
            if (drop.Contains(e)) return true;
            if (ParseMarker(e) is { } m) return drop.Contains(m.Companion) || drop.Contains(m.Lecture);
            return false;
        });
        if (stored.S.Count != before) Write(stored);
    }

    /// <summary>Drop a lecture, its chosen companion, and their marker, in place.</summary>
    private static void DetachLecture(Stored stored, string lectureKey)
    {
        var companions = stored.S.Select(ParseMarker).OfType<(string Companion, string Lecture)>()
            .Where(m => m.Lecture == lectureKey).Select(m => m.Companion).ToHashSet();
        stored.S.RemoveAll(e => e == lectureKey || companions.Contains(e)
            || (ParseMarker(e) is { } m && m.Lecture == lectureKey));
    }

    public void AddBreak(string name, string days, string from, string until)
    {
        var stored = Read();
        if (stored.B.Count >= MaxBreaks) return;
        var label = string.IsNullOrWhiteSpace(name) ? "Break" : name.Trim();
        if (label.Length > MaxBreakName) label = label[..MaxBreakName];
        stored.B.Add(new Break(Guid.NewGuid().ToString("N")[..8], label, days, from, until));
        Write(stored);
    }

    public void RemoveBreak(string id)
    {
        var stored = Read();
        if (stored.B.RemoveAll(b => b.Id == id) > 0) Write(stored);
    }
}

/// <summary>One meeting: which days, and the minutes it runs between.</summary>
public record Meeting(string Days, int Start, int End)
{
    /// <summary>The registrar's two-letter day codes, in week order.</summary>
    public static readonly string[] Week = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];

    /// <summary>The days this meets, in week order.</summary>
    public IReadOnlyList<string> DayCodes => Week.Where(d => Days.Contains(d, StringComparison.Ordinal)).ToList();

    public bool Overlaps(Meeting other) =>
        DayCodes.Intersect(other.DayCodes).Any() && Start < other.End && other.Start < End;
}

public static partial class Meetings
{
    [GeneratedRegex(@"([A-Za-z]+(?:-[A-Za-z]+)?)\s*/\s*(\d{1,2}):(\d{2})(AM|PM)\s*-\s*(\d{1,2}):(\d{2})(AM|PM)",
                    RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    /// <summary>Every meeting in a times string, such as "Tu/10:45AM-12:05PM; Th/10:45AM-12:05PM". Exact duplicates, which the registrar sometimes lists, are dropped.</summary>
    public static IReadOnlyList<Meeting> Parse(string? times)
    {
        if (string.IsNullOrWhiteSpace(times)) return [];
        return Pattern().Matches(times).Select(m => new Meeting(
            Expand(m.Groups[1].Value),
            Clock(m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value),
            Clock(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value))).Distinct().ToList();
    }

    /// <summary>
    /// A times string for display, with the registrar's exact duplicate meetings
    /// removed. "Th/3:30-5:30; TuWe/6-7; Th/3:30-5:30" becomes the first two.
    /// </summary>
    public static string Display(string? times)
    {
        if (string.IsNullOrWhiteSpace(times)) return "No meeting time";
        var seen = new HashSet<string>();
        var parts = times.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0 && seen.Add(p));
        return string.Join("; ", parts);
    }

    /// <summary>"Mo-Th" is Monday through Thursday.</summary>
    private static string Expand(string days)
    {
        var dash = days.IndexOf('-');
        if (dash < 0) return days;
        var from = Array.IndexOf(Meeting.Week, days[..dash]);
        var to = Array.IndexOf(Meeting.Week, days[(dash + 1)..]);
        return from < 0 || to < from ? days : string.Concat(Meeting.Week[from..(to + 1)]);
    }

    /// <summary>
    /// A break in the same shape as a section's meeting string, so the cart reads
    /// consistently: "MoWeFr/12:00PM-01:00PM". Days as chosen, or all weekdays
    /// when none were picked; times from 24-hour to 12-hour with AM/PM.
    /// </summary>
    public static string Label(Break window)
    {
        var days = string.IsNullOrWhiteSpace(window.Days) ? "MoTuWeThFr" : window.Days;
        return $"{days}/{Clock12(window.From)}-{Clock12(window.Until)}";
    }

    /// <summary>"13:00" to "01:00PM", matching the registrar's meeting-time format.</summary>
    private static string Clock12(string clock)
    {
        var parts = (clock ?? "").Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m))
            return clock ?? "";
        var meridiem = h < 12 ? "AM" : "PM";
        var h12 = h % 12 == 0 ? 12 : h % 12;
        return $"{h12:00}:{m:00}{meridiem}";
    }

    /// <summary>A break as a meeting.</summary>
    public static Meeting Of(Break window)
    {
        var days = string.IsNullOrWhiteSpace(window.Days) ? "MoTuWeThFr" : window.Days;
        return new Meeting(days, Minutes(window.From), Minutes(window.Until));
    }

    private static int Clock(string hour, string minute, string meridiem)
    {
        var h = int.Parse(hour);
        if (meridiem.Equals("PM", StringComparison.OrdinalIgnoreCase) && h != 12) h += 12;
        if (meridiem.Equals("AM", StringComparison.OrdinalIgnoreCase) && h == 12) h = 0;
        return h * 60 + int.Parse(minute);
    }

    private static int Minutes(string clock)
    {
        var parts = clock.Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
            ? h * 60 + m : 0;
    }

    /// <summary>The keys of every section that overlaps another section or a break.</summary>
    public static HashSet<string> Conflicting(BuiltSchedule schedule)
    {
        var clashing = new HashSet<string>();
        var windows = schedule.Breaks.Select(Of).ToList();

        foreach (var a in schedule.Sections)
        {
            var mine = Parse(a.Times);
            if (mine.Count == 0) continue;

            foreach (var b in schedule.Sections)
            {
                if (a.Key == b.Key) continue;
                if (mine.Any(m => Parse(b.Times).Any(m.Overlaps)))
                { clashing.Add(a.Key); clashing.Add(b.Key); }
            }

            if (mine.Any(m => windows.Any(m.Overlaps))) clashing.Add(a.Key);
        }
        return clashing;
    }

    /// <summary>The ids of the breaks a class runs into.</summary>
    public static HashSet<string> ConflictingBreaks(BuiltSchedule schedule)
    {
        var busy = schedule.Sections.SelectMany(s => Parse(s.Times)).ToList();
        return schedule.Breaks.Where(b => busy.Any(Of(b).Overlaps)).Select(b => b.Id).ToHashSet();
    }

    /// <summary>Whether a section clashes with anything already scheduled.</summary>
    public static bool Clashes(string? times, BuiltSchedule schedule)
    {
        var mine = Parse(times);
        if (mine.Count == 0) return false;   // no meeting time, no clash

        var busy = schedule.Sections.SelectMany(s => Parse(s.Times))
            .Concat(schedule.Breaks.Select(Of)).ToList();

        return mine.Any(m => busy.Any(m.Overlaps));
    }
}
