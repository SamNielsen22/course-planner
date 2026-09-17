using System.Globalization;
using System.Text;
using Web.Pages;

namespace Web.Schedule;

/// <summary>
/// The schedule as an iCalendar file: weekly events from the first day of
/// classes to the last, skipping days off, with building addresses.
/// </summary>
public static class Ics
{
    /// <summary>A time zone: IANA name, system rules, and the VTIMEZONE block.</summary>
    private sealed record Zone(string Id, TimeZoneInfo Info, string[] Definition);

    /// <summary>Mountain time with US daylight saving.</summary>
    private static readonly Zone Mountain = new("America/Denver", FindZone("America/Denver", "Mountain Standard Time"),
    [
        "BEGIN:VTIMEZONE", "TZID:America/Denver",
        "BEGIN:STANDARD", "DTSTART:19701101T020000", "RRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU",
        "TZOFFSETFROM:-0600", "TZOFFSETTO:-0700", "TZNAME:MST", "END:STANDARD",
        "BEGIN:DAYLIGHT", "DTSTART:19700308T020000", "RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU",
        "TZOFFSETFROM:-0700", "TZOFFSETTO:-0600", "TZNAME:MDT", "END:DAYLIGHT",
        "END:VTIMEZONE",
    ]);

    /// <summary>Korea, for the Asia Campus. No daylight saving.</summary>
    private static readonly Zone Korea = new("Asia/Seoul", FindZone("Asia/Seoul", "Korea Standard Time"),
    [
        "BEGIN:VTIMEZONE", "TZID:Asia/Seoul",
        "BEGIN:STANDARD", "DTSTART:19700101T000000",
        "TZOFFSETFROM:+0900", "TZOFFSETTO:+0900", "TZNAME:KST", "END:STANDARD",
        "END:VTIMEZONE",
    ]);

    /// <summary>Times are local to the campus.</summary>
    private static Zone ZoneOf(string campus) => campus == "uac" ? Korea : Mountain;

    /// <summary>The registrar's day codes.</summary>
    private static readonly (string Code, string ByDay, DayOfWeek Day)[] Days =
    [
        ("Mo", "MO", DayOfWeek.Monday), ("Tu", "TU", DayOfWeek.Tuesday), ("We", "WE", DayOfWeek.Wednesday),
        ("Th", "TH", DayOfWeek.Thursday), ("Fr", "FR", DayOfWeek.Friday), ("Sa", "SA", DayOfWeek.Saturday),
        ("Su", "SU", DayOfWeek.Sunday),
    ];

    /// <summary>Every section with a meeting time in a term with known dates. Breaks stay out.</summary>
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

    /// <summary>One room per meeting when the registrar lists several, else the first room for all.</summary>
    private static string?[] Rooms(string? location, int count)
    {
        var parts = (location ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return Enumerable.Range(0, count).Select(i => parts.Length == count ? parts[i] : parts.FirstOrDefault()).ToArray();
    }

    private static void Event(StringBuilder text, PickedSection section, Meeting meeting, string? room, int index,
                              AcademicCalendar.TermDates dates, Zone zone, Buildings buildings, string siteUrl, string stamp)
    {
        // A meeting that ends before it starts is a registrar typo.
        if (meeting.End <= meeting.Start) return;

        // One event per weekday: the iPhone reads a combined BYDAY rule as its first day only.
        foreach (var day in Days.Where(d => meeting.DayCodes.Contains(d.Code)))
        {
            // The first class is the first such weekday of the term.
            var first = dates.FirstDay;
            while (first.DayOfWeek != day.Day) first = first.AddDays(1);
            if (first > dates.LastDay) continue;

            Line(text, "BEGIN:VEVENT");
            Line(text, $"UID:{section.Key.Replace('|', '-').Replace(" ", "")}-{index + 1}-{day.ByDay}@utahcoursecompass.com");
            Line(text, "DTSTAMP:" + stamp);
            Line(text, $"DTSTART;TZID={zone.Id}:{Local(first, meeting.Start)}");
            Line(text, $"DTEND;TZID={zone.Id}:{Local(first, meeting.End)}");
            Line(text, $"RRULE:FREQ=WEEKLY;BYDAY={day.ByDay};UNTIL={Until(dates.LastDay, zone)}");
            foreach (var off in dates.DaysOff)
                if (off >= first && off <= dates.LastDay && off.DayOfWeek == day.Day)
                    Line(text, $"EXDATE;TZID={zone.Id}:{Local(off, meeting.Start)}");

            Line(text, "SUMMARY:" + Escape($"{section.Code}: {section.Title}"));

            var place = buildings.Resolve(room);
            if (Where(room, place) is { } where) Line(text, "LOCATION:" + Escape(where));

            Line(text, $"URL:{siteUrl}/course/{Uri.EscapeDataString(section.Subject)}/{Uri.EscapeDataString(section.CourseNumber)}");
            // A reminder half an hour before each class.
            Line(text, "BEGIN:VALARM");
            Line(text, "ACTION:DISPLAY");
            Line(text, "DESCRIPTION:" + Escape($"{section.Code} in 30 minutes"));
            Line(text, "TRIGGER:-PT30M");
            Line(text, "END:VALARM");
            Line(text, "END:VEVENT");
        }
    }

    /// <summary>Building, room and address when known; "Online" for CANVAS; else the location as written.</summary>
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

    /// <summary>The end of the last day of classes, in UTC as RRULE requires.</summary>
    private static string Until(DateOnly lastDay, Zone zone)
    {
        var midnight = lastDay.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(midnight, zone.Info).AddSeconds(-1);
        return utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>By IANA name, or the Windows name for the same zone.</summary>
    private static TimeZoneInfo FindZone(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
    }

    /// <summary>Escapes the characters iCalendar text reserves.</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\n").Replace("\n", "\\n");

    /// <summary>One line, CRLF-ended and folded at 75 bytes without splitting a character.</summary>
    private static void Line(StringBuilder text, string content)
    {
        if (Encoding.UTF8.GetByteCount(content) <= 75) { text.Append(content).Append("\r\n"); return; }

        var chunk = new StringBuilder();
        var bytes = 0;
        var first = true;
        foreach (var rune in content.EnumerateRunes())
        {
            var limit = first ? 75 : 74;   // continuation lines start with a space
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
