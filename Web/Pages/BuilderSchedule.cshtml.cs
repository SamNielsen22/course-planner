using System.Text;
using Web.Schedule;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

/// <summary>One block on the week grid: what it is and where it sits.</summary>
public record Placed(string Label, string Detail, string Day, int Start, int End, bool IsBreak);

/// <summary>Personal - YOUR schedule - so no cache, shared or private, may keep a copy.</summary>
[ResponseCache(NoStore = true)]
public class BuilderScheduleModel(ScheduleStore store, Buildings buildings) : PageModel
{
    public BuiltSchedule Schedule { get; private set; } = new();
    public List<Placed> Blocks { get; private set; } = [];

    /// <summary>For the location column: a building code carries its full name on hover.</summary>
    public Buildings Buildings => buildings;

    /// <summary>The term's class dates, when the academic calendar has them - what the export needs.</summary>
    public AcademicCalendar.TermDates? Calendar { get; private set; }

    /// <summary>
    /// A UOnline schedule: its sections never meet at an hour or in a room,
    /// so there is no week to draw, nothing to clash, and nothing to put in
    /// a calendar. The page is the list of classes and no more.
    /// </summary>
    public bool Online => Schedule.Campus == "online";

    /// <summary>Pairs that overlap. The schedule is the student's own, so a clash
    /// is reported rather than prevented - they may have added it deliberately.</summary>
    public List<(Placed A, Placed B)> Clashes { get; private set; } = [];

    /// <summary>Blocks caught in a clash, so the grid can colour them.</summary>
    public HashSet<Placed> InConflict { get; private set; } = [];

    /// <summary>The grid's first and last hour, from the schedule rather than a
    /// fixed 8-to-4: an evening seminar has to appear somewhere.</summary>
    private int FirstHour { get; set; } = 8;
    private int LastHour { get; set; } = 17;

    public IEnumerable<int> Hours => Enumerable.Range(FirstHour, LastHour - FirstHour + 1);

    /// <summary>Monday to Friday, plus a weekend day only when something meets on it.</summary>
    public IReadOnlyList<string> Days { get; private set; } = Meeting.Week[..5];

    public static string DayName(string code) => code switch
    {
        "Mo" => "Monday", "Tu" => "Tuesday", "We" => "Wednesday", "Th" => "Thursday",
        "Fr" => "Friday", "Sa" => "Saturday", "Su" => "Sunday", _ => code
    };

    public static string Clock(int minutes)
    {
        var hour = minutes / 60;
        var suffix = hour < 12 ? "am" : "pm";
        var shown = hour % 12 == 0 ? 12 : hour % 12;
        return minutes % 60 == 0 ? $"{shown}{suffix}" : $"{shown}:{minutes % 60:00}{suffix}";
    }

    /// <summary>Where a block sits in the column, as a percentage of the grid.</summary>
    public (double Top, double Height) Position(Placed block)
    {
        var span = (LastHour - FirstHour + 1) * 60.0;
        var top = (block.Start - FirstHour * 60) / span * 100;
        var height = (block.End - block.Start) / span * 100;
        return (Math.Max(0, top), Math.Max(1.5, height));
    }

    public void OnGet() => Load();

    /// <summary>
    /// The schedule as an .ics file to import into a calendar. Personal, so
    /// never cached. Sent inline rather than as an attachment: an iPhone then
    /// shows the "Add All" sheet at once instead of parking the file in
    /// Downloads, while a desktop browser, which cannot display a calendar,
    /// downloads it under the term's name either way.
    /// </summary>
    public IActionResult OnGetIcs()
    {
        Load();
        if (Schedule.Sections.Count == 0) return RedirectToPage();

        var site = $"{Request.Scheme}://{Request.Host}";
        var text = Ics.Build(Schedule, buildings, site, DateTimeOffset.UtcNow);
        Response.Headers.CacheControl = "no-store";

        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(Terms.Display(Schedule.Sections[0].Term).Replace(' ', '-') + "-classes.ics");
        Response.Headers.ContentDisposition = disposition.ToString();
        return File(Encoding.UTF8.GetBytes(text), "text/calendar; charset=utf-8");
    }

    private void Load()
    {
        Schedule = store.Current;
        if (Schedule.Sections.Count > 0) Calendar = AcademicCalendar.For(Schedule.Sections[0].Term, Schedule.Campus);

        foreach (var section in Schedule.Sections)
        {
            foreach (var meeting in Meetings.Parse(section.Times))
                foreach (var day in meeting.DayCodes)
                    Blocks.Add(new Placed(section.Code, $"Section {section.SectionNumber}",
                                          day, meeting.Start, meeting.End, false));
        }

        foreach (var window in Schedule.Breaks)
        {
            var meeting = Meetings.Of(window);
            foreach (var day in meeting.DayCodes)
                Blocks.Add(new Placed(window.Name, "Break", day, meeting.Start, meeting.End, true));
        }

        // The registrar lists the same meeting twice now and then; one block is enough.
        Blocks = Blocks.Distinct().ToList();

        // A weekend column only when a class meets then - 117 sections do in
        // Fall 2026, nearly all on Saturday - so the ordinary week stays five wide.
        Days = Meeting.Week.Where((d, i) => i < 5 || Blocks.Any(b => b.Day == d)).ToList();

        if (Blocks.Count > 0)
        {
            // Widen the grid to whatever is actually scheduled, then pad an hour
            // so a 9am class is not flush against the top edge.
            FirstHour = Math.Min(8, Blocks.Min(b => b.Start) / 60);
            LastHour = Math.Max(17, (Blocks.Max(b => b.End) + 59) / 60);
        }

        // Each pair once. Two sections of the same course clash like any other
        // pair; a section's own meetings never clash with each other, and two
        // breaks overlapping is nobody's problem.
        for (var i = 0; i < Blocks.Count; i++)
            for (var j = i + 1; j < Blocks.Count; j++)
            {
                var (a, b) = (Blocks[i], Blocks[j]);
                if (a.Day != b.Day || a.Start >= b.End || b.Start >= a.End) continue;
                if (a.IsBreak && b.IsBreak) continue;
                if (a.Label == b.Label && a.Detail == b.Detail) continue;
                Clashes.Add((a, b));
                InConflict.Add(a);
                InConflict.Add(b);
            }
    }
}
