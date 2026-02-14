using HtmlAgilityPack;

public class Crawler
{
    private string baseUrl = "";
    static readonly HttpClient http = new HttpClient();
    public void Run(string url)
    {
        baseUrl = url;
        var indexUrl = baseUrl + "index.html";

        Console.WriteLine($"Fetching subjects from {indexUrl}");
        var queries = SubjectScraper.Scrape(LoadFromUrl(indexUrl));
        Console.WriteLine($"Found {queries.Count} subjects");

        var seenCourses = new HashSet<string>();
        foreach (var query in queries)
        {
            var classListUrl = baseUrl + "class_list.html?" + query;
            var classListDoc = LoadFromUrl(classListUrl);

            var alert = classListDoc.DocumentNode.SelectSingleNode(
                "//div[contains(@class,'alert') and contains(.,'divided by credit and noncredit')]"
            );
            if (alert != null) // Some subject pages lead to a credit/noncredit menu
            {
                var extraQueries = SubjectScraper.Scrape(classListDoc);
                foreach (var extraQuery in extraQueries)
                {
                    var extraUrl = baseUrl + "class_list.html?" + extraQuery;

                    var extraSubjectLabel = query.Split('&')[0].Split('=')[1];
                    Console.WriteLine($"Scraping subject {extraSubjectLabel}: {extraUrl}");

                    var extraSections = MainSearchScraper.Scrape(LoadFromUrl(extraUrl));
                    StoreSections(extraSections);
                }
                continue;
            }
            var subjectLabel = query.Split('&')[0].Split('=')[1];
            Console.WriteLine($"Scraping subject {subjectLabel}: {classListUrl}");

            var sections = MainSearchScraper.Scrape(classListDoc);
            StoreSections(sections);

            
        }
    }
    private static HtmlDocument LoadFromUrl(string url)
    {
        Thread.Sleep(200);
        var html = http.GetStringAsync(url).Result;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        return doc;
    }
    private void StoreSections(HashSet<SectionRecord> sections)
    {
        var seenCourses = new HashSet<string>();
        foreach (var section in sections)
        {
            // Details pages require section for url but contains the same info for all sections
            var classKey = $"{section.Subject}:{section.CourseNumber}";
            if (seenCourses.Add(classKey)) // Only gather information once per course
            {
                var detailsUrl =
                baseUrl + "description.html?subj=" + section.Subject +
                "&catno=" + section.CourseNumber +
                "&section=" + section.SectionNumber;

                var details = DescriptionScraper.Scrape(LoadFromUrl(detailsUrl));
                DbStore.StoreSection(section, details);
            }

        }
        Console.WriteLine($" sections: {sections.Count}");
    }
}
