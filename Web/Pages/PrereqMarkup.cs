using System.Text;
using System.Text.RegularExpressions;
using CoursePlanner.Data;
using Microsoft.AspNetCore.Html;

namespace Web.Pages;

/// <summary>Marks up prerequisite text so each course code links to its course and carries its title. Titles are loaded once.</summary>
public class PrereqMarkup
{
    private readonly Dictionary<string, string> _titles;
    private readonly Regex _reference;

    public PrereqMarkup(CourseQueries db)
    {
        _titles = db.CourseTitles();

        // Longest subject first, so "ED PS" beats a shorter prefix.
        var subjects = _titles.Keys
            .Select(k => k[..k.LastIndexOf(' ')])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(s => s.Length)
            .Select(Regex.Escape);

        // A full code, or a bare number that inherits the subject before it ("MATH 1050 OR 1060").
        _reference = new Regex(
            @"\b(?<subj>" + string.Join("|", subjects) + @")\s*(?<num>\d{3,4}[A-Z]?)\b"
            + @"|\b(?<bare>\d{3,4}[A-Z]?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>The text with known course codes linked. Everything is HTML-encoded.</summary>
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
                // A bare number links only if the carried subject makes a real course.
                key = $"{carried} {match.Groups["bare"].Value}";
            }

            if (key is not null && _titles.TryGetValue(key, out var title))
            {
                var split = key.LastIndexOf(' ');
                var href = $"/course/{Uri.EscapeDataString(key[..split])}/{Uri.EscapeDataString(key[(split + 1)..])}";

                // data-title, not title: the stylesheet draws the tooltip without the native delay.
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
                // A number that is not a course stays plain.
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
