using HtmlAgilityPack;

class Program
{
    const string ArchiveUrl = "https://registrar.utah.edu/class-schedule-archives.php";
    const int TermsToScrape = 15;

    static void Main()
    {
        Console.WriteLine($"Fetching terms from {ArchiveUrl}");
        var http = new HttpClient();
        var doc = new HtmlDocument();
        doc.LoadHtml(http.GetStringAsync(ArchiveUrl).Result);

        var termUrls = ArchiveScraper.Scrape(doc).Take(TermsToScrape).ToList();
        Console.WriteLine($"Found {termUrls.Count} terms");

        foreach (var termUrl in termUrls)
        {
            Console.WriteLine($"Crawling term {termUrl}");
            new Crawler().Run(termUrl);
        }
    }
}
