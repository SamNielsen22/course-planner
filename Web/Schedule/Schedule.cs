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
    string Title, string? Times, string? Location, IReadOnlyList<string> Professors, int? Units)
{
    public string Key => $"{Term}|{Subject}|{CourseNumber}|{SectionNumber}";
    public string Code => $"{Subject} {CourseNumber}";

    /// <summary>The professors on one line: one, both, or the first and how many more.</summary>
    public string? Professor => Names.Line(Professors);
}

/// <summary>An hour the student wants kept free.</summary>
public record Break(string Id, string Name, string Days, string From, string Until);

/// <summary>Everything the builder is holding for one visitor.</summary>
public record BuiltSchedule
{
    /// <summary>The account schedule's name. Null for a guest, who has just the one.</summary>
    public string? Name { get; init; }

    /// <summary>The term this schedule is for: chosen at creation, or that of its first class; null when neither.</summary>
    public string? Term { get; init; }

    /// <summary>
    /// Which of the registrar's schedules this is for - main, uac or online -
    /// chosen at creation, or that of its first class. One schedule is one
    /// campus: a week in Incheon and a week in Salt Lake share no grid.
    /// </summary>
    public string Campus { get; init; } = Web.Pages.Campus.Main;
    public List<PickedSection> Sections { get; init; } = [];
    public List<Break> Breaks { get; init; } = [];

    /// <summary>Credit hours across the classes, as the registrar lists them.</summary>
    public int Units => Sections.Sum(s => s.Units ?? 0);

    /// <summary>Classes that overlap another class or a break - the tile says so before the grid does.</summary>
    public int Conflicts => Meetings.Conflicting(this).Count;
}

/// <summary>One of a signed-in student's schedules, as the schedule home lists them.</summary>
public record ScheduleSummary(string Id, string Name, string? Term, string Campus, int Classes, int Units, int Conflicts,
                              DateTime UpdatedAt, bool IsOpen);

