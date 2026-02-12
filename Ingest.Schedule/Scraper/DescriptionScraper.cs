using HtmlAgilityPack;

public record CourseDetails(string Description, string Prerequisites);

public static class DescriptionScraper
{
    public static CourseDetails Scrape(HtmlDocument doc)
    {
        string description = "";
        string prerequisites = "";

        var cards = doc.DocumentNode.SelectNodes("//div[contains(@class,'card')]");

        if (cards == null)
            return new CourseDetails(description, prerequisites);

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

        return new CourseDetails(description, prerequisites);
    }
}
