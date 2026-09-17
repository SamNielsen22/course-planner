using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Web.Accounts;
using Web.Pages;
using CoursePlanner.Data;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace Web.Schedule;

/// <summary>A section the student has put in their schedule.</summary>
public record PickedSection(
    string Term, string Subject, string CourseNumber, string SectionNumber, string Campus,
    string Title, string? Times, string? Location, IReadOnlyList<SectionInstructor> Instructors, int? Units)
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
    /// <summary>The schedule's name. Null for a guest.</summary>
    public string? Name { get; init; }

    /// <summary>The term, chosen at creation or taken from the first class.</summary>
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

/// <summary>One saved schedule, as the schedule home lists it.</summary>
public record ScheduleSummary(string Id, string Name, string? Term, string Campus, int Classes, int Units, int Conflicts,
                              DateTime UpdatedAt, bool IsOpen);

/// <summary>
/// Where a schedule lives. A guest's is a signed cookie holding section keys
/// and breaks for one term; the details are looked up fresh on every read.
/// A signed-in student's schedules are rows in Postgres, with a second cookie
/// naming the one the builder is editing. The guest cookie joins the account
/// at sign-in. If Postgres is down the cookie is used instead.
/// </summary>
public class ScheduleStore(IHttpContextAccessor accessor, IDataProtectionProvider protection,
                           SectionIndex sections, IServiceProvider services)
{
    private const string CookieName = "schedule";        // a guest's schedule, signed
    private const string OpenCookieName = "schedule-open"; // which saved schedule the builder edits

    // Caps keep the cookie under the browser's 4KB limit.
    private const int MaxSections = 40;
    private const int MaxBreaks = 12;
    private const int MaxBreakName = 40;
    private const int MaxName = 40;

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(180);

    // Bumping "v1" retires every old cookie at once.
    private readonly IDataProtector _protector = protection.CreateProtector("Web.Schedule.v1");

    /// <summary>The cookie's contents: section keys, breaks, term and campus.</summary>
    private sealed record Stored(List<string> S, List<Break> B, string? T = null, string? C = null);

    // A write earlier in this request, since Request.Cookies still holds what the browser sent.
    private Stored? _written;

    // The saved schedule this request is editing, once looked up.
    private SavedSchedule? _open;

    private HttpContext Http =>
        accessor.HttpContext ?? throw new InvalidOperationException("no request in progress");

    private string? UserId =>
        Http.User.Identity?.IsAuthenticated == true ? Http.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;

    public bool SignedIn => UserId is not null;

    // Null when accounts are off.
    private UserDbContext? Db => services.GetService<UserDbContext>();

    private Stored Read()
    {
        if (_written is not null) return _written;
        if (UserId is { } userId && Db is { } db)
        {
            try { return FromRow(OpenRow(db, userId)); }
            catch (NpgsqlException) { /* Postgres down: use the cookie */ }
        }
        return ReadCookie();
    }

    private Stored ReadCookie()
    {
        var raw = Http.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(raw)) return new([], []);
        // An unreadable cookie is an empty schedule, not an error.
        try
        {
            return JsonSerializer.Deserialize<Stored>(_protector.Unprotect(raw)) ?? new([], []);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return new([], []);
        }
    }

    private void Write(Stored stored)
    {
        _written = stored;
        if (UserId is { } userId && Db is { } db)
        {
            try
            {
                // A first write starts a schedule.
                Save(db, OpenRow(db, userId) ?? NewRow(db, userId, null, stored.T, stored.C), stored);
                return;
            }
            catch (NpgsqlException) { /* Postgres down: keep it in the cookie */ }
        }
        WriteCookie(stored);
    }

    private void WriteCookie(Stored stored) =>
        Http.Response.Cookies.Append(CookieName, _protector.Protect(JsonSerializer.Serialize(stored)), Options());

    private CookieOptions Options() => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = Http.Request.IsHttps,
        MaxAge = Lifetime,
        Path = "/",
        IsEssential = true,
    };

    private static Stored FromRow(SavedSchedule? row) => row is null ? new([], []) : new(
        JsonSerializer.Deserialize<List<string>>(row.Sections) ?? [],
        JsonSerializer.Deserialize<List<Break>>(row.Breaks) ?? [],
        row.Term, row.Campus);

    /// <summary>The schedule being edited: the one the open cookie names, else the most recently touched.</summary>
    private SavedSchedule? OpenRow(UserDbContext db, string userId)
    {
        if (_open is not null) return _open;
        var wanted = Http.Request.Cookies[OpenCookieName];
        _open = (wanted is null ? null : db.Schedules.FirstOrDefault(s => s.Id == wanted && s.UserId == userId))
                ?? db.Schedules.Where(s => s.UserId == userId).OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
        return _open;
    }

    private SavedSchedule NewRow(UserDbContext db, string userId, string? name, string? term, string? campus)
    {
        var count = db.Schedules.Count(s => s.UserId == userId);
        var row = new SavedSchedule
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            UserId = userId,
            Name = Clean(name) ?? $"Schedule {count + 1}",
            Term = term,
            Campus = Web.Pages.Campus.Known(campus),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Schedules.Add(row);
        MarkOpen(row);
        return row;
    }

    private static string? Clean(string? name)
    {
        var trimmed = (name ?? "").Trim();
        return trimmed.Length == 0 ? null : trimmed[..Math.Min(MaxName, trimmed.Length)];
    }

    private void MarkOpen(SavedSchedule row)
    {
        _open = row;
        Http.Response.Cookies.Append(OpenCookieName, row.Id, Options());
    }

    private static void Save(UserDbContext db, SavedSchedule row, Stored stored)
    {
        row.Sections = JsonSerializer.Serialize(stored.S);
        row.Breaks = JsonSerializer.Serialize(stored.B);
        row.UpdatedAt = DateTime.UtcNow;
        db.SaveChanges();
    }

    private static string TermOf(string key) => key[..key.IndexOf('|')];

    /// <summary>The campus: as chosen, else that of the first class, else main.</summary>
    private string CampusOf(Stored stored) =>
        stored.C ?? stored.S.Select(k => sections.Find(k)?.Campus).FirstOrDefault(c => c is not null) ?? Web.Pages.Campus.Main;

    /// <summary>On sign-in, the guest cookie's classes join the account and the cookie is cleared.</summary>
    public void AdoptCookie(string userId)
    {
        if (Db is not { } db) return;
        var cookie = ReadCookie();
        if (cookie.S.Count == 0 && cookie.B.Count == 0) return;
        var campus = CampusOf(cookie);
        var row = db.Schedules.Where(s => s.UserId == userId && s.Campus == campus).OrderByDescending(s => s.UpdatedAt).FirstOrDefault()
                  ?? NewRow(db, userId, null, cookie.T, campus);
        var mine = FromRow(row);
        foreach (var key in cookie.S)
            if (!mine.S.Contains(key) && (IsMarker(key) || SectionCount(mine) < MaxSections)) mine.S.Add(key);
        mine.B.AddRange(cookie.B.Where(b => mine.B.All(x => x.Id != b.Id)).Take(Math.Max(0, MaxBreaks - mine.B.Count)));
        // Keep only the newest term; a marker survives only if both its ends do.
        var newest = Terms.NewestFirst(mine.S.Where(k => !IsMarker(k)).Select(TermOf).Distinct()).FirstOrDefault();
        mine.S.RemoveAll(k => IsMarker(k) ? ParseMarker(k) is not { } m || TermOf(m.Companion) != newest
                                          : TermOf(k) != newest);
        Save(db, row, mine);
        MarkOpen(row);
        Http.Response.Cookies.Delete(CookieName);
        _written = mine;
    }

    /// <summary>A guest's term and campus choice. A different campus or term starts a new schedule; breaks stay.</summary>
    public void StartGuest(string campus, string? term)
    {
        if (SignedIn) return;
        var stored = ReadCookie();
        if (CampusOf(stored) != campus) stored.S.Clear();
        if (term is not null && stored.S.Any(k => !IsMarker(k) && TermOf(k) != term)) stored.S.Clear();
        Write(stored with { C = campus, T = term ?? stored.T });
    }

    /// <summary>The schedule, filled in from the current catalogue.</summary>
    public BuiltSchedule Current => Hydrate(Read(), _open);

    /// <summary>Keys to sections, with today's titles, times, rooms and professors.</summary>
    private BuiltSchedule Hydrate(Stored stored, SavedSchedule? row)
    {
        var picked = new List<PickedSection>(stored.S.Count);
        foreach (var key in stored.S)
        {
            if (sections.Find(key) is not { } s) continue;
            picked.Add(new PickedSection(
                s.Term, s.Subject, s.CourseNumber, s.SectionNumber, s.Campus, s.Title, s.Times, s.Location,
                s.Instructors, s.Units));
        }
        return new BuiltSchedule
        {
            Name = row?.Name,
            Term = row?.Term ?? stored.T ?? picked.FirstOrDefault()?.Term,
            Campus = row?.Campus ?? CampusOf(stored),
            Sections = picked,
            Breaks = stored.B,
        };
    }

    // A lecture and the companion a student picked for it are one unit: added
    // together, removed together. The link is a marker "link:<companion>\t<lecture>"
    // stored alongside the section keys, so it rides in the cookie and the row
    // with no schema change and is skipped on read (Find returns null for it).
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

    /// <summary>How many real sections are in the schedule, markers aside.</summary>
    private static int SectionCount(Stored s) => s.S.Count(k => !IsMarker(k));

    public void Add(string key)
    {
        if (sections.Find(key) is not { } section) return;
        var stored = Read();
        if (stored.S.Contains(key)) return;
        // One schedule is one campus; a key from another is ignored.
        if ((_open?.Campus ?? CampusOf(stored)) != section.Campus) return;
        stored = stored with { C = section.Campus };
        RemoveForNewTerm(stored, TermOf(key));   // a new term starts a new schedule
        if (SectionCount(stored) >= MaxSections) return;
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
        var stored = Read();
        if ((_open?.Campus ?? CampusOf(stored)) != lecture.Campus) return;
        stored = stored with { C = lecture.Campus };
        RemoveForNewTerm(stored, TermOf(lectureKey));

        // Drop any earlier choice for this lecture, then add the two together.
        DetachLecture(stored, lectureKey);
        if (SectionCount(stored) >= MaxSections) return;
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

    /// <summary>Clear the schedule when the added key is in a different term. Markers of dropped sections go too.</summary>
    private static void RemoveForNewTerm(Stored stored, string term) =>
        stored.S.RemoveAll(k => IsMarker(k) ? ParseMarker(k) is { } m && (TermOf(m.Companion) != term || TermOf(m.Lecture) != term)
                                            : TermOf(k) != term);

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

    // ------------------------------------------------------------ the schedule home

    /// <summary>A student's schedules, most recently touched first. Empty for a guest.</summary>
    public IReadOnlyList<ScheduleSummary> Mine()
    {
        if (UserId is not { } userId || Db is not { } db) return [];
        var open = OpenRow(db, userId)?.Id;
        return db.Schedules.Where(s => s.UserId == userId).OrderByDescending(s => s.UpdatedAt).AsEnumerable()
            .Select(s =>
            {
                var built = Hydrate(FromRow(s), s);
                return new ScheduleSummary(s.Id, s.Name, built.Term, s.Campus, built.Sections.Count, built.Units, built.Conflicts,
                                           s.UpdatedAt, s.Id == open);
            })
            .ToList();
    }

    /// <summary>A new empty schedule, opened for the builder.</summary>
    public void Create(string? name, string? term, string? campus)
    {
        if (UserId is not { } userId || Db is not { } db) return;
        NewRow(db, userId, name, term, campus);
        db.SaveChanges();
    }

    /// <summary>Make one of their schedules the one the builder edits.</summary>
    public void Open(string id)
    {
        if (UserId is not { } userId || Db is not { } db) return;
        if (db.Schedules.FirstOrDefault(s => s.Id == id && s.UserId == userId) is { } row) MarkOpen(row);
    }

    public void Rename(string id, string? name)
    {
        if (UserId is not { } userId || Db is not { } db || Clean(name) is not { } clean) return;
        if (db.Schedules.FirstOrDefault(s => s.Id == id && s.UserId == userId) is not { } row) return;
        row.Name = clean;
        db.SaveChanges();
    }

    public void Delete(string id)
    {
        if (UserId is not { } userId || Db is not { } db) return;
        if (db.Schedules.FirstOrDefault(s => s.Id == id && s.UserId == userId) is not { } row) return;
        db.Schedules.Remove(row);
        db.SaveChanges();
        if (Http.Request.Cookies[OpenCookieName] == id) Http.Response.Cookies.Delete(OpenCookieName);
        if (_open?.Id == id) _open = null;
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
