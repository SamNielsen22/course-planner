using System.Globalization;
using System.Text;
using Web.Pages;

namespace Web.Schedule;

/// <summary>
/// The schedule as an iCalendar file (RFC 5545): one weekly event per section,
/// running from the term's first day of classes to its last and skipping the
/// days off, with the building's name and street address as the location so
/// a phone's calendar can map it. Written by hand - the format is a few lines
/// of text, and its one subtlety, that a recurring event's UNTIL must be in
/// UTC while its start is local time, is handled in one place here.
/// </summary>
public static class Ics
{
    /// <summary>A time zone: its IANA name, the system's rules for it, and its VTIMEZONE for the file.</summary>
    private sealed record Zone(string Id, TimeZoneInfo Info, string[] Definition);

    /// <summary>Mountain time, with the US daylight-saving rules in force since 2007.</summary>
    private static readonly Zone Mountain = new("America/Denver", FindZone("America/Denver", "Mountain Standard Time"),
    [
        "BEGIN:VTIMEZONE", "TZID:America/Denver",
        "BEGIN:STANDARD", "DTSTART:19701101T020000", "RRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU",
        "TZOFFSETFROM:-0600", "TZOFFSETTO:-0700", "TZNAME:MST", "END:STANDARD",
        "BEGIN:DAYLIGHT", "DTSTART:19700308T020000", "RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU",
        "TZOFFSETFROM:-0700", "TZOFFSETTO:-0600", "TZNAME:MDT", "END:DAYLIGHT",
        "END:VTIMEZONE",
    ]);

    /// <summary>Korea, for the Asia Campus in Incheon: no daylight saving.</summary>
    private static readonly Zone Korea = new("Asia/Seoul", FindZone("Asia/Seoul", "Korea Standard Time"),
    [
        "BEGIN:VTIMEZONE", "TZID:Asia/Seoul",
        "BEGIN:STANDARD", "DTSTART:19700101T000000",
        "TZOFFSETFROM:+0900", "TZOFFSETTO:+0900", "TZNAME:KST", "END:STANDARD",
        "END:VTIMEZONE",
    ]);

    /// <summary>The schedule's meeting times are local to its campus.</summary>
    private static Zone ZoneOf(string campus) => campus == "uac" ? Korea : Mountain;

    /// <summary>Two-letter registrar day codes, as the schedule writes them.</summary>
    private static readonly (string Code, string ByDay, DayOfWeek Day)[] Days =
    [
        ("Mo", "MO", DayOfWeek.Monday), ("Tu", "TU", DayOfWeek.Tuesday), ("We", "WE", DayOfWeek.Wednesday),
        ("Th", "TH", DayOfWeek.Thursday), ("Fr", "FR", DayOfWeek.Friday), ("Sa", "SA", DayOfWeek.Saturday),
        ("Su", "SU", DayOfWeek.Sunday),
    ];

