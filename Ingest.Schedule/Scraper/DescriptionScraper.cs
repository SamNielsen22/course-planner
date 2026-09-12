using System.Text.RegularExpressions;
using HtmlAgilityPack;

/// <summary>
/// What a course's description page says about it. Title is the full name from
/// the page heading - "Manufacturing for Engineering Systems" - where the
/// class listing carries only the registrar's 30-character short title
/// ("Manufact for Eng Sys"); empty when the heading was not where expected.
/// </summary>
public record DetailsRecord(string Title, string Description, string Prerequisites, string RequirementDesignation);

public static partial class DescriptionScraper
{
    // "ME EN 2650 - Manufacturing for Engineering Systems": code, dash, name.
    [GeneratedRegex(@"^\s*[A-Z][A-Z ]*\s+\d+[A-Z]?\s*-\s*(?<title>.+?)\s*$")]
    private static partial Regex Heading();

    public static DetailsRecord Scrape(HtmlDocument doc)
    {
        var description = "";
        var prerequisites = "";
        var requirementDesignation = "";

        var heading = HtmlUtils.CleanText(doc.DocumentNode.SelectSingleNode("//h1")?.InnerText) ?? "";
        var title = Heading().Match(heading) is { Success: true } match ? match.Groups["title"].Value : "";

        var cards = doc.DocumentNode.SelectNodes("//div[contains(@class,'card')]");

        if (cards == null){
            Console.WriteLine("Could not find card in class description");
            return new DetailsRecord(title, description, prerequisites, requirementDesignation);
        }

        foreach (var card in cards)
        {
            var headerNode = card.SelectSingleNode(".//div[contains(@class,'card-header')]");
            if (headerNode == null)
                continue;

            var headerText = HtmlUtils.CleanText(headerNode.InnerText);

            var bodyNode = card.SelectSingleNode(".//div[contains(@class,'card-body')]");
            if (bodyNode == null)
                continue;

            if (headerText == "Enrollment Information")
            {
                // Rows are found by their label, not by scanning every span: the
                // enrollment requirement only sometimes carries a "Prerequisites:"
                // prefix, and the designation row sits in the same card.
                prerequisites = string.Join(" ", RowValues(bodyNode, "Enrollment Requirement"));
                if (prerequisites.StartsWith("Prerequisites:"))
                    prerequisites = prerequisites.Substring("Prerequisites:".Length);

                requirementDesignation = string.Join("; ", RowValues(bodyNode, "Requirement Designation"));
            }

            if (headerText == "Description")
            {
                var div = bodyNode.SelectSingleNode(".//div");
                description = HtmlUtils.CleanText(div?.InnerText);
            }
        }

        return new DetailsRecord(title, description, prerequisites, requirementDesignation);
    }

    /// <summary>
    /// The span values of one labelled row inside the Enrollment Information card,
    /// e.g. "Enrollment Requirement" or "Requirement Designation". A row is absent
    /// when a course has nothing for it, and may hold several spans - one per
    /// designation, separated by a line break.
    /// </summary>
    static IEnumerable<string> RowValues(HtmlNode bodyNode, string label)
    {
        var rows = bodyNode.SelectNodes(".//div[contains(@class,'row')]");
        if (rows == null)
            return [];

        foreach (var row in rows)
        {
            var labelNode = row.SelectSingleNode("./div[contains(@class,'fw-bold')]");
            if (labelNode == null)
                continue;
            if (!HtmlUtils.CleanText(labelNode.InnerText).StartsWith(label))
                continue;

            var spans = row.SelectNodes(".//span");
            if (spans == null)
                continue;

            return spans
                .Select(span => HtmlUtils.CleanText(span.InnerText))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct()
                .ToList();
        }

        return [];
    }
}
