using System.Security;
using System.Text;
using CoursePlanner.Data;
using Microsoft.AspNetCore.OutputCaching;
using Web.Pages;

namespace Web;

/// <summary>
/// The sitemap, so a search engine finds every course and professor page
/// without crawling through the search pages, which show nothing until
/// asked. An index of three: the handful of fixed pages, the courses, and
/// the professors. Built from the in-memory indexes and cached like any
/// other public page. Addresses use the host the request came in on, which
/// behind the tunnel is the site's own domain.
/// </summary>
public static class Sitemaps
{
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(6);

    public static void Map(WebApplication app)
    {
        app.MapGet("/sitemap.xml", (HttpContext http) => Xml(
            "sitemapindex",
            new[] { "/sitemap-pages.xml", "/sitemap-courses.xml", "/sitemap-professors.xml", "/sitemap-classes.xml" }
                .Select(path => $"<sitemap><loc>{Escape(Base(http) + path)}</loc></sitemap>")))
            .CacheOutput(p => p.Expire(Fresh));

        app.MapGet("/sitemap-pages.xml", (HttpContext http) => Xml(
            "urlset",
            new[] { "/", "/courses", "/Instructors", "/About" }.Select(path => Url(Base(http) + path))))
            .CacheOutput(p => p.Expire(Fresh));

        app.MapGet("/sitemap-courses.xml", (HttpContext http, SiteIndex site) => Xml(
            "urlset",
            site.Courses.Select(c => Url($"{Base(http)}/course/{Uri.EscapeDataString(c.Subject)}/{Uri.EscapeDataString(c.Number)}"))))
            .CacheOutput(p => p.Expire(Fresh));

        app.MapGet("/sitemap-professors.xml", (HttpContext http, InstructorIndex people) => Xml(
            "urlset",
            people.All.Select(p => Url($"{Base(http)}/professor/{p.Unid}/{Names.Slug(p.Name)}"))))
            .CacheOutput(p => p.Expire(Fresh));

        // A page per professor-and-class pair with grades: "Daniel Kopta CS 2420"
        // is what people search for, and this is the page that answers it.
        app.MapGet("/sitemap-classes.xml", (HttpContext http, GradeIndex grades, InstructorIndex people) =>
        {
            var names = people.All.ToDictionary(p => p.Unid, p => p.Name);
            return Xml("urlset", grades.GradedClasses
                .Where(c => names.ContainsKey(c.Unid))
                .Select(c => Url($"{Base(http)}/professor/{c.Unid}/{Names.Slug(names[c.Unid])}/{Names.CourseSlug(c.Subject, c.CourseNumber)}")));
        }).CacheOutput(p => p.Expire(Fresh));
    }

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
