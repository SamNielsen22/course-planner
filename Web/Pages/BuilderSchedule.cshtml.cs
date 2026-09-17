using System.Text;
using Web.Schedule;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

/// <summary>One block on the week grid.</summary>
public record Placed(string Label, string Detail, string Day, int Start, int End, bool IsBreak);

/// <summary>The built schedule. Personal, so never cached.</summary>
[ResponseCache(NoStore = true)]
public class BuilderScheduleModel(ScheduleStore store, Buildings buildings) : PageModel
{
    public BuiltSchedule Schedule { get; private set; } = new();
    public List<Placed> Blocks { get; private set; } = [];

    /// <summary>Building names for the location column.</summary>
    public Buildings Buildings => buildings;

    /// <summary>The term's class dates, when known. The export needs them.</summary>
    public AcademicCalendar.TermDates? Calendar { get; private set; }

    /// <summary>A UOnline schedule has no meeting times, so no grid, clashes or export.</summary>
    public bool Online => Schedule.Campus == "online";

    /// <summary>Pairs of blocks that overlap. Reported, not prevented.</summary>
    public List<(Placed A, Placed B)> Clashes { get; private set; } = [];

    /// <summary>Blocks in a clash, for colouring.</summary>
    public HashSet<Placed> InConflict { get; private set; } = [];

    /// <summary>The grid's hours, widened to fit the schedule.</summary>
    private int FirstHour { get; set; } = 8;
    private int LastHour { get; set; } = 17;

    public IEnumerable<int> Hours => Enumerable.Range(FirstHour, LastHour - FirstHour + 1);

    /// <summary>Monday to Friday, plus a weekend day when something meets on it.</summary>
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

    /// <summary>A block's place in its column, as percentages.</summary>
    public (double Top, double Height) Position(Placed block)
    {
        var span = (LastHour - FirstHour + 1) * 60.0;
        var top = (block.Start - FirstHour * 60) / span * 100;
        var height = (block.End - block.Start) / span * 100;
        return (Math.Max(0, top), Math.Max(1.5, height));
    }

    public void OnGet() => Load();

    /// <summary>The schedule as an .ics file. Sent inline so an iPhone offers "Add All" at once.</summary>
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

        // The registrar sometimes lists a meeting twice.
        Blocks = Blocks.Distinct().ToList();

        // A weekend column only when a class meets then.
        Days = Meeting.Week.Where((d, i) => i < 5 || Blocks.Any(b => b.Day == d)).ToList();

        if (Blocks.Count > 0)
        {
            // Widen the grid to what is scheduled.
            FirstHour = Math.Min(8, Blocks.Min(b => b.Start) / 60);
            LastHour = Math.Max(17, (Blocks.Max(b => b.End) + 59) / 60);
        }

        // Each pair once. A section never clashes with itself, and two breaks may overlap.
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
