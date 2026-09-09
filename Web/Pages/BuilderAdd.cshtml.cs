using Web.Schedule;
using CoursePlanner.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

/// <summary>
/// Search and results on one page. They used to be two, which meant every
/// refinement was a round trip and the filters you were adjusting scrolled out
/// of sight on arrival.
/// </summary>
public class BuilderAddModel(CourseQueries db, SiteIndex site, SectionIndex sections, GradeIndex grades, ScheduleStore store) : PageModel
{
    private const int PageSize = 24;

    [BindProperty(SupportsGet = true)] public string? Term { get; set; }

    /// <summary>
    /// Inferred from the search box, not chosen from a list. A student thinks
    /// "CS 2420" or "calculus", not "select a subject, then enter a number", and
    /// the two fields could contradict each other besides.
    /// </summary>
    public string? Subject { get; private set; }

    /// <summary>Gen-ed designation. Matched with LIKE, because the registrar
    /// packs several into one field.</summary>
    [BindProperty(SupportsGet = true)] public string? Req { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    // On by default: a section you cannot register for is not a useful result.
    [BindProperty(SupportsGet = true)] public bool Open { get; set; } = true;

    /// <summary>Hide sections that clash with what is already in the schedule.</summary>
    [BindProperty(SupportsGet = true)] public bool NoClash { get; set; } = true;

    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "name";
    // Bound from "pg", not "page". Razor Pages reserves "page" for its own
    // routing, so a property bound to it silently stays at its default and
    // every request looks like page 1.
    [BindProperty(SupportsGet = true, Name = "pg")] public int Page { get; set; } = 1;

    public IReadOnlyList<string> Terms { get; private set; } = [];
    public IReadOnlyList<string> Departments { get; private set; } = [];
    public IReadOnlyList<string> Designations { get; private set; } = [];
    public IReadOnlyList<SectionCard> Results { get; private set; } = [];
    public int Total { get; private set; }

    public int LastPage => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < LastPage;

    /// <summary>Sections already in the schedule, so the card can say so.</summary>
    public HashSet<string> Picked { get; private set; } = [];

    /// <summary>What the cart panel shows.</summary>
    public BuiltSchedule Cart { get; private set; } = new();

    public void OnGet() => Search();

    /// <summary>Put one section in the schedule and stay where you were.</summary>
    public IActionResult OnPostAdd(string term, string subject, string number, string section)
    {
        // Only the key is kept. The store fills in title, time and room on
        // read, so what the cart shows is always what the registrar says now.
        var key = SectionIndex.KeyOf(term, subject, number, section);
        if (sections.Find(key) is not null) store.Add(key);

        return new OkResult();
    }

    public IActionResult OnGetRows()
    {
        Search();
        return Partial("_SectionResults", this);
    }

    /// <summary>Take one section back out, from either the card or the cart.</summary>
    public IActionResult OnPostDrop(string key)
    {
        store.RemoveSection(key);
        return new OkResult();
    }

    public IActionResult OnPostAddBreak(string name, string days, string from, string until)
    {
        store.AddBreak(name, days, from, until);
        return new OkResult();
    }

    public IActionResult OnPostDropBreak(string id)
    {
        store.RemoveBreak(id);
        return new OkResult();
    }

    /// <summary>The cart on its own, so adding a section can refresh just that.</summary>
    public IActionResult OnGetCart() => Partial("_Cart", store.Current);

    private void Search()
    {
        if (Page < 1) Page = 1;
        Terms = Pages.Terms.NewestFirst(site.Terms);
        Term = Terms.Contains(Term) ? Term : Terms.FirstOrDefault();
        Departments = site.Departments;
        Designations = site.Designations;
        if (Term is null) return;

        // A query that is exactly a subject code means the subject. Left as free
        // text it would match every title containing those letters, so "CS"
        // would drag in "Physics" and "Forensics".
        var typed = (Q ?? "").Trim();
        var text = typed;
        if (typed.Length > 0 &&
            Departments.FirstOrDefault(d => d.Equals(typed, StringComparison.OrdinalIgnoreCase))
                is { } code)
        {
            Subject = code;
            text = null;
        }

        // From the in-memory term, not the database: the whole term was being
        // read and built on every keystroke to show twenty-four rows.
        var found = sections.Find(Term, subject: Subject, query: text,
                                  requirement: Req, openOnly: Open);

        var schedule = store.Current;
        Cart = schedule;
        Picked = schedule.Sections.Select(s => s.Key).ToHashSet();

        // A section already in the schedule clashes with itself, and is dropped
        // like any other clash - the hour is taken, so it is no longer a
        // candidate. Taking it back out is done from the cart, which is why the
        // card does not need to stay on screen to offer it.
        if (NoClash)
            found = found.Where(s => !Meetings.Clashes(s.Times, schedule)).ToList();

        // The COURSE average, not the section's own. A future term has no
        // section-level grades at all - Fall 2026 has none of 8,415 - so sorting
        // on those compared null with null and did nothing at all. This is also
        // the figure the card actually shows.
        var averages = Sort == "gpa" ? site.CourseAverages : null;
        double? Average(Section s) =>
            averages!.TryGetValue($"{s.Subject}|{s.CourseNumber}", out var g) ? g : null;

        var ordered = Sort switch
        {
            // A missing grade is not a good grade, so those sort last either way.
            "gpa" => found.OrderBy(s => Average(s) is null).ThenByDescending(Average).ToList(),
            "time" => found.OrderBy(s => CourseQueries.EarliestStart(s.Times) ?? int.MaxValue).ToList(),
            // Sections with no meeting time sort last either way: they answer
            // neither "what starts first" nor "what starts last".
            "time_desc" => found.OrderBy(s => s.Times is null)
                                .ThenByDescending(s => CourseQueries.EarliestStart(s.Times) ?? int.MinValue).ToList(),
            // Numerically. As text "105" falls between "1030" and "1050".
            _ => found.OrderBy(s => s.Subject, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(s => int.TryParse(
                          new string(s.CourseNumber.TakeWhile(char.IsDigit).ToArray()),
                          out var n) ? n : int.MaxValue)
                      .ThenBy(s => s.CourseNumber, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(s => s.SectionNumber, StringComparer.OrdinalIgnoreCase).ToList(),
        };

        Total = ordered.Count;
        if ((Page - 1) * PageSize >= Total && Page > 1) Page = LastPage;
        Results = db.Cards(ordered.Skip((Page - 1) * PageSize).Take(PageSize).ToList(),
                           grades.CourseAverage, grades.InstructorCourseAverage);
    }
}
