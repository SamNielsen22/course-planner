using CoursePlanner.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Web.Accounts;

var builder = WebApplication.CreateBuilder(args);

// Behind the Cloudflare tunnel the app sees plain HTTP; the forwarded headers
// carry the visitor's real scheme and address. Trusted from localhost only.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var connectionString = builder.Configuration.GetConnectionString("CoursePlanner")
                       ?? "Data Source=../data/courseplanner.db";
builder.Services.AddSingleton(new CourseQueries(connectionString));
builder.Services.AddRazorPages();

// Pages gzip about ten to one, and a home upload link is the bottleneck.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});

// Indexes built once at startup: course titles for prerequisite links, the
// building list, the blank-search order, every grade row, every instructor,
// the reference lists, and each term's sections.
builder.Services.AddSingleton<Web.Pages.PrereqMarkup>();
builder.Services.AddSingleton<Web.Pages.Buildings>();
builder.Services.AddSingleton<Web.Pages.Spotlight>();
builder.Services.AddSingleton<GradeIndex>();
builder.Services.AddSingleton<InstructorIndex>();
// Unlisted: the site answers anyone with the address, but tells every search
// engine to stay out and serves no sitemap. Set Site:Unlisted to false to be
// findable.
builder.Services.AddSingleton(new Web.Unlisted(builder.Configuration.GetValue("Site:Unlisted", true)));

// Terms named in Terms:Hidden stay stored but off every picker.
builder.Services.AddSingleton(new HiddenTerms(
    (builder.Configuration.GetSection("Terms:Hidden").Get<string[]>() ?? []).ToHashSet()));
builder.Services.AddSingleton<SiteIndex>();
builder.Services.AddSingleton<SectionIndex>();

// A guest's schedule travels in a signed cookie, so any server can answer any
// request and a restart empties nothing.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Web.Schedule.ScheduleStore>();

// Accounts exist only when a UserData (Postgres) connection is configured.
// The public pages never need it.
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
        // Length over character classes. The email is the username.
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

    // Google is the only way to sign in; the button appears once the credentials are configured.
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

// Both cookies are signed with this key ring, and every server must share it or
// each rejects the others' cookies and visitors are signed out as they move
// between machines. With accounts on it lives in the Postgres they already
// share, which works wherever the servers are; a KeysPath overrides that for a
// single machine with no database. With neither, keys are per-process and a
// restart signs everyone out.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
var protection = builder.Services.AddDataProtection().SetApplicationName("CourseCompass");
if (!string.IsNullOrEmpty(keysPath))
    protection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
else if (!string.IsNullOrEmpty(userData))
    protection.PersistKeysToDbContext<UserDbContext>();

// Public pages are rendered once a minute and replayed. Opt-in per page; the builder is never cached.
builder.Services.AddOutputCache();

// The Add button posts from inside the GET search form, so the antiforgery token
// travels as a header. Its cookie is not marked Secure by default, unlike the
// schedule and account cookies, so it is set to follow the request's scheme.
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "RequestVerificationToken";
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

var app = builder.Build();

// Compression sits outside the output cache: the cache holds one plain copy and compression happens on the way out.
app.UseForwardedHeaders();

// A few free security headers on every response: no MIME sniffing, no framing
// (clickjacking), and a trimmed referrer.
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["X-Frame-Options"] = "DENY";
    await next();
});
app.UseResponseCompression();

// Assets are requested with a content hash (asp-append-version), so a versioned
// URL can be cached hard: a changed file gets a new URL. An unversioned request
// for the same file, say a bookmarked /css/site.css, gets an hour instead.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl =
            ctx.Context.Request.Query.ContainsKey("v")
                ? "public,max-age=31536000,immutable"
                : "public,max-age=3600",
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// No caching in development, so a change shows on the next reload.
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

// Lets the tests host the site in-process.
public partial class Program { }
