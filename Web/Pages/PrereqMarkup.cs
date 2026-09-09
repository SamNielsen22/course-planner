using System.Text;
using System.Text.RegularExpressions;
using CoursePlanner.Data;
using Microsoft.AspNetCore.Html;

namespace Web.Pages;

/// <summary>
/// Turns a prerequisite string into markup where every course reference carries
/// its title.
///
/// The catalogue is loaded once and held: 13,000 titles keyed "SUBJ NUMBER", so
/// resolving a reference is a dictionary hit rather than a query. Re-reading it
/// per card would be one round trip for every code on every row of every page.
/// </summary>
public class PrereqMarkup
{
    private readonly Dictionary<string, string> _titles;
    private readonly Regex _reference;

    public PrereqMarkup(CourseQueries db)
    {
        _titles = db.CourseTitles();

        // Longest subject first, so ED PS wins over any shorter code that
        // prefixes it. \d{3,4} because the catalogue has 3-digit numbers, and
        // because \d{1,4} would match the "2" in "ME EN 2-5XXX".
        var subjects = _titles.Keys
            .Select(k => k[..k.LastIndexOf(' ')])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(s => s.Length)
            .Select(Regex.Escape);

        // Either a full code, or a bare number that inherits the subject before
        // it. The registrar writes "MATH 1050 OR 1060 OR 1080 OR 1210", and only
        // the first of those carries a subject of its own.
        _reference = new Regex(
            @"\b(?<subj>" + string.Join("|", subjects) + @")\s*(?<num>\d{3,4}[A-Z]?)\b"
            + @"|\b(?<bare>\d{3,4}[A-Z]?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>
    /// The prerequisite text with known course codes wrapped so they can be
    /// hovered. Text outside a match is HTML-encoded; nothing from the database
    /// is emitted raw.
    /// </summary>
    public IHtmlContent Annotate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return HtmlString.Empty;

        var html = new StringBuilder(text.Length + 64);
        var cursor = 0;
        string? carried = null;

        foreach (Match match in _reference.Matches(text))
        {
            html.Append(HtmlEncoder(text[cursor..match.Index]));

            string? key = null;
            if (match.Groups["subj"].Success)
            {
                carried = Collapse(match.Groups["subj"].Value);
                key = $"{carried} {match.Groups["num"].Value}";
            }
            else if (carried is not null)
            {
                // A bare number only becomes a link if the carried subject makes
                // a course that exists. That guard is what keeps "a score of 550"
                // from turning into a course reference.
                key = $"{carried} {match.Groups["bare"].Value}";
            }

            if (key is not null && _titles.TryGetValue(key, out var title))
            {
                // The key is the full code even when the text showed only a bare
                // number, so the link and the label both name the whole course.
                var split = key.LastIndexOf(' ');
                var href = $"/course/{Uri.EscapeDataString(key[..split])}/{Uri.EscapeDataString(key[(split + 1)..])}";

                // data-title, not title: the native tooltip waits about a
                // second before appearing. The stylesheet draws this one, and
                // aria-label carries the same text for a screen reader.
                html.Append("<a class=\"course-ref\" href=\"")
                    .Append(HtmlEncoder(href))
                    .Append("\" data-title=\"")
                    .Append(HtmlEncoder(title))
                    .Append("\" aria-label=\"")
                    .Append(HtmlEncoder($"{key}: {title}"))
                    .Append("\">")
                    .Append(HtmlEncoder(match.Value))
                    .Append("</a>");
            }
            else
            {
                // A number that resolves to nothing - a renumbered course, a test
                // score, a credit count - is left plain rather than offered as
                // something to hover.
                html.Append(HtmlEncoder(match.Value));
            }

            cursor = match.Index + match.Length;
        }

        html.Append(HtmlEncoder(text[cursor..]));
        return new HtmlString(html.ToString());
    }

    /// <summary>"ED  PS" and "ed ps" both key as "ED PS".</summary>
    private static string Collapse(string subject) =>
        string.Join(' ', subject.ToUpperInvariant()
                                .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string HtmlEncoder(string value) =>
        System.Net.WebUtility.HtmlEncode(value);
}
