using CoursePlanner.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;
using Web.Accounts;
using Web.Schedule;

namespace Web.Pages;

/// <summary>The schedule home. A guest continues into their one schedule; a student manages several. Never cached.</summary>
[ResponseCache(NoStore = true)]
public class SchedulesModel(ScheduleStore store, AccountsFeature accounts, SiteIndex site) : PageModel
{
    public bool SignedIn => store.SignedIn;

    /// <summary>Whether sign-in is configured.</summary>
    public bool CanSignIn => accounts.Google;

    /// <summary>Terms a schedule can be for, newest first.</summary>
    public IReadOnlyList<string> Terms { get; private set; } = [];

    /// <summary>A student's schedules, most recently touched first.</summary>
    public IReadOnlyList<ScheduleSummary> Schedules { get; private set; } = [];

    /// <summary>True when the account database could not be reached.</summary>
    public bool Unavailable { get; private set; }

    /// <summary>A guest's current campus and term, offered first in the dialog.</summary>
    public string GuestCampus { get; private set; } = Campus.Main;
    public string? GuestTerm { get; private set; }

    public void OnGet()
    {
        Terms = Pages.Terms.NewestFirst(site.Terms);
        if (!SignedIn)
        {
            var guest = store.Current;
            GuestCampus = guest.Campus;
            GuestTerm = guest.Term ?? Terms.FirstOrDefault();
            return;
        }
        try { Schedules = store.Mine(); }
        catch (NpgsqlException) { Unavailable = true; }
    }

    /// <summary>A new schedule, opened for the builder.</summary>
    public IActionResult OnPostNew(string? name, string? term, string? campus)
    {
        store.Create(name, Known(term), Campus.Known(campus));
        return RedirectToPage("/BuilderAdd");
    }

    /// <summary>A guest's way in, with their term and campus.</summary>
    public IActionResult OnPostGuest(string? campus, string? term)
    {
        store.StartGuest(Campus.Known(campus), Known(term));
        return RedirectToPage("/BuilderAdd");
    }

    public IActionResult OnPostOpen(string id)
    {
        store.Open(id);
        return RedirectToPage("/BuilderAdd");
    }

    public IActionResult OnPostRename(string id, string? name)
    {
        store.Rename(id, name);
        return RedirectToPage();
    }

    public IActionResult OnPostDelete(string id)
    {
        store.Delete(id);
        return RedirectToPage();
    }

    /// <summary>A known term, or null.</summary>
    private string? Known(string? term) => term is not null && site.Terms.Contains(term) ? term : null;
}
