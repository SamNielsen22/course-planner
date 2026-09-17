using Microsoft.AspNetCore.OutputCaching;
using CoursePlanner.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

/// <summary>The professor search, by name.</summary>
[OutputCache(Duration = 60)]
[ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
public class InstructorsModel(InstructorIndex people) : PageModel
{
    private const int PageSize = 24;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    // Bound from "pg": Razor Pages reserves "page" for routing.
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
        (Results, Total) = people.Search(Q, Page, PageSize);
        // Past the end of a shorter list: step back to the last page.
        if (Results.Count == 0 && Page > 1)
        {
            Page = LastPage;
            (Results, Total) = people.Search(Q, Page, PageSize);
        }
    }
}
