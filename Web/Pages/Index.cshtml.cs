using CoursePlanner.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.OutputCaching;

namespace Web.Pages;

/// <summary>The course search, across every term.</summary>
[OutputCache(Duration = 60)]
[ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
public class IndexModel(CourseQueries db) : PageModel
{
    public const int PageSize = 40;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    // Bound from "pg": Razor Pages reserves "page" for routing.
    [BindProperty(SupportsGet = true, Name = "pg")] public int Page { get; set; } = 1;

    public IReadOnlyList<Course> Results { get; private set; } = [];
    public int Total { get; private set; }
    public bool Searched { get; private set; }

    public int LastPage => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < LastPage;

    public void OnGet() => Search();

    /// <summary>The results alone, for live updates while typing.</summary>
    public IActionResult OnGetRows()
    {
        Search();
        return Partial("_CourseResults", this);
    }

    private void Search()
    {
        // Nothing typed, nothing shown.
        Searched = !string.IsNullOrWhiteSpace(Q);
        if (!Searched) return;

        var all = db.SearchAllCourses(Q);
        Total = all.Count;
        if (Page < 1) Page = 1;
        if (Page > LastPage) Page = LastPage;
        Results = all.Skip((Page - 1) * PageSize).Take(PageSize).ToList();
    }
}
