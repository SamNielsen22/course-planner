using HtmlAgilityPack;
public record DetailsRecord(string Description, string Prerequisites, string RequirementDesignation);
public static class DescriptionScraper
{
    public static DetailsRecord Scrape(HtmlDocument doc)
    {
        var description = "";
        var prerequisites = "";
        var requirementDesignation = "";

        var cards = doc.DocumentNode.SelectNodes("//div[contains(@class,'card')]");

        if (cards == null){
            Console.WriteLine("Could not find card in class description");
            return new DetailsRecord(description, prerequisites, requirementDesignation);
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

        return new DetailsRecord(description, prerequisites, requirementDesignation);
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
