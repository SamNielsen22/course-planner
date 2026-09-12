using System.Text;
using HtmlAgilityPack;

public class Crawler
{
    public const string Root = "https://class-schedule.app.utah.edu/";

    // Which of the registrar's schedules this walk is on - main, uac or
    // online - recorded on every section and on the progress rows, since a
    // subject done on one schedule is not done on another.
    private string campus = "";
    private string baseUrl = "";
    static readonly HttpClient http = new HttpClient();

    // Attempts per page before it is given up on and skipped.
    const int MaxAttempts = 5;

    static readonly Dictionary<string, DetailsRecord> detailsByCourse = new();
    public void Run(string campus, string termCode)
    {
        this.campus = campus;
        baseUrl = Root + campus + "/" + termCode + "/";
        var indexUrl = baseUrl + "index.html";

        var alreadyCrawled = DbStore.CompletedSubjects(termCode, campus);
        if (alreadyCrawled.Count > 0)
            Console.WriteLine($"Resuming {campus} {termCode}: skipping {alreadyCrawled.Count} subjects already crawled");

        Console.WriteLine($"Fetching subjects from {indexUrl}");
        var indexDoc = LoadFromUrl(indexUrl);
        if (indexDoc is null)
        {
            // Without the index there are no subjects to walk. Give up on this
            // term rather than the whole crawl - the other eighteen are fine.
            Console.WriteLine($"SKIPPING {campus} {termCode}: subject index would not load");
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
            var page = Load(classListUrl);
            var classListDoc = page?.Doc;
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

                    var extra = Load(extraUrl);
                    if (extra is null)
                    {
                        Console.WriteLine($"  ! part of {subjectLabel} would not load");
                        whole = false;
                        continue;
                    }
                    StoreSections(MainSearchScraper.Scrape(extra.Doc));
                    if (!extra.Complete)
                    {
                        Console.WriteLine($"  ! part of {subjectLabel} arrived cut off - kept, subject left unmarked");
                        whole = false;
                    }
                }
                // Only marked done if every part loaded; a partial subject must
                // be walked again rather than remembered as complete.
                if (whole) DbStore.MarkSubjectDone(termCode, campus, subjectLabel);
                else skipped++;
                continue;
            }
            Console.WriteLine($"Scraping subject {subjectLabel}: {classListUrl}");

            var sections = MainSearchScraper.Scrape(classListDoc);
            StoreSections(sections);
            // A page that arrived cut off keeps what came - the cards before
            // the cut are whole - but the subject is not marked done: the rest
            // is still owed, and a later run asks for it again.
            if (page!.Complete)
                DbStore.MarkSubjectDone(termCode, campus, subjectLabel);
            else
            {
                Console.WriteLine($"  ! {subjectLabel} arrived cut off: {sections.Count} sections kept, subject left unmarked");
                skipped++;
            }
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
    public int RefreshSeats(string campus, string termCode)
    {
        this.campus = campus;
        baseUrl = Root + campus + "/" + termCode + "/";
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
            var classListDoc = Load(baseUrl + "class_list.html?" + query)?.Doc;
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
                    var extraDoc = Load(baseUrl + "class_list.html?" + extraQuery)?.Doc;
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

    /// <summary>A fetched page, and whether all of it arrived.</summary>
    private sealed record Fetched(HtmlDocument Doc, bool Complete);

    /// <summary>A whole page, or null: for the index and the description pages, where half a page is no use.</summary>
    private static HtmlDocument? LoadFromUrl(string url) => Load(url) is { Complete: true } page ? page.Doc : null;

    /// <summary>
    /// Fetch a page, retrying; null once the retries are spent with nothing
    /// usable. A page the server cuts off partway - it does that to a few,
    /// the same few every time, apparently failing while rendering one
    /// particular card - comes back after the last attempt with what did
    /// arrive, the unfinished card dropped, and Complete false so the caller
    /// can store the whole cards without marking the subject done.
    /// </summary>
    /// <remarks>
    /// Returns rather than throws once the retries are spent. It used to
    /// throw, and because nothing upstream caught it the process died: on
    /// 2026-09-04 the registrar's own Fall 2026 NURS page began returning a
    /// 500 - their application error page, "the help desk has been notified" -
    /// and that one broken page killed a nineteen-term crawl 133 subjects in.
    ///
    /// A page that stays broken would kill it again on every rerun, so the
    /// crawl could never finish however many times it was started. One
    /// unreadable page has to cost one page.
    /// </remarks>
    private static Fetched? Load(string url)
    {
        Fetched? partial = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            Thread.Sleep(3000);
            string reason;
            try
            {
                var (html, complete) = Fetch(url).Result;
                var doc = new HtmlDocument();
                if (complete)
                {
                    doc.LoadHtml(html);
                    return new Fetched(doc, true);
                }
                doc.LoadHtml(CutOffPage.DropUnfinishedCard(html));
                partial = new Fetched(doc, false);
                reason = "the response ended before the page did";
            }
            catch (AggregateException error)
            {
                reason = error.InnerException?.Message ?? error.Message;
            }

            if (attempt == MaxAttempts)
            {
                if (partial is not null)
                {
                    Console.WriteLine($"CUT OFF {url} - keeping the whole cards that arrived");
                    return partial;
                }
                Console.WriteLine($"GIVING UP on {url}");
                Console.WriteLine($"         {reason}");
                return null;
            }
            Console.WriteLine($"WARNING: {reason}");
            Console.WriteLine($"         retrying in 30s (attempt {attempt}/{MaxAttempts})");
            Thread.Sleep(30_000);
        }
        return partial;
    }

    /// <summary>
    /// The page's text and whether it ended properly, read by hand rather than
    /// with GetStringAsync. When the registrar's server cuts a response off it
    /// closes the connection without the empty chunk that ends a chunked
    /// response; curl hands over what came, .NET throws "The response ended
    /// prematurely". What came is kept either way, and the closing tag says
    /// whether it was the whole page.
    /// </summary>
    private static async Task<(string Html, bool Complete)> Fetch(string url)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new MemoryStream();
        var complete = true;
        try
        {
            await stream.CopyToAsync(buffer);
        }
        catch (HttpIOException error) when (error.HttpRequestError == HttpRequestError.ResponseEnded)
        {
            complete = EndsWithHtmlClose(buffer);
        }

        var charset = response.Content.Headers.ContentType?.CharSet;
        var encoding = Encoding.UTF8;
        if (charset is not null)
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); } catch (ArgumentException) { }
        return (encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), complete);
    }

    /// <summary>Whether what arrived ends in the document's closing tag, whitespace aside.</summary>
    private static bool EndsWithHtmlClose(MemoryStream buffer)
    {
        var length = (int)buffer.Length;
        var tail = Encoding.ASCII.GetString(buffer.GetBuffer(), Math.Max(0, length - 64), Math.Min(64, length));
        return tail.TrimEnd().EndsWith("</html>", StringComparison.OrdinalIgnoreCase);
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
                        DbStore.StoreSection(section, new DetailsRecord("", "", "", ""), campus);
                        continue;
                    }
                    details = DescriptionScraper.Scrape(detailsDoc);
                }
                detailsByCourse[classKey] = details;
            }

            DbStore.StoreSection(section, details, campus); // Every section gets stored, not just the first
        }
        Console.WriteLine($" sections: {sections.Count}");
    }
}
