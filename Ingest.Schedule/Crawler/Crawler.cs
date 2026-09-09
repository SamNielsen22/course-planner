using HtmlAgilityPack;

public class Crawler
{
    private string baseUrl = "";
    static readonly HttpClient http = new HttpClient();

    // Attempts per page before it is given up on and skipped.
    const int MaxAttempts = 5;

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
        var indexDoc = LoadFromUrl(indexUrl);
        if (indexDoc is null)
        {
            // Without the index there are no subjects to walk. Give up on this
            // term rather than the whole crawl - the other eighteen are fine.
            Console.WriteLine($"SKIPPING TERM {termCode}: subject index would not load");
            return;
        }
        var queries = SubjectScraper.Scrape(indexDoc);
        Console.WriteLine($"Found {queries.Count} subjects");

        var skipped = 0;
        var seenCourses = new HashSet<string>();
        foreach (var query in queries)
        {
            var subjectLabel = query.Split('&')[0].Split('=')[1];
            if (alreadyCrawled.Contains(subjectLabel))
                continue;

            var classListUrl = baseUrl + "class_list.html?" + query;
            var classListDoc = LoadFromUrl(classListUrl);
            if (classListDoc is null)
            {
                // Left unmarked in crawl_progress on purpose, so a later run
                // picks it up once the registrar's page recovers.
                Console.WriteLine($"  ! {subjectLabel} skipped - page would not load");
                skipped++;
                continue;
            }

            var alert = classListDoc.DocumentNode.SelectSingleNode(
                "//div[contains(@class,'alert') and contains(.,'divided by credit and noncredit')]"
            );
            if (alert != null) // Some subject pages lead to a credit/noncredit menu
            {
                var extraQueries = SubjectScraper.Scrape(classListDoc);
                var whole = true;
                foreach (var extraQuery in extraQueries)
                {
                    var extraUrl = baseUrl + "class_list.html?" + extraQuery;

                    Console.WriteLine($"Scraping subject {subjectLabel}: {extraUrl}");

                    var extraDoc = LoadFromUrl(extraUrl);
                    if (extraDoc is null)
                    {
                        Console.WriteLine($"  ! part of {subjectLabel} would not load");
                        whole = false;
                        continue;
                    }
                    StoreSections(MainSearchScraper.Scrape(extraDoc));
                }
                // Only marked done if every part loaded; a partial subject must
                // be walked again rather than remembered as complete.
                if (whole) DbStore.MarkSubjectDone(termCode, subjectLabel);
                else skipped++;
                continue;
            }
            Console.WriteLine($"Scraping subject {subjectLabel}: {classListUrl}");

            var sections = MainSearchScraper.Scrape(classListDoc);
            StoreSections(sections);
            DbStore.MarkSubjectDone(termCode, subjectLabel);
        }

        if (skipped > 0)
            Console.WriteLine($"Term {termCode}: {skipped} subject(s) skipped and "
                              + "left unmarked - rerun to pick them up");
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
        var indexDoc = LoadFromUrl(indexUrl);
        if (indexDoc is null)
        {
            Console.WriteLine("subject index would not load - no seats refreshed");
            return 0;
        }
        var queries = SubjectScraper.Scrape(indexDoc);
        Console.WriteLine($"Found {queries.Count} subjects");

        var updated = 0;
        foreach (var query in queries)
        {
            var subjectLabel = query.Split('&')[0].Split('=')[1];
            var classListDoc = LoadFromUrl(baseUrl + "class_list.html?" + query);
            if (classListDoc is null)
            {
                Console.WriteLine($"  {subjectLabel}: page would not load - skipped");
                continue;
            }

            var alert = classListDoc.DocumentNode.SelectSingleNode(
                "//div[contains(@class,'alert') and contains(.,'divided by credit and noncredit')]"
            );

            var documents = new List<HtmlDocument>();
            if (alert != null)   // a credit/noncredit menu, not a class list
                foreach (var extraQuery in SubjectScraper.Scrape(classListDoc))
                {
                    var extraDoc = LoadFromUrl(baseUrl + "class_list.html?" + extraQuery);
                    if (extraDoc is not null) documents.Add(extraDoc);
                }
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

    /// <summary>
    /// Fetch a page, or null if it will not load.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing once the retries are spent. It used to
    /// throw, and because nothing upstream caught it the process died: on
    /// 2026-09-04 the registrar's own Fall 2026 NURS page began returning a
    /// 500 - their application error page, "the help desk has been notified" -
    /// and that one broken page killed a nineteen-term crawl 133 subjects in.
    ///
    /// A page that stays broken would kill it again on every rerun, so the
    /// crawl could never finish however many times it was started. One
    /// unreadable page has to cost one page.
    /// </remarks>
    private static HtmlDocument? LoadFromUrl(string url)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            Thread.Sleep(3000);
            try
            {
                var html = http.GetStringAsync(url).Result;
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                return doc;
            }
            catch (AggregateException error)
            {
                var reason = error.InnerException?.Message ?? error.Message;
                if (attempt == MaxAttempts)
                {
                    Console.WriteLine($"GIVING UP on {url}");
                    Console.WriteLine($"         {reason}");
                    return null;
                }
                Console.WriteLine($"WARNING: {reason}");
                Console.WriteLine($"         retrying in 30s (attempt {attempt}/{MaxAttempts})");
                Thread.Sleep(30_000);
            }
        }
        return null;
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

                    // A description that will not load costs the description,
                    // not the section: the schedule row is still worth storing.
                    // Left empty rather than cached as a wrong value, and not
                    // memoised, so the next run fetches it again.
                    var detailsDoc = LoadFromUrl(detailsUrl);
                    if (detailsDoc is null)
                    {
                        DbStore.StoreSection(section, new DetailsRecord("", "", ""));
                        continue;
                    }
                    details = DescriptionScraper.Scrape(detailsDoc);
                }
                detailsByCourse[classKey] = details;
            }

            DbStore.StoreSection(section, details); // Every section gets stored, not just the first
        }
        Console.WriteLine($" sections: {sections.Count}");
    }
}
