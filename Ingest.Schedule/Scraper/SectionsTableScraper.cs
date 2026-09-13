using System.Text.RegularExpressions;
using HtmlAgilityPack;

/// <summary>
/// One row of the registrar's sections table. sections.html lists a whole
/// subject when the catalogue number is left blank, and it is the enrollment
/// side of the schedule: how many may enrol, how many have, how many are
/// waiting, and the class number a student types into registration. The
/// class list carries none of that beyond seats available and a yes or no on
/// the wait list - and it leaves the class number blank on a lecture that
/// students reach through its lab.
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
    /// <summary>
    /// The term the page is for, spelled as the catalogue spells it -
    /// "Fall2026" - read from the heading "Main Campus - Fall 2026 Class
    /// Schedule". Null when the heading is not there, which is the sign of a
    /// page that is not the table at all.
    /// </summary>
    public static string? Term(HtmlDocument doc)
    {
        var heading = doc.DocumentNode.SelectSingleNode("//h1");
        if (heading is null) return null;
        var match = Regex.Match(HtmlUtils.CleanText(heading.InnerText), @"\b(Spring|Summer|Fall)\s+(\d{4})\b");
        return match.Success ? match.Groups[1].Value + match.Groups[2].Value : null;
    }

    /// <summary>
    /// Every row of the table. Cells are read by their data-label rather than
    /// by position, because the title cell spans two columns.
    /// </summary>
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

    /// <summary>A cell's number, or null for a blank or a dash. Seats available runs negative when a section is over-enrolled.</summary>
    private static int? Number(Dictionary<string, string> cells, string label) =>
        int.TryParse(cells.GetValueOrDefault(label, ""), out var n) ? n : null;
}
