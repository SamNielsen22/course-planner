using CoursePlanner.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Web.Accounts;

var builder = WebApplication.CreateBuilder(args);

// Behind Cloudflare Tunnel the app sees plain HTTP from cloudflared on the
// same machine, and the tunnel says in X-Forwarded-Proto what the visitor
// actually used. Without this every absolute URL the app builds - the Google
// sign-in callback above all - would say http://, and Google would refuse it
// against the https:// address it has on file. Honoured only from loopback,
// the default, so the header means nothing from anywhere else.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var connectionString = builder.Configuration.GetConnectionString("CoursePlanner")
                       ?? "Data Source=../data/courseplanner.db";
builder.Services.AddSingleton(new CourseQueries(connectionString));
builder.Services.AddRazorPages();

// The builder and professor pages are 65-70KB of HTML that gzips to about 6KB.
// Without this every byte goes out uncompressed - measured, no Content-Encoding
// on any response - which is what makes a home upload link the bottleneck
// long before the CPU is. Brotli first; gzip for anything that lacks it.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});

// Built once and held: it loads the whole catalogue of titles so a prerequisite
// string can be marked up with dictionary lookups instead of queries.
builder.Services.AddSingleton<Web.Pages.PrereqMarkup>();

// Same again for the University's building list, so a room code can carry
// its building's name on hover and its street address into a calendar.
builder.Services.AddSingleton<Web.Pages.Buildings>();
builder.Services.AddSingleton<Web.Pages.Spotlight>();

// Every published grade row with its reconstruction, held in memory. The
// reconstruction runs once here, at startup; every grade figure on the site
// is then read or pooled from this one place.
builder.Services.AddSingleton<GradeIndex>();

// Same reason: the professor search ranks people by averages that used to
// cost ~450ms to compute across the whole database on every request.
builder.Services.AddSingleton<InstructorIndex>();

// Reference data - terms, subjects, designations, the About figures - read
// once instead of on every request; and each term's sections held in memory,
// refreshed every minute because the seat crawl rewrites seat counts.
// Terms crawled ahead of their registration window stay stored but off every
// picker until they are taken off this list.
builder.Services.AddSingleton(new HiddenTerms(
    (builder.Configuration.GetSection("Terms:Hidden").Get<string[]>() ?? []).ToHashSet()));
builder.Services.AddSingleton<SiteIndex>();
builder.Services.AddSingleton<SectionIndex>();

// The schedule under construction travels in a signed cookie, so no server
// remembers anything about a visitor between requests. That is what lets a
// second server answer the next request as well as the first did, and what
// stops a restart or an idle timeout from emptying someone's cart. The cookie
// is signed with the data-protection key ring; on more than one machine that
// ring must be shared (PersistKeysToFileSystem on a common path), or each box
// will reject the others' cookies.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Web.Schedule.ScheduleStore>();

// Accounts are optional. They exist when a UserData (Postgres) connection is
// configured; without one the account pages answer 404 and nothing else
// changes. The public site never needs the user database - if it is
// unreachable, signing in fails and the pages keep serving.
var userData = builder.Configuration.GetConnectionString("UserData");
var google = builder.Configuration.GetSection("Authentication:Google");
builder.Services.AddSingleton(new AccountsFeature(
    Enabled: !string.IsNullOrEmpty(userData),
    Google: !string.IsNullOrEmpty(userData) && !string.IsNullOrEmpty(google["ClientId"])));
if (!string.IsNullOrEmpty(userData))
{
    builder.Services.AddDbContext<UserDbContext>(options => options.UseNpgsql(userData));
    builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
    {
        // Length over character classes, as current guidance has it. The
        // email is the username, so it has to be unique.
        options.Password.RequiredLength = 10;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireDigit = false;
        options.User.RequireUniqueEmail = true;
    }).AddEntityFrameworkStores<UserDbContext>();
    builder.Services.ConfigureApplicationCookie(options =>
    {
        options.Cookie.Name = "account";
        options.LoginPath = "/account/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(180);
        options.SlidingExpiration = true;
    });

    // Sign in with Google - the only way in. The button exists only when the
    // credentials are configured (user-secrets here, environment variables on
    // a server), so before they are set the sign-in page says so rather than
    // offering a dead button.
    var external = builder.Services.AddAuthentication();
    if (!string.IsNullOrEmpty(google["ClientId"]))
    {
        external.AddGoogle(options =>
        {
            options.ClientId = google["ClientId"]!;
            options.ClientSecret = google["ClientSecret"] ?? "";
        });
    }
}

// Both cookies - the schedule and the sign-in - are signed with this key ring.
// One application name and one key folder shared across every server, or each
// box rejects the others' cookies. Unset, keys live in the user profile, which
// is right for a single machine.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
var protection = builder.Services.AddDataProtection().SetApplicationName("CourseCompass");
if (!string.IsNullOrEmpty(keysPath)) protection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// Pages that read the same for every visitor - home, course and professor
// pages, both searches, About - are rendered once a minute and replayed.
// Opt-in per page: the builder is never cached, it shows YOUR schedule.
builder.Services.AddOutputCache();

// The Add button lives inside the search form, which is a GET, so no hidden
// antiforgery field reaches it. Accepting the token as a header lets the
// button post without nesting a second form inside the first, which HTML
// forbids.
builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");

var app = builder.Build();

// The site is server-rendered: pages call CourseQueries directly rather than
// going back out over HTTP.
// Compression wraps the whole pipeline so static files get it too. It sits
// outside the output cache on purpose: the cache stores one uncompressed copy
// and compression happens on the way out, so no Accept-Encoding variant is
// needed and a stale-but-compressed body cannot be served to the wrong client.
app.UseForwardedHeaders();
app.UseResponseCompression();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Caching only outside development. Locally, a page cached for sixty seconds
// keeps showing the build before the one you just made, which reads as the
// change not having landed. Production keeps the full cache.
if (app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        await next();
    });
}
else
{
    app.UseOutputCache();
}

app.MapRazorPages();
Web.Sitemaps.Map(app);

app.Run();

// Lets the end-to-end tests host the site in-process (WebApplicationFactory<Program>).
public partial class Program { }
