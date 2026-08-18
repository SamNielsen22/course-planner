using HtmlAgilityPack;

class ArchiveScraper
{
    public static List<string> Scrape(HtmlDocument doc)
    {
        var aTags = doc.DocumentNode.SelectNodes("//a[contains(@href,'class-schedule.app.utah.edu/main/')]");
        if (aTags == null)
        {
            Console.WriteLine($"WARNING: could not find term links in archive page");
            return new List<string>();
        }

        var termUrls = new HashSet<string>();

        foreach (var aTag in aTags)
        {
            var href = aTag.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(href))
                continue;

            if (!href.EndsWith("/")) // Some links end with index.html, Crawler wants the base url
                href = href.Substring(0, href.LastIndexOf('/') + 1);

            termUrls.Add(href);
        }

        // Term codes sort chronologically, so newest terms come first
        return termUrls.OrderByDescending(u => u).ToList();
    }
}
