using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Web.Accounts;
using Web.Schedule;

namespace Web.Pages.Account;

/// <summary>
/// Sign in with Google or Apple. A POST sends the visitor to the provider; the
/// provider sends them back to the Callback handler, where an existing account
/// is signed in or a new one is made from the email the provider vouches for.
/// </summary>
[ResponseCache(NoStore = true)]
public class ExternalModel(AccountsFeature accounts, IServiceProvider services, ScheduleStore schedule) : PageModel
{
    // Arriving here by plain GET means nothing was in flight.
    public IActionResult OnGet() => RedirectToPage("/Account/Login");

    /// <summary>Off to the provider, from the sign-in page's button.</summary>
    public IActionResult OnPost(string provider, string? returnUrl) => Start(provider, returnUrl);

    /// <summary>
    /// Off to the provider, from the nav's "Sign in" link. A GET, because the
    /// nav sits on pages the output cache serves to everyone, where a per-user
    /// antiforgery form cannot live. Starting a sign-in has no effect of its
    /// own - the account only changes when the provider sends the person back,
    /// and that return carries the OAuth state the handler checks.
    /// </summary>
    public IActionResult OnGetStart(string provider, string? returnUrl) => Start(provider, returnUrl);

    private IActionResult Start(string provider, string? returnUrl)
    {
        if (!accounts.Enabled) return NotFound();
        var signIn = services.GetRequiredService<SignInManager<IdentityUser>>();
        var callback = Url.Page("/Account/External", "Callback", new { returnUrl });
        var properties = signIn.ConfigureExternalAuthenticationProperties(provider, callback);
        return new ChallengeResult(provider, properties);
    }

    public async Task<IActionResult> OnGetCallbackAsync(string? returnUrl, string? remoteError)
    {
        if (!accounts.Enabled) return NotFound();
        if (remoteError is not null) return Failed($"The sign-in provider said: {remoteError}");

        var signIn = services.GetRequiredService<SignInManager<IdentityUser>>();
        var users = services.GetRequiredService<UserManager<IdentityUser>>();
        var info = await signIn.GetExternalLoginInfoAsync();
        if (info is null) return RedirectToPage("/Account/Login");

        var result = await signIn.ExternalLoginSignInAsync(
            info.LoginProvider, info.ProviderKey, isPersistent: true, bypassTwoFactor: true);
        IdentityUser? user;
        if (result.Succeeded)
        {
            user = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        }
        else if (result.IsLockedOut)
        {
            return Failed("Too many attempts. Try again in a few minutes.");
        }
        else
        {
            // First time with this provider. Google verifies the address it
            // sends, so an existing account with the same email is this
            // person's and gets the login attached; otherwise a new account is
            // made, with no password of its own.
            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            if (string.IsNullOrEmpty(email) || !email.Contains('@'))
                return Failed($"{info.ProviderDisplayName} didn't share an email address, so an account can't be made.");

            user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
                var created = await users.CreateAsync(user);
                if (!created.Succeeded) return Failed(string.Join(" ", created.Errors.Select(e => e.Description)));
            }
            var linked = await users.AddLoginAsync(user, info);
            if (!linked.Succeeded) return Failed(string.Join(" ", linked.Errors.Select(e => e.Description)));
            await signIn.SignInAsync(user, isPersistent: true, info.LoginProvider);
        }

        if (user is not null) schedule.AdoptCookie(user.Id);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/builder");
    }

    private IActionResult Failed(string message) => RedirectToPage("/Account/Login", new { error = message });
}