/// <summary>
/// A guest's schedule lives in a signed cookie in their own browser - not in a
/// session, not in the database.
///
/// It used to be session state, and that was the one thing on the site that
/// tied a visitor to a particular server: their cart existed in one process's
/// memory, so a second server could not have answered them, a restart emptied
/// it, and eight idle hours threw it away. A cookie makes every request
/// self-describing. Any server can serve it, and it survives everything short
/// of the visitor clearing their cookies.
///
/// Only KEYS are stored - "Term|Subject|Number|Section" - plus the breaks,
/// and only one term's: the first class added for a new term starts a new
/// schedule, so last term's classes never sit on this term's grid.
/// Titles, times, rooms and professors are looked up on every read from the
/// section index, so a schedule kept for months never shows last month's room.
/// A section the registrar has since withdrawn simply does not come back.
///
/// Signed in, a student keeps any number of schedules in Postgres. A second
/// cookie names the one the builder is editing - the "open" one - and the
/// most recently touched stands in when it names nothing. The guest cookie
/// is adopted into the account at sign-in. If Postgres cannot be reached the
/// request falls back to the cookie rather than failing.
/// </summary>
public class ScheduleStore(IHttpContextAccessor accessor, IDataProtectionProvider protection,
                           SectionIndex sections, IServiceProvider services)
{
    private const string CookieName = "schedule";        // a guest's one schedule, signed
    private const string OpenCookieName = "schedule-open"; // which account schedule the builder edits

    /// <summary>
    /// Well inside the 4KB a browser allows, with the signature on top. Both
    /// are capped: past 4KB the browser drops the cookie whole, and one break
    /// too many would have thrown away the entire schedule.
    /// </summary>
    private const int MaxSections = 40;
    private const int MaxBreaks = 12;
    private const int MaxBreakName = 40;
    private const int MaxName = 40;

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(180);

    // The purpose string is part of the key: a cookie signed for anything else
    // will not unprotect here, and bumping "v1" retires every old cookie at once.
    private readonly IDataProtector _protector = protection.CreateProtector("Web.Schedule.v1");

    /// <summary>What actually goes in the cookie: keys, breaks, and the term and campus it is for.</summary>
    private sealed record Stored(List<string> S, List<Break> B, string? T = null, string? C = null);

    // A write in this request must be visible to a read later in the same
    // request; Request.Cookies still holds what the browser sent.
    private Stored? _written;

    // The account schedule this request is editing, once looked up.
    private SavedSchedule? _open;

    private HttpContext Http =>
        accessor.HttpContext ?? throw new InvalidOperationException("no request in progress");

    // Signed in, the schedule lives under the account rather than in the cookie.
    private string? UserId =>
        Http.User.Identity?.IsAuthenticated == true ? Http.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;

    public bool SignedIn => UserId is not null;

    // Null when accounts are off; the cookie is then the only store.
    private UserDbContext? Db => services.GetService<UserDbContext>();

    private Stored Read()
    {
        if (_written is not null) return _written;
        if (UserId is { } userId && Db is { } db)
        {
            try { return FromRow(OpenRow(db, userId)); }
            catch (NpgsqlException) { /* unreachable: this request uses the cookie */ }
        }
        return ReadCookie();
    }

    private Stored ReadCookie()
    {
        var raw = Http.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(raw)) return new([], []);
        // A cookie from an older build, or one signed by another machine, must
        // not throw on read: an unreadable schedule is an empty one.
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
                // A first write with no schedule yet starts one.
                Save(db, OpenRow(db, userId) ?? NewRow(db, userId, null, stored.T, stored.C), stored);
                return;
            }
            catch (NpgsqlException) { /* unreachable: keep it in the cookie for now */ }
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

    /// <summary>
    /// The schedule the builder is editing: the one the open cookie names, if
    /// it is theirs; otherwise the most recently touched; otherwise none.
    /// </summary>
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

    /// <summary>The campus a stored schedule is for: as chosen, or that of its first class, or main.</summary>
    private string CampusOf(Stored stored) =>
        stored.C ?? stored.S.Select(k => sections.Find(k)?.Campus).FirstOrDefault(c => c is not null) ?? Web.Pages.Campus.Main;

    /// <summary>
    /// On sign-in: whatever the guest cookie holds joins the account, and the
    /// cookie is cleared. Into their most recently touched schedule for the
    /// same campus, or as a new one if they have none. Takes the id rather
    /// than reading the request, because the sign-in cookie only takes effect
    /// on the next request.
    /// </summary>
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
            if (!mine.S.Contains(key) && mine.S.Count < MaxSections) mine.S.Add(key);
        mine.B.AddRange(cookie.B.Where(b => mine.B.All(x => x.Id != b.Id)).Take(Math.Max(0, MaxBreaks - mine.B.Count)));
        // Two schedules from different terms: the newer term is the one being planned.
        var newest = Terms.NewestFirst(mine.S.Select(TermOf).Distinct()).FirstOrDefault();
        mine.S.RemoveAll(k => TermOf(k) != newest);
        Save(db, row, mine);
        MarkOpen(row);
        Http.Response.Cookies.Delete(CookieName);
        _written = mine;
    }

    /// <summary>The schedule as the pages show it, filled in from the current data.</summary>
    public BuiltSchedule Current => Hydrate(Read(), _open);

    /// <summary>Keys to what the pages show: titles, times, rooms and professors as the registrar has them now.</summary>
    private BuiltSchedule Hydrate(Stored stored, SavedSchedule? row)
    {
        var picked = new List<PickedSection>(stored.S.Count);
        foreach (var key in stored.S)
        {
            if (sections.Find(key) is not { } s) continue;
            picked.Add(new PickedSection(
                s.Term, s.Subject, s.CourseNumber, s.SectionNumber, s.Campus, s.Title, s.Times, s.Location,
                s.Instructors.Select(i => Names.Natural(i.Name)).ToList(), s.Units));
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

    public void Add(string key)
    {
        if (sections.Find(key) is not { } section) return;
        var stored = Read();
        if (stored.S.Contains(key)) return;
        // One schedule is one campus. An account schedule's was chosen when it
        // was created, and a key from another campus is simply not for it - the
        // builder never offers one. A guest's follows what they add: a class
        // from another campus starts afresh, as one from another term does.
        if (_open is { } row && row.Campus != section.Campus) return;
        if (_open is null && CampusOf(stored) != section.Campus)
            stored.S.RemoveAll(k => sections.Find(k)?.Campus != section.Campus);
        stored = stored with { C = section.Campus };
        stored.S.RemoveAll(k => TermOf(k) != TermOf(key));   // a new term starts a new schedule
        if (stored.S.Count >= MaxSections) return;
        stored.S.Add(key);
        Write(stored);
    }

    public void RemoveSection(string key)
    {
        var stored = Read();
        if (stored.S.Remove(key)) Write(stored);
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

    // ------------------------------------------------------------ the schedule home

    /// <summary>A signed-in student's schedules, most recently touched first. Empty for a guest.</summary>
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

    /// <summary>A new, empty schedule for a term and a campus, opened for the builder.</summary>
    public void Create(string? name, string? term, string? campus)
    {
        if (UserId is not { } userId || Db is not { } db) return;
        NewRow(db, userId, name, term, campus);
        db.SaveChanges();
    }

    /// <summary>Make one of theirs the schedule the builder edits.</summary>
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

    /// <summary>
    /// The days this meets, as codes in week order. Codes run together -
    /// "TuTh", "MoWeFr" - and every code starts with a capital, so a substring
    /// test cannot match across two of them.
    /// </summary>
    public IReadOnlyList<string> DayCodes => Week.Where(d => Days.Contains(d, StringComparison.Ordinal)).ToList();

    public bool Overlaps(Meeting other) =>
        DayCodes.Intersect(other.DayCodes).Any() && Start < other.End && other.Start < End;
}

public static partial class Meetings
{
    [GeneratedRegex(@"([A-Za-z]+(?:-[A-Za-z]+)?)\s*/\s*(\d{1,2}):(\d{2})(AM|PM)\s*-\s*(\d{1,2}):(\d{2})(AM|PM)",
                    RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    /// <summary>
    /// Every meeting in a times string, separated by semicolons when a section
    /// has several ("Tu/10:45AM-12:05PM; Th/10:45AM-12:05PM").
    /// </summary>
    public static IReadOnlyList<Meeting> Parse(string? times)
    {
        if (string.IsNullOrWhiteSpace(times)) return [];
        return Pattern().Matches(times).Select(m => new Meeting(
            Expand(m.Groups[1].Value),
            Clock(m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value),
            Clock(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value))).ToList();
    }

    /// <summary>
    /// "Mo-Th" is Monday through Thursday. The registrar writes a range for
    /// about one meeting in fifteen - language classes most of all - and read
    /// as written it would land on Thursday alone.
    /// </summary>
    private static string Expand(string days)
    {
        var dash = days.IndexOf('-');
        if (dash < 0) return days;
        var from = Array.IndexOf(Meeting.Week, days[..dash]);
        var to = Array.IndexOf(Meeting.Week, days[(dash + 1)..]);
        return from < 0 || to < from ? days : string.Concat(Meeting.Week[from..(to + 1)]);
    }

    /// <summary>A break's 24-hour "HH:MM" pair, on the days it applies to.</summary>
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

    /// <summary>
    /// The keys of every section that overlaps something else in the schedule -
    /// another section, or a break. Reported rather than prevented: the student
    /// may have added the clash deliberately, but should be able to see it.
    /// </summary>
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

    /// <summary>Whether a section clashes with anything already scheduled.</summary>
    public static bool Clashes(string? times, BuiltSchedule schedule)
    {
        var mine = Parse(times);
        if (mine.Count == 0) return false;   // no meeting time cannot clash

        var busy = schedule.Sections.SelectMany(s => Parse(s.Times))
            .Concat(schedule.Breaks.Select(Of)).ToList();

        return mine.Any(m => busy.Any(m.Overlaps));
    }
}
