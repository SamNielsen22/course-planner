using CoursePlanner.Data;

var builder = WebApplication.CreateBuilder(args);

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
app.UseResponseCompression();
app.UseStaticFiles();
app.UseRouting();

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

app.Run();
