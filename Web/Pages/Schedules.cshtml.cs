using CoursePlanner.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;
using Web.Accounts;
using Web.Schedule;

namespace Web.Pages;

/// <summary>
/// The schedule home - where "Schedule Builder" leads. A guest has the one
/// schedule their browser keeps and continues straight into it; a signed-in
/// student sees every schedule they have listed, opens one for the builder,
/// creates another for a chosen term, renames or deletes. Personal, so never
/// cached.
/// </summary>
[ResponseCache(NoStore = true)]
public class SchedulesModel(ScheduleStore store, AccountsFeature accounts, SiteIndex site) : PageModel
{
    public bool SignedIn => store.SignedIn;

    /// <summary>Whether a sign-in can be offered: accounts on, Google configured.</summary>
    public bool CanSignIn => accounts.Google;

    /// <summary>Every term with sections, newest first - what a new schedule can be for.</summary>
    public IReadOnlyList<string> Terms { get; private set; } = [];

    /// <summary>Signed in: every schedule, most recently touched first.</summary>
    public IReadOnlyList<ScheduleSummary> Schedules { get; private set; } = [];

    /// <summary>True when the account database could not be reached.</summary>
    public bool Unavailable { get; private set; }

    /// <summary>A guest: the schedule their browser holds, so the page can say what is in progress.</summary>
    public BuiltSchedule Guest { get; private set; } = new();

    public void OnGet()
    {
        Terms = Pages.Terms.NewestFirst(site.Terms);
        if (!SignedIn) { Guest = store.Current; return; }
        try { Schedules = store.Mine(); }
        catch (NpgsqlException) { Unavailable = true; }
    }

    /// <summary>Signed in: a new schedule for a term and a campus, opened for the builder.</summary>
    public IActionResult OnPostNew(string? name, string? term, string? campus)
    {
        store.Create(name, Known(term), Campus.Known(campus));
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

    /// <summary>A term the catalogue has, or null - never a typed-in string.</summary>
    private string? Known(string? term) => term is not null && site.Terms.Contains(term) ? term : null;
}
