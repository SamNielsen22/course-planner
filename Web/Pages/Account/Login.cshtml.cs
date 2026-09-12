using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Web.Accounts;

namespace Web.Pages.Account;

/// <summary>
/// The one way in: sign in with Google. There is no password - the button
/// sends the visitor to Google (see <see cref="ExternalModel"/>), which
/// creates the account on first use. Identity's services are resolved by hand
/// so the page still constructs, and answers 404, when accounts are off.
/// </summary>
[ResponseCache(NoStore = true)]
public class LoginModel(AccountsFeature accounts, IServiceProvider services) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    /// <summary>A message from a failed Google sign-in, carried back here to show.</summary>
    [BindProperty(SupportsGet = true)] public string? Error { get; set; }

    /// <summary>The configured sign-in providers - Google, when its credentials are set.</summary>
    public IReadOnlyList<(string Name, string Display)> Providers { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!accounts.Enabled) return NotFound();
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Account/Index");
        if (!string.IsNullOrEmpty(Error)) ModelState.AddModelError("", Error);
        var schemes = await services.GetRequiredService<SignInManager<IdentityUser>>().GetExternalAuthenticationSchemesAsync();
        Providers = schemes.Select(s => (s.Name, s.DisplayName ?? s.Name)).ToList();
        return Page();
    }
}
