using HtmlAgilityPack;
public static class DescriptionScraper
{
    public static (string Description, string Prerequisites) Scrape(HtmlDocument doc)
    {
        var description = "";
        var prerequisites = "";

        var cards = doc.DocumentNode.SelectNodes("//div[contains(@class,'card')]");

        if (cards == null)
            return (description, prerequisites);

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
                var spans = bodyNode.SelectNodes(".//span");
                if (spans == null) continue;
                foreach (var span in spans)
                {
                    var spanText = HtmlUtils.CleanText(span.InnerText);
                    if (spanText.StartsWith("Prerequisites:"))
                    {
                        prerequisites = spanText.Substring("Prerequisites:".Length);
                        break;
                    }
                }
            }

            if (headerText == "Description")
            {
                var div = bodyNode.SelectSingleNode(".//div");
                description = HtmlUtils.CleanText(div?.InnerText);
            }
        }

        return (description, prerequisites);
    }
}