    /// <summary>
    /// Every section with a meeting time in a term whose dates are known.
    /// Breaks are the student's own constraints, not appointments, and stay out.
    /// </summary>
    public static string Build(BuiltSchedule schedule, Buildings buildings, string siteUrl, DateTimeOffset now)
    {
        var text = new StringBuilder();
        Line(text, "BEGIN:VCALENDAR");
        Line(text, "VERSION:2.0");
        Line(text, "PRODID:-//Utah Course Compass//Schedule//EN");
        Line(text, "CALSCALE:GREGORIAN");
        Line(text, "METHOD:PUBLISH");
        if (schedule.Sections.Count > 0)
            Line(text, "X-WR-CALNAME:" + Escape(Terms.Display(schedule.Sections[0].Term) + " classes"));
        var zone = ZoneOf(schedule.Campus);
        Line(text, "X-WR-TIMEZONE:" + zone.Id);
        foreach (var line in zone.Definition) Line(text, line);

        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var section in schedule.Sections)
        {
            if (AcademicCalendar.For(section.Term, schedule.Campus) is not { } dates) continue;
            var meetings = Meetings.Parse(section.Times);
            var rooms = Rooms(section.Location, meetings.Count);
            for (var i = 0; i < meetings.Count; i++)
                Event(text, section, meetings[i], rooms[i], i, dates, zone, buildings, siteUrl, stamp);
        }
        Line(text, "END:VCALENDAR");
        return text.ToString();
    }

    /// <summary>
    /// A section with several meetings lists a room for each - "M LI 1715, M LI
    /// 1160" against "Tu/…; Th/…" - and when the registrar has not, the first
    /// room stands for all.
    /// </summary>
    private static string?[] Rooms(string? location, int count)
    {
        var parts = (location ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return Enumerable.Range(0, count).Select(i => parts.Length == count ? parts[i] : parts.FirstOrDefault()).ToArray();
    }

    private static void Event(StringBuilder text, PickedSection section, Meeting meeting, string? room, int index,
                              AcademicCalendar.TermDates dates, Zone zone, Buildings buildings, string siteUrl, string stamp)
    {
        // A meeting that ends before it starts is a registrar typo (fourteen
        // sections, all "…AM-01:00AM"); a calendar would refuse it.
        var days = Days.Where(d => meeting.DayCodes.Contains(d.Code)).ToList();
        if (days.Count == 0 || meeting.End <= meeting.Start) return;

        // The first class is the first day of term that falls on a meeting day.
        var first = dates.FirstDay;
        while (days.All(d => d.Day != first.DayOfWeek)) first = first.AddDays(1);
        if (first > dates.LastDay) return;

        Line(text, "BEGIN:VEVENT");
        Line(text, $"UID:{section.Key.Replace('|', '-').Replace(" ", "")}-{index + 1}@utahcoursecompass.com");
        Line(text, "DTSTAMP:" + stamp);
        Line(text, $"DTSTART;TZID={zone.Id}:{Local(first, meeting.Start)}");
        Line(text, $"DTEND;TZID={zone.Id}:{Local(first, meeting.End)}");
        Line(text, $"RRULE:FREQ=WEEKLY;BYDAY={string.Join(',', days.Select(d => d.ByDay))};UNTIL={Until(dates.LastDay, zone)}");
        foreach (var off in dates.DaysOff)
            if (off >= first && off <= dates.LastDay && days.Any(d => d.Day == off.DayOfWeek))
                Line(text, $"EXDATE;TZID={zone.Id}:{Local(off, meeting.Start)}");

        Line(text, "SUMMARY:" + Escape($"{section.Code}: {section.Title}"));

        var place = buildings.Resolve(room);
        if (Where(room, place) is { } where) Line(text, "LOCATION:" + Escape(where));

        Line(text, $"URL:{siteUrl}/course/{Uri.EscapeDataString(section.Subject)}/{Uri.EscapeDataString(section.CourseNumber)}");
        Line(text, "END:VEVENT");
    }

    /// <summary>
    /// The building's name, the room and the street address when the location
    /// names a building; "Online" for the registrar's CANVAS and Online
    /// placeholders; otherwise the location as written, or nothing.
    /// </summary>
    private static string? Where(string? location, Buildings.Place? place)
    {
        if (place is not null)
            return string.Join(", ", new[]
            {
                place.Building.Name,
                place.Room.Length > 0 ? "Room " + place.Room : null,
                place.Building.Address,
            }.Where(part => !string.IsNullOrEmpty(part)));

        var raw = (location ?? "").Replace(" .", "").Trim();
        if (raw.Length == 0) return null;
        return raw.StartsWith("CANVAS", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("Online", StringComparison.OrdinalIgnoreCase) ? "Online" : raw;
    }

    private static string Local(DateOnly day, int minutes) =>
        day.ToDateTime(new TimeOnly(minutes / 60, minutes % 60)).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    /// <summary>The end of the last day of classes, in UTC as the RRULE requires.</summary>
    private static string Until(DateOnly lastDay, Zone zone)
    {
        var midnight = lastDay.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(midnight, zone.Info).AddSeconds(-1);
        return utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>By IANA name, or by the name Windows gives the same zone.</summary>
    private static TimeZoneInfo FindZone(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
    }

    /// <summary>Text values: backslash, semicolon, comma and newline are escaped.</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\n").Replace("\n", "\\n");

    /// <summary>
    /// One content line, CRLF-terminated and folded at 75 octets as the format
    /// requires - counted in bytes, never splitting a character, since a title
    /// can carry an en dash or an accent.
    /// </summary>
    private static void Line(StringBuilder text, string content)
    {
        if (Encoding.UTF8.GetByteCount(content) <= 75) { text.Append(content).Append("\r\n"); return; }

        var chunk = new StringBuilder();
        var bytes = 0;
        var first = true;
        foreach (var rune in content.EnumerateRunes())
        {
            var limit = first ? 75 : 74;   // a continuation line begins with a space
            if (bytes + rune.Utf8SequenceLength > limit)
            {
                text.Append(first ? "" : " ").Append(chunk).Append("\r\n");
                chunk.Clear(); bytes = 0; first = false;
            }
            chunk.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        text.Append(first ? "" : " ").Append(chunk).Append("\r\n");
    }
}
