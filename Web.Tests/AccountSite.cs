using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Accounts;

namespace Web.Tests;

/// <summary>
/// The site with accounts on and a fake sign-in, so the account paths can be
/// driven without Google. The test user's rows are removed after each test.
/// Skipped where the UserData connection string is not configured.
/// </summary>
public sealed class AccountSite : WebApplicationFactory<Program>
{
    public const string UserId = "e2e-test-user";

    private static readonly string? Connection = new ConfigurationBuilder()
        .AddUserSecrets("47aa7a70-220e-4c3b-b9b7-12b386b6efcf")   // Web.csproj's UserSecretsId
        .Build()
        .GetConnectionString("UserData");

    public static bool Available => !string.IsNullOrEmpty(Connection);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:CoursePlanner", $"Data Source={Site.DatabasePath};Mode=ReadOnly");
        builder.UseSetting("ConnectionStrings:UserData", Connection);
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestSignIn>("Test", _ => { });
        });
    }

    public Browser Visitor() => new(CreateClient(new WebApplicationFactoryClientOptions
    {
        HandleCookies = true,
        AllowAutoRedirect = false,
    }));

    /// <summary>Nothing of the test user's stays in Postgres: each test starts and ends with this.</summary>
    public void Reset()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        db.Schedules.Where(s => s.UserId == UserId).ExecuteDelete();
    }

    private sealed class TestSignIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId), new Claim(ClaimTypes.Name, "e2e")], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}

/// <summary>A fact that runs only where the account database is reachable.</summary>
public sealed class AccountFactAttribute : FactAttribute
{
    public AccountFactAttribute()
    {
        if (!AccountSite.Available) Skip = "ConnectionStrings:UserData is not in user-secrets on this machine";
    }
}
