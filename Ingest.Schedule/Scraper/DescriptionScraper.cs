using HtmlAgilityPack;

public record DetailsRecord(string Description, string Prerequisites);

public static class DescriptionScraper
{
    public static DetailsRecord Scrape(string url)
    {
        var doc = ScraperUtils.LoadFromUrl(url);
        var description = "";
        var prerequisites = "";

        var cards = doc.DocumentNode.SelectNodes("//div[contains(@class,'card')]");

        if (cards == null)
            return new DetailsRecord(description, prerequisites);

        foreach (var card in cards)
        {
            var headerNode = card.SelectSingleNode(".//div[contains(@class,'card-header')]");
            if (headerNode == null)
                continue;

            var headerText = ScraperUtils.CleanText(headerNode.InnerText);

            var bodyNode = card.SelectSingleNode(".//div[contains(@class,'card-body')]");
            if (bodyNode == null)
                continue;

            if (headerText == "Enrollment Information")
            {
                var spans = bodyNode.SelectNodes(".//span");
                if (spans == null) continue;
                foreach (var span in spans)
                {
                    var spanText = ScraperUtils.CleanText(span.InnerText);
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
                description = ScraperUtils.CleanText(div?.InnerText);
            }
        }

        return new DetailsRecord(description, prerequisites);
    }
}
