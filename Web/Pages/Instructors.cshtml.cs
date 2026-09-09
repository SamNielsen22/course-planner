using Microsoft.AspNetCore.OutputCaching;
using CoursePlanner.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

[OutputCache(Duration = 60)]
[ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
public class InstructorsModel(InstructorIndex people) : PageModel
{
    private const int PageSize = 24;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    // Bound from "pg", not "page". Razor Pages reserves "page" for its own
    // routing, so a property bound to it silently stays at its default and
    // every request looks like page 1.
    [BindProperty(SupportsGet = true, Name = "pg")] public int Page { get; set; } = 1;

    public IReadOnlyList<InstructorCard> Results { get; private set; } = [];
    public int Total { get; private set; }

    public int LastPage => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < LastPage;

    public void OnGet() => Search();

    public IActionResult OnGetRows()
    {
        Search();
        return Partial("_InstructorResults", this);
    }

    private void Search()
    {
        if (Page < 1) Page = 1;
        // Name only, in the index's weighted-random order: one box, like the
        // course lookup, and no ranking.
        (Results, Total) = people.Search(Q, Page, PageSize);
        // A filter change can leave you past the end of a shorter list; step
        // back rather than showing an empty page.
        if (Results.Count == 0 && Page > 1)
        {
            Page = LastPage;
            (Results, Total) = people.Search(Q, Page, PageSize);
        }
    }
}
