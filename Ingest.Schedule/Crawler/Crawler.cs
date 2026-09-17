using System.Text;
using HtmlAgilityPack;

public class Crawler
{
    public const string Root = "https://class-schedule.app.utah.edu/";

    // Which schedule this walk is on: main, uac or online. Recorded on every
    // section and every progress row, since a subject done on one is not done on another.
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
            // A cut-off page keeps its whole cards but is not marked done, so a later run asks again.
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
    /// Refresh one term's enrollment figures: seats, cap, enrolled, waiting,
    /// class number. One small request per subject, from the registrar's
    /// sections table. Cheap enough to run several times a day.
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
            Console.WriteLine($"{campus} {termCode}: no schedule published, or the index would not load - nothing refreshed");
            return 0;
        }
        var queries = SubjectScraper.Scrape(indexDoc);
        Console.WriteLine($"Found {queries.Count} subjects");

        var updated = 0;
        foreach (var query in queries)
        {
            // The label as the index links it, percent-encoded ("CH%20EN"),
            // which is how the table's address wants it too.
            var subjectLabel = query.Split('&')[0].Split('=')[1];
            var page = Load(baseUrl + "sections.html?subj=" + subjectLabel + "&catno=");
            if (page is null)
            {
                Console.WriteLine($"  {subjectLabel}: table would not load - skipped");
                continue;
            }

            var term = SectionsTableScraper.Term(page.Doc);
            if (term is null)
            {
                Console.WriteLine($"  {subjectLabel}: not a sections table (no term in the heading) - skipped");
                continue;
            }

            var counts = SectionsTableScraper.Scrape(page.Doc);
            var result = DbStore.UpdateCounts(term, campus, Uri.UnescapeDataString(subjectLabel), counts, page.Complete);
            updated += result.Updated;

            var line = $"  {subjectLabel}: {result.Updated} sections updated";
            // The table runs ahead of the class list, so a section can be here
            // before it is published; a cut-off list page does the same.
            if (result.Unknown > 0) line += $", {result.Unknown} on the registrar's table but not on the class list (not yet published, or the list page was cut off)";
            if (result.Removed > 0) line += $", {result.Removed} no longer listed - removed";
            if (!page.Complete) line += " (page cut off: what came was kept, nothing removed)";
            Console.WriteLine(line);
        }

        return updated;
    }

    /// <summary>
    /// Re-read one term's class lists for the companion pairings alone. Only
    /// pairs_with is written, and only the subjects the database says contain a
    /// course with both a lecture and a companion section are fetched: on a
    /// typical term that is about 34 subjects of 190, so the other 156 pages are
    /// never asked for. For a term whose notes were added after it was crawled.
    /// </summary>
    public int RefreshPairings(string campus, string termCode)
    {
        this.campus = campus;
        baseUrl = Root + campus + "/" + termCode + "/";

        // Which subjects could possibly have a pairing to read. Nothing to do if none.
        var wanted = DbStore.SubjectsWithCompanions(termCode, campus);
        if (wanted.Count == 0)
        {
            // Either the term has no paired course, or it was never crawled and
            // the database has nothing to go on. Say which, since the fix differs.
            var known = DbStore.SectionCount(termCode, campus);
            Console.WriteLine(known == 0
                ? $"{campus} {termCode}: not crawled yet, so there is nothing to look up - run the crawl first"
                : $"{campus} {termCode}: no course has both a lecture and a companion section - nothing to fetch");
            return 0;
        }

        var indexUrl = baseUrl + "index.html";
        Console.WriteLine($"Fetching subjects from {indexUrl}");
        var indexDoc = LoadFromUrl(indexUrl);
        if (indexDoc is null)
        {
            Console.WriteLine($"{campus} {termCode}: no schedule published, or the index would not load - nothing refreshed");
            return 0;
        }
        var queries = SubjectScraper.Scrape(indexDoc)
            .Where(q => wanted.Contains(SubjectScraper.Subject(q)))
            .ToList();
        Console.WriteLine($"{queries.Count} subject(s) to read, of {wanted.Count} the database expects");
        foreach (var missing in wanted.Except(queries.Select(SubjectScraper.Subject)))
            Console.WriteLine($"  ! {missing}: in the database but not on the index - not refreshed");

        var updated = 0;
        foreach (var query in queries)
        {
            var subject = SubjectScraper.Subject(query);
            var page = Load(baseUrl + "class_list.html?" + query);
            if (page is null)
            {
                Console.WriteLine($"  {subject}: page would not load - skipped");
                continue;
            }

            // Some subjects answer with a credit/noncredit menu instead of cards,
            // exactly as on the crawl: read each part the menu links to.
            var parts = IsCreditMenu(page.Doc)
                ? SubjectScraper.Scrape(page.Doc).Select(q => Load(baseUrl + "class_list.html?" + q)).ToList()
                : new List<Fetched?> { page };

            var named = 0; var changed = 0; var cutOff = false;
            foreach (var part in parts)
            {
                if (part is null) { Console.WriteLine($"  ! part of {subject} would not load - that part skipped"); continue; }
                var sections = MainSearchScraper.Scrape(part.Doc);
                named += sections.Count(s => s.PairsWith is not null);
                changed += DbStore.UpdatePairings(campus, sections);
                cutOff |= !part.Complete;
            }
            updated += changed;
            if (named > 0 || changed > 0 || cutOff)
                Console.WriteLine($"  {subject}: {named} companion sections paired on the page, {changed} changed{(cutOff ? " (page cut off)" : "")}");
        }
        return updated;
    }

    /// <summary>The page that lists a subject's credit and noncredit halves rather than its classes.</summary>
    private static bool IsCreditMenu(HtmlDocument doc) =>
        doc.DocumentNode.SelectSingleNode("//div[contains(@class,'alert') and contains(.,'divided by credit and noncredit')]") is not null;

    /// <summary>A fetched page, and whether all of it arrived.</summary>
    private sealed record Fetched(HtmlDocument Doc, bool Complete);

    /// <summary>A whole page, or null: for the index and the description pages, where half a page is no use.</summary>
    private static HtmlDocument? LoadFromUrl(string url) => Load(url) is { Complete: true } page ? page.Doc : null;

    /// <summary>
    /// Fetch a page, retrying, and return null once the retries are spent -
    /// one unreadable page must cost one page, not the whole crawl. A page the
    /// server cuts off partway comes back with its whole cards and Complete false.
    /// </summary>
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
                // A 404 will still be a 404 in thirty seconds; an unpublished term answers one.
                if (error.InnerException is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
                {
                    Console.WriteLine($"NOT FOUND {url}");
                    return null;
                }
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
    /// The page's text and whether it ended properly. Read from the stream by
    /// hand so a response the server cuts off keeps what arrived; the closing
    /// tag says whether it was the whole page.
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

                    // A description that will not load costs the description, not
                    // the section. Left empty and not memoised, so the next run tries again.
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
