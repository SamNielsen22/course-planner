using Web.Schedule;
using CoursePlanner.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

/// <summary>The schedule builder: search, results and the cart on one page.</summary>
public class BuilderAddModel(CourseQueries db, SiteIndex site, SectionIndex sections, GradeIndex grades, ScheduleStore store, Spotlight spotlight) : PageModel
{
    private const int PageSize = 24;

    [BindProperty(SupportsGet = true)] public string? Term { get; set; }

    /// <summary>The schedule's campus. Results come from it alone; there is nothing to pick.</summary>
    public string Campus { get; private set; } = Pages.Campus.Main;

    /// <summary>Set when the search text is exactly a subject code.</summary>
    public string? Subject { get; private set; }

    /// <summary>Requirement designation filter.</summary>
    [BindProperty(SupportsGet = true)] public string? Req { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    /// <summary>Open seats only. On by default.</summary>
    [BindProperty(SupportsGet = true)] public bool Open { get; set; } = true;

    /// <summary>Hide sections that clash with the schedule.</summary>
    [BindProperty(SupportsGet = true)] public bool NoClash { get; set; } = true;

    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "relevance";
    // Bound from "pg": Razor Pages reserves "page" for routing.
    [BindProperty(SupportsGet = true, Name = "pg")] public int Page { get; set; } = 1;

    public IReadOnlyList<string> Terms { get; private set; } = [];
    public IReadOnlyList<string> Departments { get; private set; } = [];
    public IReadOnlyList<string> Designations { get; private set; } = [];
    public IReadOnlyList<SectionCard> Results { get; private set; } = [];
    public int Total { get; private set; }

    public int LastPage => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < LastPage;

    /// <summary>Keys of sections already in the schedule.</summary>
    public HashSet<string> Picked { get; private set; } = [];

    /// <summary>What the cart panel shows.</summary>
    public BuiltSchedule Cart { get; private set; } = new();

    /// <summary>
    /// The companion choices for every "required" lecture on the page, keyed by
    /// section number, as JSON. The pairing is static, so it is embedded with the
    /// results and the chooser opens with no round trip. Seats are as of page load,
    /// the same freshness as the seat counts already on the cards.
    /// </summary>
    public string CompanionData { get; private set; } = "{}";

    public void OnGet() => Search();

    /// <summary>Add one section to the schedule.</summary>
    public IActionResult OnPostAdd(string term, string subject, string number, string section)
    {
        // Only the key is kept; details are looked up on read.
        var key = SectionIndex.KeyOf(term, subject, number, section);
        if (sections.Find(key) is not null) store.Add(key);

        return new OkResult();
    }

    /// <summary>Add a lecture and the companion section chosen for it, together.</summary>
    public IActionResult OnPostAddPair(string term, string subject, string number, string lecture, string companion)
    {
        var lectureKey = SectionIndex.KeyOf(term, subject, number, lecture);
        var companionKey = SectionIndex.KeyOf(term, subject, number, companion);
        store.AddPair(lectureKey, companionKey);
        return new OkResult();
    }

    public IActionResult OnGetRows()
    {
        Search();
        return Partial("_SectionResults", this);
    }

    /// <summary>Remove one section from the schedule.</summary>
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

    /// <summary>The cart alone, for refreshing it after a change.</summary>
    public IActionResult OnGetCart() => Partial("_Cart", store.Current);

    private void Search()
    {
        if (Page < 1) Page = 1;
        Terms = Pages.Terms.NewestFirst(site.Terms);
        // The schedule's own term is the default; a term in the query wins.
        var schedule = store.Current;
        if (!Terms.Contains(Term))
            Term = schedule.Term is { } own && Terms.Contains(own) ? own : Terms.FirstOrDefault();
        Campus = schedule.Campus;
        Departments = site.Departments;
        Designations = site.Designations;
        if (Term is null) return;

        // Text that is exactly a subject code means that subject, not a substring.
        var typed = (Q ?? "").Trim();
        var text = typed;
        if (typed.Length > 0 &&
            Departments.FirstOrDefault(d => d.Equals(typed, StringComparison.OrdinalIgnoreCase))
                is { } code)
        {
            Subject = code;
            text = null;
        }

        var found = sections.Find(Term, subject: Subject, query: text,
                                  requirement: Req, openOnly: Open, campus: Campus);

        // Companions are added through their lecture, not on their own, so a
        // lab or discussion in a course that has a lecture is kept out of the
        // results. A lab that is its own course (no lecture) still shows.
        var withLecture = sections.All(Term)
            .Where(s => s.Component == "Lecture")
            .Select(s => (s.Subject, s.CourseNumber)).ToHashSet();
        found = found.Where(s => !(Companions.IsCompanion(s.Component)
                                   && withLecture.Contains((s.Subject, s.CourseNumber)))).ToList();

        Cart = schedule;
        Picked = schedule.Sections.Select(s => s.Key).ToHashSet();

        // Sections already in the schedule are not shown again in the results.
        found = found.Where(s => !Picked.Contains(SectionIndex.KeyOf(s.Term, s.Subject, s.CourseNumber, s.SectionNumber))).ToList();

        // A section that clashes with the schedule drops out too.
        if (NoClash)
            found = found.Where(s => !Meetings.Clashes(s.Times, schedule)).ToList();

        // The course average, which the card shows; a future term has no section grades.
        var averages = Sort == "gpa" ? site.CourseAverages : null;
        double? Average(Section s) =>
            averages!.TryGetValue($"{s.Subject}|{s.CourseNumber}", out var g) ? g : null;

        // Relevance: exact code, then code prefix, then title start, then title contains.
        var byName = found.OrderBy(s => s.Subject, StringComparer.OrdinalIgnoreCase)
                          .ThenBy(s => int.TryParse(new string(s.CourseNumber.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : int.MaxValue)
                          .ThenBy(s => s.CourseNumber, StringComparer.OrdinalIgnoreCase)
                          .ThenBy(s => s.SectionNumber, StringComparer.OrdinalIgnoreCase);
        var roman = CourseQueries.RomanTitle(typed);
        int Relevance(Section s)
        {
            const StringComparison Like = StringComparison.OrdinalIgnoreCase;
            if (typed.Length == 0) return 0;
            var code = $"{s.Subject} {s.CourseNumber}";
            if (code.Equals(typed, Like)) return 0;
            if (code.StartsWith(typed, Like)) return 1;
            if (s.Title.StartsWith(typed, Like) || (roman is not null && s.Title.StartsWith(roman, Like))) return 2;
            if (s.Title.Contains(typed, Like) || (roman is not null && s.Title.Contains(roman, Like))) return 3;
            return 4;
        }

        static int Number(Section s) =>
            int.TryParse(new string(s.CourseNumber.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : int.MaxValue;

        var ordered = Sort switch
        {
            // Nothing typed: the fixed spotlight order.
            "relevance" when typed.Length == 0 && Subject is null => spotlight.Order(found),
            "relevance" => byName.OrderBy(Relevance).ToList(),
            // No grade sorts last.
            "gpa" => found.OrderBy(s => Average(s) is null).ThenByDescending(Average).ToList(),
            "time" => found.OrderBy(s => CourseQueries.EarliestStart(s.Times) ?? int.MaxValue).ToList(),
            // No meeting time sorts last either way.
            "time_desc" => found.OrderBy(s => s.Times is null)
                                .ThenByDescending(s => CourseQueries.EarliestStart(s.Times) ?? int.MinValue).ToList(),
            // Numeric order, so "105" does not fall between "1030" and "1050".
            _ => found.OrderBy(s => s.Subject, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(Number)
                      .ThenBy(s => s.CourseNumber, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(s => s.SectionNumber, StringComparer.OrdinalIgnoreCase).ToList(),
        };

        Total = ordered.Count;
        if ((Page - 1) * PageSize >= Total && Page > 1) Page = LastPage;
        Results = db.Cards(ordered.Skip((Page - 1) * PageSize).Take(PageSize).ToList(),
                           grades.CourseAverage, grades.InstructorCourseAverage);

        CompanionData = BuildCompanionData();
    }

    /// <summary>
    /// The companion sections a student may choose for each "required" lecture on
    /// this page, from the in-memory term index, so the chooser needs no fetch.
    /// </summary>
    private string BuildCompanionData()
    {
        var lectures = Results.Where(c => c.RequiresCompanion).ToList();
        if (lectures.Count == 0) return "{}";

        // The whole course's sections, once per course on the page.
        var byCourse = sections.All(Term!)
            .GroupBy(s => (s.Subject, s.CourseNumber))
            .ToDictionary(g => g.Key, g => g.ToList());

        var data = new Dictionary<string, object>();
        foreach (var lec in lectures)
        {
            if (!byCourse.TryGetValue((lec.Subject, lec.CourseNumber), out var course)) continue;
            var pieces = course.Select(s => new CoursePlanner.Data.CourseSection(s.SectionNumber, s.Component, s.PairsWith)).ToList();
            var chosen = Companions.ChoicesFor(pieces, lec.SectionNumber).ToHashSet();
            var published = Companions.Resolve(pieces).Companions.GetValueOrDefault(lec.SectionNumber) is { Count: > 0 };
            var byNumber = course.ToDictionary(s => s.SectionNumber);

            var key = SectionIndex.KeyOf(Term!, lec.Subject, lec.CourseNumber, lec.SectionNumber);
            data[key] = chosen.OrderBy(x => x, StringComparer.Ordinal).Select(n =>
            {
                var s = byNumber[n];
                return new
                {
                    section = n,
                    kind = Companions.Kind(new[] { s.Component }),
                    times = s.Times,
                    location = s.Location,
                    seats = s.SeatsAvailable,
                    professor = Names.Line(s.Instructors.Select(i => Names.Natural(i.Name)).ToList()),
                    published,
                };
            }).ToList();
        }
        return System.Text.Json.JsonSerializer.Serialize(data);
    }
}
