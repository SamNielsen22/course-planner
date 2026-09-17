using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Web.Accounts;

namespace Web.Pages.Account;

/// <summary>The account page: who you are, sign out, delete. No [Authorize], since there is no scheme when accounts are off.</summary>
[ResponseCache(NoStore = true)]
public class IndexModel(AccountsFeature accounts, IServiceProvider services) : PageModel
{
    public string Email { get; private set; } = "";

    /// <summary>How they signed in, such as "Google".</summary>
    public string? Provider { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!accounts.Enabled) return NotFound();
        if (User.Identity?.IsAuthenticated != true) return RedirectToPage("/Account/Login");

        var users = services.GetRequiredService<UserManager<IdentityUser>>();
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            // A cookie for an account that no longer exists; clear it.
            await services.GetRequiredService<SignInManager<IdentityUser>>().SignOutAsync();
            return RedirectToPage("/Account/Login");
        }

        Email = user.Email ?? "";
        Provider = (await users.GetLoginsAsync(user)).FirstOrDefault()?.ProviderDisplayName;
        return Page();
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        if (!accounts.Enabled) return NotFound();
        await services.GetRequiredService<SignInManager<IdentityUser>>().SignOutAsync();
        return LocalRedirect("/");
    }

    /// <summary>Deletes the account and its schedules.</summary>
    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!accounts.Enabled) return NotFound();
        var users = services.GetRequiredService<UserManager<IdentityUser>>();
        var user = await users.GetUserAsync(User);
        if (user is null) return RedirectToPage("/Account/Login");

        var db = services.GetRequiredService<UserDbContext>();
        db.Schedules.RemoveRange(db.Schedules.Where(s => s.UserId == user.Id));
        await db.SaveChangesAsync();
        await users.DeleteAsync(user);
        await services.GetRequiredService<SignInManager<IdentityUser>>().SignOutAsync();
        return LocalRedirect("/");
    }
}
