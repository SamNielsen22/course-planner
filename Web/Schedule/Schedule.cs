using System.Text.Json;
using System.Text.RegularExpressions;
using Web.Pages;
using CoursePlanner.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Web.Schedule;

/// <summary>A section the student has put in their schedule.</summary>
public record PickedSection(
    string Term, string Subject, string CourseNumber, string SectionNumber,
    string Title, string? Times, string? Location, string? Professor)
{
    public string Key => $"{Term}|{Subject}|{CourseNumber}|{SectionNumber}";
    public string Code => $"{Subject} {CourseNumber}";
}

/// <summary>An hour the student wants kept free.</summary>
public record Break(string Id, string Name, string Days, string From, string Until);

/// <summary>Everything the builder is holding for one visitor.</summary>
public record BuiltSchedule
{
    public List<PickedSection> Sections { get; init; } = [];
    public List<Break> Breaks { get; init; } = [];
}

/// <summary>
/// The schedule lives in a signed cookie in the visitor's own browser - not in
/// a session, not in the database.
///
/// It used to be session state, and that was the one thing on the site that
/// tied a visitor to a particular server: their cart existed in one process's
/// memory, so a second server could not have answered them, a restart emptied
/// it, and eight idle hours threw it away. A cookie makes every request
/// self-describing. Any server can serve it, and it survives everything short
/// of the visitor clearing their cookies.
///
/// Only KEYS are stored - "Term|Subject|Number|Section" - plus the breaks.
/// Titles, times, rooms and professors are looked up on every read from the
/// section index, so a schedule kept for months never shows last month's room.
/// A section the registrar has since withdrawn simply does not come back.
/// </summary>
public class ScheduleStore(IHttpContextAccessor accessor, IDataProtectionProvider protection,
                           SectionIndex sections)
{
    private const string CookieName = "schedule";

    /// <summary>Well inside the 4KB a browser allows, with the signature on top.</summary>
    private const int MaxSections = 40;

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(180);

    // The purpose string is part of the key: a cookie signed for anything else
    // will not unprotect here, and bumping "v1" retires every old cookie at once.
    private readonly IDataProtector _protector = protection.CreateProtector("Web.Schedule.v1");

    /// <summary>What actually goes in the cookie.</summary>
    private sealed record Stored(List<string> S, List<Break> B);

    // A write in this request must be visible to a read later in the same
    // request; Request.Cookies still holds what the browser sent.
    private Stored? _written;

    private HttpContext Http =>
        accessor.HttpContext ?? throw new InvalidOperationException("no request in progress");

    private Stored Read()
    {
        if (_written is not null) return _written;
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
        Http.Response.Cookies.Append(CookieName, _protector.Protect(JsonSerializer.Serialize(stored)),
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = Http.Request.IsHttps,
                MaxAge = Lifetime,
                Path = "/",
                IsEssential = true,
            });
    }

    /// <summary>The schedule as the pages show it, filled in from the current data.</summary>
    public BuiltSchedule Current
    {
        get
        {
            var stored = Read();
            var picked = new List<PickedSection>(stored.S.Count);
            foreach (var key in stored.S)
            {
                if (sections.Find(key) is not { } s) continue;
                picked.Add(new PickedSection(
                    s.Term, s.Subject, s.CourseNumber, s.SectionNumber, s.Title, s.Times, s.Location,
                    s.Instructors.Count > 0 ? Names.Natural(s.Instructors[0].Name) : null));
            }
            return new BuiltSchedule { Sections = picked, Breaks = stored.B };
        }
    }

    public void Add(string key)
    {
        var stored = Read();
        if (stored.S.Contains(key) || stored.S.Count >= MaxSections) return;
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
        stored.B.Add(new Break(Guid.NewGuid().ToString("N")[..8],
                               string.IsNullOrWhiteSpace(name) ? "Break" : name.Trim(),
                               days, from, until));
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
    public bool Overlaps(Meeting other) =>
        SharesADay(Days, other.Days) && Start < other.End && other.Start < End;

    /// <summary>
    /// Day codes run together - "TuTh", "MWF" - and Tu/Th must be read before
    /// T and H or "Tu" matches Monday's neighbour and Thursday is lost.
    /// </summary>
    private static bool SharesADay(string a, string b) =>
        DaysOf(a).Overlaps(DaysOf(b));

    private static HashSet<char> DaysOf(string days)
    {
        var found = new HashSet<char>();
        for (var i = 0; i < days.Length; i++)
        {
            if (i + 1 < days.Length && days[i] == 'T' && days[i + 1] == 'u') { found.Add('T'); i++; }
            else if (i + 1 < days.Length && days[i] == 'T' && days[i + 1] == 'h') { found.Add('R'); i++; }
            else if (days[i] == 'M') found.Add('M');
            else if (days[i] == 'W') found.Add('W');
            else if (days[i] == 'F') found.Add('F');
            else if (days[i] == 'T') found.Add('T');
            else if (days[i] == 'R') found.Add('R');
        }
        return found;
    }
}

public static partial class Meetings
{
    [GeneratedRegex(@"([A-Za-z]+)\s*/\s*(\d{1,2}):(\d{2})(AM|PM)\s*-\s*(\d{1,2}):(\d{2})(AM|PM)",
                    RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    /// <summary>Every meeting in a times string; sections can list several.</summary>
    public static IReadOnlyList<Meeting> Parse(string? times)
    {
        if (string.IsNullOrWhiteSpace(times)) return [];
        return Pattern().Matches(times).Select(m => new Meeting(
            m.Groups[1].Value,
            Clock(m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value),
            Clock(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value))).ToList();
    }

    /// <summary>A break's 24-hour "HH:MM" pair, on the days it applies to.</summary>
    public static Meeting Of(Break window)
    {
        var days = string.IsNullOrWhiteSpace(window.Days) ? "MTWRF" : window.Days;
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
