class Program
{
    // The registrar publishes three class schedules on the one site, and a
    // section is listed in exactly one of them: main (Salt Lake, whose subject
    // pages also carry the Sandy, St. George and Herriman classes), uac (the
    // Asia Campus in Incheon, sections 3xx) and online (UOnline Programs,
    // sections 29x). The professional schools keep calendars of their own but
    // no schedule of their own, so these three are the whole University.
    static readonly string[] Campuses = { "main", "uac", "online" };
    const int TermsToScrape = 20;   // the term ahead, then back to Fall 2020, matching the grade data

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

        var termCodes = TermCodesNewestFirst().Take(TermsToScrape).ToList();

        Console.WriteLine($"Crawling {termCodes.Count} terms on {Campuses.Length} schedules, newest first");

        foreach (var termCode in termCodes)
            foreach (var campus in Campuses)
            {
                Console.WriteLine($"Crawling {campus} {termCode}");
                new Crawler().Run(campus, termCode);
            }
    }

    /// <summary>
    /// Seat and wait-list counts go stale within hours during registration, so
    /// they get their own pass - one request per subject, no description pages
    /// - rather than waiting for a full crawl. Over two terms: the one under
    /// way, and the one ahead, which is the one being registered for once the
    /// registrar publishes it (until then its pages answer 404 and it costs one
    /// request per campus).
    /// </summary>
    static void RefreshSeats()
    {
        var updated = 0;
        foreach (var termCode in TermCodesNewestFirst().Take(2))
        {
            Console.WriteLine($"Refreshing counts for term {termCode}");
            foreach (var campus in Campuses)
                updated += new Crawler().RefreshSeats(campus, termCode);
        }
        Console.WriteLine($"Done. Counts updated on {updated} sections.");
    }

    /// <summary>
    /// Term codes are generated rather than scraped: the archive page lists only terms
    /// that have already finished, so it misses the upcoming ones a planner cares about.
    /// Starts one term ahead of the one under way - the registrar publishes the next
    /// schedule a couple of months before registration opens, and that is the term
    /// a planner is planning; until it is published its index answers 404 and the
    /// crawl skips it - and walks backwards forever. The caller decides how many to take.
    /// </summary>
    static IEnumerable<string> TermCodesNewestFirst()
    {
        var today = DateTime.Now;
        var (year, term) = NextTerm(today.Year, TermOfMonth(today.Month));

        while (true)
        {
            yield return TermCode(year, term);
            (year, term) = term switch
            {
                Fall => (year, Summer),
                Summer => (year, Spring),
                _ => (year - 1, Fall)
            };
        }
    }

    static (int Year, int Term) NextTerm(int year, int term) => term switch
    {
        Spring => (year, Summer),
        Summer => (year, Fall),
        _ => (year + 1, Spring)
    };

    /// <summary>Which term a date falls in, by the month the semester starts.</summary>
    static int TermOfMonth(int month) =>
        month >= 8 ? Fall :
        month >= 5 ? Summer :
                     Spring;

    /// <summary>1 for the 2000s, the two digit year, then the term. Fall 2026 is 1268.</summary>
    static string TermCode(int year, int term) => $"1{year % 100:00}{term}";
}
