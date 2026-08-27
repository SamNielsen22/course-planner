using HtmlAgilityPack;

public class Crawler
{
    private string baseUrl = "";
    static readonly HttpClient http = new HttpClient();

    static readonly Dictionary<string, DetailsRecord> detailsByCourse = new();
    public void Run(string url)
    {
        baseUrl = url;
        var termCode = baseUrl.TrimEnd('/').Split('/').Last();
        var indexUrl = baseUrl + "index.html";

        var alreadyCrawled = DbStore.CompletedSubjects(termCode);
        if (alreadyCrawled.Count > 0)
            Console.WriteLine($"Resuming term {termCode}: skipping {alreadyCrawled.Count} subjects already crawled");

        Console.WriteLine($"Fetching subjects from {indexUrl}");
        var queries = SubjectScraper.Scrape(LoadFromUrl(indexUrl));
        Console.WriteLine($"Found {queries.Count} subjects");

        var seenCourses = new HashSet<string>();
        foreach (var query in queries)
        {
            var subjectLabel = query.Split('&')[0].Split('=')[1];
            if (alreadyCrawled.Contains(subjectLabel))
                continue;

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

                    Console.WriteLine($"Scraping subject {subjectLabel}: {extraUrl}");

                    var extraSections = MainSearchScraper.Scrape(LoadFromUrl(extraUrl));
                    StoreSections(extraSections);
                }
                DbStore.MarkSubjectDone(termCode, subjectLabel);
                continue;
            }
            Console.WriteLine($"Scraping subject {subjectLabel}: {classListUrl}");

            var sections = MainSearchScraper.Scrape(classListDoc);
            StoreSections(sections);
            DbStore.MarkSubjectDone(termCode, subjectLabel);

            
        }
    }
    /// <summary>
    /// Refresh seat counts for one term and nothing else. Costs one request per
    /// subject page plus the index - no description pages, since seats are the
    /// only thing being read. Cheap enough to run several times a day.
    /// </summary>
    public int RefreshSeats(string url)
    {
        baseUrl = url;
        var indexUrl = baseUrl + "index.html";

        Console.WriteLine($"Fetching subjects from {indexUrl}");
        var queries = SubjectScraper.Scrape(LoadFromUrl(indexUrl));
        Console.WriteLine($"Found {queries.Count} subjects");

        var updated = 0;
        foreach (var query in queries)
        {
            var subjectLabel = query.Split('&')[0].Split('=')[1];
            var classListDoc = LoadFromUrl(baseUrl + "class_list.html?" + query);

            var alert = classListDoc.DocumentNode.SelectSingleNode(
                "//div[contains(@class,'alert') and contains(.,'divided by credit and noncredit')]"
            );

            var documents = new List<HtmlDocument>();
            if (alert != null)   // a credit/noncredit menu, not a class list
                foreach (var extraQuery in SubjectScraper.Scrape(classListDoc))
                    documents.Add(LoadFromUrl(baseUrl + "class_list.html?" + extraQuery));
            else
                documents.Add(classListDoc);

            var subjectTotal = 0;
            foreach (var document in documents)
                subjectTotal += DbStore.UpdateSeats(MainSearchScraper.Scrape(document));

            updated += subjectTotal;
            Console.WriteLine($"  {subjectLabel}: {subjectTotal} sections updated");
        }

        return updated;
    }

    private static HtmlDocument LoadFromUrl(string url)
    {
        for (var attempt = 1; ; attempt++)
        {
            Thread.Sleep(3000);
            try
            {
                var html = http.GetStringAsync(url).Result;
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                return doc;
            }
            catch (AggregateException error) when (attempt < 5)
            {
                Console.WriteLine($"WARNING: {error.InnerException?.Message ?? error.Message}");
                Console.WriteLine($"         retrying in 30s (attempt {attempt}/5)");
                Thread.Sleep(30_000);
            }
        }
    }
    private void StoreSections(HashSet<SectionRecord> sections)
    {
        foreach (var section in sections)
        {
            // Details pages require section for url but contains the same info for all sections
            var classKey = $"{section.Subject}:{section.CourseNumber}";
            if (!detailsByCourse.TryGetValue(classKey, out var details)) // Only fetch once per course
            {
                details = DbStore.StoredDetails(section.Subject, section.CourseNumber);
                if (details is null)   // not fetched on any previous run either
                {
                    var detailsUrl =
                    baseUrl + "description.html?subj=" + section.Subject +
                    "&catno=" + section.CourseNumber +
                    "&section=" + section.SectionNumber;

                    details = DescriptionScraper.Scrape(LoadFromUrl(detailsUrl));
                }
                detailsByCourse[classKey] = details;
            }

            DbStore.StoreSection(section, details); // Every section gets stored, not just the first
        }
        Console.WriteLine($" sections: {sections.Count}");
    }
}
