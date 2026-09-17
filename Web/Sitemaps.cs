using System.Security;
using System.Text;
using CoursePlanner.Data;
using Microsoft.AspNetCore.OutputCaching;
using Web.Pages;

namespace Web;

/// <summary>Whether the site is hidden from search engines (Site:Unlisted).</summary>
public sealed record Unlisted(bool Yes);

/// <summary>
/// robots.txt and the sitemaps: fixed pages, courses, professors, and
/// professor-and-class pages. Unlisted, robots refuses everything and the
/// sitemaps are not served at all.
/// </summary>
public static class Sitemaps
{
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(6);

    public static void Map(WebApplication app)
    {
        // Written here rather than as a static file so the unlisted switch can
        // change it. Unlisted, it is a flat refusal with no sitemap named.
        app.MapGet("/robots.txt", (HttpContext http, Unlisted unlisted) => Results.Text(
            unlisted.Yes
                ? "User-agent: *\nDisallow: /\n"
                : "User-agent: *\nAllow: /\nDisallow: /builder\nDisallow: /schedules\n"
                  + "Disallow: /BuilderSchedule\nDisallow: /account\n\n"
                  + $"Sitemap: {Base(http)}/sitemap.xml\n",
            "text/plain; charset=utf-8"));

        app.MapGet("/sitemap.xml", (HttpContext http, Unlisted unlisted) => Hidden(unlisted) ?? Xml(
            "sitemapindex",
            new[] { "/sitemap-pages.xml", "/sitemap-courses.xml", "/sitemap-professors.xml", "/sitemap-classes.xml" }
                .Select(path => $"<sitemap><loc>{Escape(Base(http) + path)}</loc></sitemap>")))
            .CacheOutput(p => p.Expire(Fresh));

        app.MapGet("/sitemap-pages.xml", (HttpContext http, Unlisted unlisted) => Hidden(unlisted) ?? Xml(
            "urlset",
            new[] { "/", "/courses", "/Instructors", "/About" }.Select(path => Url(Base(http) + path))))
            .CacheOutput(p => p.Expire(Fresh));

        app.MapGet("/sitemap-courses.xml", (HttpContext http, SiteIndex site, Unlisted unlisted) => Hidden(unlisted) ?? Xml(
            "urlset",
            site.Courses.Select(c => Url($"{Base(http)}/course/{Uri.EscapeDataString(c.Subject)}/{Uri.EscapeDataString(c.Number)}"))))
            .CacheOutput(p => p.Expire(Fresh));

        app.MapGet("/sitemap-professors.xml", (HttpContext http, InstructorIndex people, Unlisted unlisted) => Hidden(unlisted) ?? Xml(
            "urlset",
            people.All.Select(p => Url($"{Base(http)}/professor/{p.Unid}/{Names.Slug(p.Name)}"))))
            .CacheOutput(p => p.Expire(Fresh));

        // One page per professor-and-class pair with grades.
        app.MapGet("/sitemap-classes.xml", (HttpContext http, GradeIndex grades, InstructorIndex people, Unlisted unlisted) =>
        {
            if (Hidden(unlisted) is { } hidden) return hidden;
            var names = people.All.ToDictionary(p => p.Unid, p => p.Name);
            return Xml("urlset", grades.GradedClasses
                .Where(c => names.ContainsKey(c.Unid))
                .Select(c => Url($"{Base(http)}/professor/{c.Unid}/{Names.Slug(names[c.Unid])}/{Names.CourseSlug(c.Subject, c.CourseNumber)}")));
        }).CacheOutput(p => p.Expire(Fresh));
    }

    /// <summary>404 while unlisted, so a crawler is given no list of pages to follow.</summary>
    private static IResult? Hidden(Unlisted unlisted) => unlisted.Yes ? Results.NotFound() : null;

    private static string Base(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";

    private static string Url(string loc) => $"<url><loc>{Escape(loc)}</loc></url>";

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

    private static IResult Xml(string root, IEnumerable<string> entries)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            .Append('<').Append(root).Append(" xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
        foreach (var entry in entries) xml.Append(entry).Append('\n');
        xml.Append("</").Append(root).Append(">\n");
        return Results.Content(xml.ToString(), "application/xml; charset=utf-8");
    }
}
