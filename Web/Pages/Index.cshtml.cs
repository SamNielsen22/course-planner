using CoursePlanner.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.OutputCaching;

namespace Web.Pages;

/// <summary>
/// The course lookup. Find the class first; the term and the section are
/// chosen on the course's own page, so the search itself needs no filters -
/// and a course that last ran years ago is as findable as this term's.
/// </summary>
[OutputCache(Duration = 60)]
[ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
public class IndexModel(CourseQueries db) : PageModel
{
    public const int PageSize = 40;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    // Bound from "pg", not "page": Razor Pages reserves "page" for routing.
    [BindProperty(SupportsGet = true, Name = "pg")] public int Page { get; set; } = 1;

    public IReadOnlyList<Course> Results { get; private set; } = [];
    public int Total { get; private set; }
    public bool Searched { get; private set; }

    public int LastPage => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < LastPage;

    public void OnGet() => Search();

    /// <summary>
    /// The results block on its own, so typing can refresh it without a
    /// navigation. The page and the live update render the same partial, which
    /// is what keeps them from drifting apart.
    /// </summary>
    public IActionResult OnGetRows()
    {
        Search();
        return Partial("_CourseResults", this);
    }

    private void Search()
    {
        // An empty query would return the whole catalogue, so wait until
        // there is something to narrow by.
        Searched = !string.IsNullOrWhiteSpace(Q);
        if (!Searched) return;

        var all = db.SearchAllCourses(Q);
        Total = all.Count;
        if (Page < 1) Page = 1;
        if (Page > LastPage) Page = LastPage;   // a shorter list than the page you were on
        Results = all.Skip((Page - 1) * PageSize).Take(PageSize).ToList();
    }
}
