using Web.Schedule;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

/// <summary>One block on the week grid: what it is and where it sits.</summary>
public record Placed(string Label, string Detail, string Day, int Start, int End, bool IsBreak);

public class BuilderScheduleModel(ScheduleStore store) : PageModel
{
    private static readonly string[] Weekdays = ["Mo", "Tu", "We", "Th", "Fr"];

    public BuiltSchedule Schedule { get; private set; } = new();
    public List<Placed> Blocks { get; private set; } = [];

    /// <summary>Sections in the schedule that publish no meeting time at all.</summary>
    public List<PickedSection> Unscheduled { get; private set; } = [];

    /// <summary>Pairs that overlap. The schedule is the student's own, so a clash
    /// is reported rather than prevented - they may have added it deliberately.</summary>
    public List<(Placed A, Placed B)> Clashes { get; private set; } = [];

    /// <summary>Blocks caught in a clash, so the grid can colour them.</summary>
    public HashSet<Placed> InConflict { get; private set; } = [];

    /// <summary>The grid's first and last hour, from the schedule rather than a
    /// fixed 8-to-4: an evening seminar has to appear somewhere.</summary>
    public int FirstHour { get; private set; } = 8;
    public int LastHour { get; private set; } = 17;

    public IEnumerable<int> Hours => Enumerable.Range(FirstHour, LastHour - FirstHour + 1);
    public IReadOnlyList<string> Days => Weekdays;

    public static string DayName(string code) => code switch
    {
        "Mo" => "Monday", "Tu" => "Tuesday", "We" => "Wednesday",
        "Th" => "Thursday", "Fr" => "Friday", _ => code
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

    public void OnGet()
    {
        Schedule = store.Current;

        foreach (var section in Schedule.Sections)
        {
            var meetings = Meetings.Parse(section.Times);
            if (meetings.Count == 0) { Unscheduled.Add(section); continue; }

            foreach (var meeting in meetings)
                foreach (var day in Weekdays.Where(d => DaysOf(meeting.Days).Contains(d)))
                    Blocks.Add(new Placed(section.Code, $"Section {section.SectionNumber}",
                                          day, meeting.Start, meeting.End, false));
        }

        foreach (var window in Schedule.Breaks)
        {
            var meeting = Meetings.Of(window);
            foreach (var day in Weekdays.Where(d => DaysOf(meeting.Days).Contains(d)))
                Blocks.Add(new Placed(window.Name, "Break", day, meeting.Start, meeting.End, true));
        }

        if (Blocks.Count > 0)
        {
            // Widen the grid to whatever is actually scheduled, then pad an hour
            // so a 9am class is not flush against the top edge.
            FirstHour = Math.Min(8, Blocks.Min(b => b.Start) / 60);
            LastHour = Math.Max(17, (Blocks.Max(b => b.End) + 59) / 60);
        }

        foreach (var a in Blocks)
            foreach (var b in Blocks)
                if (!ReferenceEquals(a, b) && a.Day == b.Day
                    && a.Start < b.End && b.Start < a.End
                    && string.CompareOrdinal(a.Label, b.Label) < 0)
                {
                    Clashes.Add((a, b));
                    InConflict.Add(a);
                    InConflict.Add(b);
                }
    }

    /// <summary>
    /// Day codes as the registrar writes them, two letters each. A break with no
    /// days set applies to the whole week.
    /// </summary>
    private static HashSet<string> DaysOf(string days)
    {
        if (string.IsNullOrWhiteSpace(days)) return [.. Weekdays];
        var found = new HashSet<string>();
        for (var i = 0; i + 1 < days.Length + 1; i++)
            if (i + 1 < days.Length && Weekdays.Contains(days[i..(i + 2)]))
            { found.Add(days[i..(i + 2)]); i++; }
        return found.Count > 0 ? found : [.. Weekdays];
    }
}
