class Program
{
    const string BaseUrl = "https://class-schedule.app.utah.edu/main/";
    const int TermsToScrape = 19;   // back to Fall 2020, matching the grade data

    // The digit the registrar gives each term. Listed newest first within a year.
    const int Fall = 8;
    const int Summer = 6;
    const int Spring = 4;

    static void Main(string[] args)
    {
        DbStore.EnsureColumns();

        if (args.Length > 0 && args[0].Equals("seats", StringComparison.OrdinalIgnoreCase))
        {
            RefreshSeats();
            return;
        }

        var termUrls = TermCodesNewestFirst()
            .Take(TermsToScrape)
            .Select(termCode => BaseUrl + termCode + "/")
            .ToList();

        Console.WriteLine($"Crawling {termUrls.Count} terms, newest first");

        foreach (var termUrl in termUrls)
        {
            Console.WriteLine($"Crawling term {termUrl}");
            new Crawler().Run(termUrl);
        }
    }

    /// <summary>
    /// Seat counts go stale within hours during registration, so they get their own
    /// pass over the term now under way - one request per subject, no description
    /// pages - rather than waiting for a full crawl.
    /// </summary>
    static void RefreshSeats()
    {
        var termCode = TermCodesNewestFirst().First();
        Console.WriteLine($"Refreshing seats for term {termCode}");

        var updated = new Crawler().RefreshSeats(BaseUrl + termCode + "/");
        Console.WriteLine($"Done. Seat counts updated on {updated} sections.");
    }

    /// <summary>
    /// Term codes are generated rather than scraped: the archive page lists only terms
    /// that have already finished, so it misses the upcoming ones a planner cares about.
    /// Starts at the term currently under way and walks backwards forever - the caller
    /// decides how many to take.
    /// </summary>
    static IEnumerable<string> TermCodesNewestFirst()
    {
        var today = DateTime.Now;
        var currentTerm = TermOfMonth(today.Month);

        for (var year = today.Year; ; year--)
        {
            foreach (var term in new[] { Fall, Summer, Spring })
            {
                var isFutureTerm = year == today.Year && term > currentTerm;
                if (!isFutureTerm)
                    yield return TermCode(year, term);
            }
        }
    }

    /// <summary>Which term a date falls in, by the month the semester starts.</summary>
    static int TermOfMonth(int month) =>
        month >= 8 ? Fall :
        month >= 5 ? Summer :
                     Spring;

    /// <summary>1 for the 2000s, the two digit year, then the term. Fall 2026 is 1268.</summary>
    static string TermCode(int year, int term) => $"1{year % 100:00}{term}";
}
