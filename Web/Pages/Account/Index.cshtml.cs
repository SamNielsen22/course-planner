using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Web.Accounts;
using Web.Schedule;

namespace Web.Pages.Account;

/// <summary>
/// The signed-in person's page: who they are, what they have saved, sign out,
/// and - behind a disclosure - delete the account. No [Authorize]: with
/// accounts off there is no authentication scheme for it to use, so the page
/// answers 404 itself instead.
/// </summary>
[ResponseCache(NoStore = true)]
public class IndexModel(AccountsFeature accounts, IServiceProvider services, ScheduleStore schedule) : PageModel
{
    public string Email { get; private set; } = "";

    /// <summary>"Google" - how they signed in. Null for an account with no external login.</summary>
    public string? Provider { get; private set; }

    /// <summary>Their schedules, so the page can say how many.</summary>
    public IReadOnlyList<ScheduleSummary> Schedules { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!accounts.Enabled) return NotFound();
        if (User.Identity?.IsAuthenticated != true) return RedirectToPage("/Account/Login");

        var users = services.GetRequiredService<UserManager<IdentityUser>>();
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            // A sign-in cookie for an account that no longer exists - deleted,
            // most likely. Clear it, or the sign-in page would bounce back here.
            await services.GetRequiredService<SignInManager<IdentityUser>>().SignOutAsync();
            return RedirectToPage("/Account/Login");
        }

        Email = user.Email ?? "";
        Provider = (await users.GetLoginsAsync(user)).FirstOrDefault()?.ProviderDisplayName;
        Schedules = schedule.Mine();
        return Page();
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        if (!accounts.Enabled) return NotFound();
        await services.GetRequiredService<SignInManager<IdentityUser>>().SignOutAsync();
        return LocalRedirect("/");
    }

    /// <summary>Deletes the person and their saved schedule. The checkbox is the confirmation.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(bool confirm)
    {
        if (!accounts.Enabled) return NotFound();
        var users = services.GetRequiredService<UserManager<IdentityUser>>();
        var user = await users.GetUserAsync(User);
        if (user is null) return RedirectToPage("/Account/Login");
        if (!confirm)
        {
            Email = user.Email ?? "";
            Provider = (await users.GetLoginsAsync(user)).FirstOrDefault()?.ProviderDisplayName;
            Schedules = schedule.Mine();
            ModelState.AddModelError("", "Tick the box to confirm.");
            return Page();
        }

        var db = services.GetRequiredService<UserDbContext>();
        db.Schedules.RemoveRange(db.Schedules.Where(s => s.UserId == user.Id));
        await db.SaveChangesAsync();
        await users.DeleteAsync(user);
        await services.GetRequiredService<SignInManager<IdentityUser>>().SignOutAsync();
        return LocalRedirect("/");
    }
}
