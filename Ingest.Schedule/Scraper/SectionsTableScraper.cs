using System.Text.RegularExpressions;
using HtmlAgilityPack;

/// <summary>
/// One row of the registrar's sections table - the enrollment side of the
/// schedule. sections.html lists a whole subject when the catalogue number is
/// left blank; the class list carries none of this but the seat count.
/// </summary>
record SectionCounts(
    string Subject,
    string CourseNumber,
    string SectionNumber,
    string? ClassNumber,
    int? EnrollmentCap,
    int? Waitlist,
    int? Enrolled,
    int? SeatsAvailable
);

static class SectionsTableScraper
{
    /// <summary>The term from the heading, as "Fall2026". Null when this is not a sections table.</summary>
    public static string? Term(HtmlDocument doc)
    {
        var heading = doc.DocumentNode.SelectSingleNode("//h1");
        if (heading is null) return null;
        var match = Regex.Match(HtmlUtils.CleanText(heading.InnerText), @"\b(Spring|Summer|Fall)\s+(\d{4})\b");
        return match.Success ? match.Groups[1].Value + match.Groups[2].Value : null;
    }

    /// <summary>Every row. Cells are read by data-label, since the title cell spans two columns.</summary>
    public static List<SectionCounts> Scrape(HtmlDocument doc)
    {
        var rows = new List<SectionCounts>();
        foreach (var tr in doc.DocumentNode.SelectNodes("//table//tr[td]") ?? Enumerable.Empty<HtmlNode>())
        {
            var cells = new Dictionary<string, string>();
            foreach (var td in tr.SelectNodes("./td") ?? Enumerable.Empty<HtmlNode>())
            {
                var label = td.GetAttributeValue("data-label", "");
                if (label.Length > 0) cells[label] = HtmlUtils.CleanText(td.InnerText);
            }

            var subject = cells.GetValueOrDefault("Subject", "");
            var course = cells.GetValueOrDefault("Catalog #", "");
            var section = cells.GetValueOrDefault("Section", "");
            if (subject.Length == 0 || course.Length == 0 || section.Length == 0) continue;

            var classNumber = cells.GetValueOrDefault("Class #", "");
            rows.Add(new SectionCounts(
                subject, course, section,
                classNumber.Length > 0 ? classNumber : null,
                Number(cells, "Enrollment Cap"),
                Number(cells, "Wait List"),
                Number(cells, "Currently Enrolled"),
                Number(cells, "Seats Available")));
        }
        return rows;
    }

    /// <summary>A cell's number, or null for a blank or a dash. Seats can be negative.</summary>
    private static int? Number(Dictionary<string, string> cells, string label) =>
        int.TryParse(cells.GetValueOrDefault(label, ""), out var n) ? n : null;
}
