namespace Web.Pages;

/// <summary>
/// When each term's classes run, from the registrar's academic calendars:
/// first day, last day, and days off. The calendar export needs these, so add
/// a term when the crawl adds one. The Asia Campus has its own dates.
/// </summary>
public static class AcademicCalendar
{
    public sealed record Holiday(string Name, DateOnly From, DateOnly To);

    public sealed record TermDates(DateOnly FirstDay, DateOnly LastDay, IReadOnlyList<Holiday> Holidays)
    {
        /// <summary>Every day off, one by one.</summary>
        public IEnumerable<DateOnly> DaysOff =>
            Holidays.SelectMany(h => Enumerable.Range(0, h.To.DayNumber - h.From.DayNumber + 1).Select(h.From.AddDays));

        /// <summary>"Labor Day, fall break and Thanksgiving".</summary>
        public string HolidayNames => Holidays.Count switch
        {
            0 => "",
            1 => Holidays[0].Name,
            _ => string.Join(", ", Holidays.Take(Holidays.Count - 1).Select(h => h.Name)) + " and " + Holidays[^1].Name,
        };
    }

    private static readonly Dictionary<(string Campus, string Term), TermDates> Known = new()
    {
        [(Campus.Main, "Spring2026")] = new(new(2026, 1, 5), new(2026, 4, 21),
        [
            new("Martin Luther King Jr. Day", new(2026, 1, 19), new(2026, 1, 19)),
            new("Presidents Day", new(2026, 2, 16), new(2026, 2, 16)),
            new("spring break", new(2026, 3, 7), new(2026, 3, 15)),
        ]),
        [(Campus.Main, "Summer2026")] = new(new(2026, 5, 11), new(2026, 7, 29),
        [
            new("Memorial Day", new(2026, 5, 25), new(2026, 5, 25)),
            new("Juneteenth", new(2026, 6, 15), new(2026, 6, 15)),
            new("Independence Day", new(2026, 7, 3), new(2026, 7, 3)),
            new("Pioneer Day", new(2026, 7, 24), new(2026, 7, 24)),
        ]),
        [(Campus.Main, "Fall2026")] = new(new(2026, 8, 24), new(2026, 12, 10),
        [
            new("Labor Day", new(2026, 9, 7), new(2026, 9, 7)),
            new("fall break", new(2026, 10, 10), new(2026, 10, 18)),
            new("Thanksgiving", new(2026, 11, 26), new(2026, 11, 29)),
        ]),
        // 2026-2027 main campus and UOnline calendar, registrar's PDF dated 07/20/26.
        [(Campus.Main, "Spring2027")] = new(new(2027, 1, 11), new(2027, 4, 27),
        [
            new("Martin Luther King Jr. Day", new(2027, 1, 18), new(2027, 1, 18)),
            new("Presidents Day", new(2027, 2, 15), new(2027, 2, 15)),
            new("spring break", new(2027, 3, 6), new(2027, 3, 14)),
        ]),
        [(Campus.Main, "Summer2027")] = new(new(2027, 5, 17), new(2027, 8, 4),
        [
            new("Memorial Day", new(2027, 5, 31), new(2027, 5, 31)),
            new("Juneteenth", new(2027, 6, 21), new(2027, 6, 21)),
            new("Independence Day", new(2027, 7, 5), new(2027, 7, 5)),
            new("Pioneer Day", new(2027, 7, 23), new(2027, 7, 23)),
        ]),
        // Asia Campus, from the registrar's Asia Campus calendars. Independence
        // Movement Day (March 2) falls before its spring classes begin.
        [("uac", "Spring2026")] = new(new(2026, 3, 3), new(2026, 6, 8),
        [
            new("Korea Labor Day", new(2026, 5, 1), new(2026, 5, 1)),
            new("Children's Day", new(2026, 5, 5), new(2026, 5, 5)),
            new("Buddha's Birthday", new(2026, 5, 25), new(2026, 5, 25)),
            new("Memorial Day", new(2026, 6, 6), new(2026, 6, 6)),
        ]),
        [("uac", "Summer2026")] = new(new(2026, 6, 18), new(2026, 7, 29),
        [
            new("Constitution Day", new(2026, 7, 17), new(2026, 7, 17)),
        ]),
        [("uac", "Fall2026")] = new(new(2026, 8, 24), new(2026, 12, 4),
        [
            new("Chuseok", new(2026, 9, 25), new(2026, 9, 25)),
            new("National Foundation Day", new(2026, 10, 2), new(2026, 10, 2)),
            new("Hangeul Day", new(2026, 10, 9), new(2026, 10, 9)),
            new("Thanksgiving", new(2026, 11, 26), new(2026, 11, 29)),
        ]),
    };

    /// <summary>A term's dates on a campus: the Asia Campus's own, the main calendar for everyone else.</summary>
    public static TermDates? For(string term, string campus = Campus.Main) =>
        Known.GetValueOrDefault((campus == "uac" ? "uac" : Campus.Main, term));
}
