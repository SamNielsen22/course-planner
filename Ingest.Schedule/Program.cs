class Program
{
    // The registrar's three class schedules: main (Salt Lake and the satellite
    // sites), uac (the Asia Campus) and online (UOnline). A section is in exactly one.
    static readonly string[] Campuses = { "main", "uac", "online" };
    const int TermsToScrape = 20;   // the term ahead, then back to Fall 2020, matching the grade data

    // The digit the registrar gives each term. Listed newest first within a year.
    const int Fall = TermCodes.Fall;
    const int Summer = TermCodes.Summer;
    const int Spring = TermCodes.Spring;

    static int Main(string[] args)
    {
        DbStore.EnsureColumns();

        // Besides the crawl, the database chores that used to be Python scripts:
        //   seats                       refresh enrollment figures for the terms under way
        //   grades [--db p] csv...      load the GPA csv into the grade tables
        //   departments [--db p]        give each instructor a department from what they teach
        //   titles [--db p] [--limit n] fill in full course titles from the description pages
        //   pairs                       re-read which lab/discussion registers you into which lecture, terms under way
        //   companions [--db p] [--file f]  apply the hand-entered pairings in data/companion-pairs.tsv
        //   recrawl                     re-crawl the terms under way, picking up classes added since
        //   descriptions [--db p] [--limit n]  re-read course pages oldest first, so an edited
        //                               description or prerequisite stops being stale
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        var rest = args.Skip(1).ToList();
        var databasePath = "data/courseplanner.db";
        var at = rest.IndexOf("--db");
        if (at >= 0 && at + 1 < rest.Count) { databasePath = rest[at + 1]; rest.RemoveRange(at, 2); }

        switch (command)
        {
            case "seats":
                RefreshSeats();
                return 0;
            case "pairs":
                RefreshPairings();
                return 0;
            case "grades":
                return Ingest.Schedule.Database.GradeLoader.Run(databasePath, rest);
            case "departments":
                return Ingest.Schedule.Database.Departments.Run(databasePath);
            case "titles":
                var limitAt = rest.IndexOf("--limit");
                int? limit = limitAt >= 0 && limitAt + 1 < rest.Count && int.TryParse(rest[limitAt + 1], out var n) ? n : null;
                return Ingest.Schedule.Database.Titles.Run(databasePath, limit);
            case "companions":
                var fileAt = rest.IndexOf("--file");
                var file = fileAt >= 0 && fileAt + 1 < rest.Count ? rest[fileAt + 1] : Ingest.Schedule.Database.CompanionOverrides.DefaultFile;
                return Ingest.Schedule.Database.CompanionOverrides.Run(databasePath, file);
            case "recrawl":
                Recrawl();
                return 0;
            case "descriptions":
                var descAt = rest.IndexOf("--limit");
                int? descLimit = descAt >= 0 && descAt + 1 < rest.Count && int.TryParse(rest[descAt + 1], out var d) ? d : null;
                return Ingest.Schedule.Database.Descriptions.Run(
                    databasePath, descLimit,
                    TermCodesNewestFirst().Take(2).Select(TermCodes.Display).ToList());
            case "":
                break;
            default:
                Console.Error.WriteLine($"unknown command '{args[0]}': expected seats, recrawl, descriptions, grades, departments, titles, pairs or companions, or nothing for the full crawl");
                return 1;
        }

        var termCodes = TermCodesNewestFirst().Take(TermsToScrape).ToList();

        Console.WriteLine($"Crawling {termCodes.Count} terms on {Campuses.Length} schedules, newest first");

        foreach (var termCode in termCodes)
            foreach (var campus in Campuses)
            {
                Console.WriteLine($"Crawling {campus} {termCode}");
                new Crawler().Run(campus, termCode);
            }
        return 0;
    }

    /// <summary>
    /// Re-crawl the term under way and the one ahead, so classes the registrar
    /// has added since the last crawl are picked up with their component, units,
    /// times, room and instructors. The seats pass cannot do this: it only
    /// updates and cancels rows it already has.
    ///
    /// Only these two terms are forgotten and re-walked. Finished terms keep
    /// their crawl marks, since a term that has ended cannot change. Course
    /// descriptions already stored are not fetched again, so the cost is roughly
    /// one page per subject.
    /// </summary>
    static void Recrawl()
    {
        var termCodes = TermCodesNewestFirst().Take(2).ToList();
        foreach (var termCode in termCodes)
        {
            var forgotten = DbStore.ClearProgress(termCode);
            Console.WriteLine($"Re-crawling term {termCode} ({forgotten} subject marks cleared)");
            foreach (var campus in Campuses)
                new Crawler().Run(campus, termCode);
        }
        Console.WriteLine("Done. Re-crawl complete.");
    }

    /// <summary>
    /// Refresh the counts for the term under way and the one ahead, without a
    /// full crawl. An unpublished term answers 404 and costs one request.
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
    /// Re-read the companion pairings for the term under way and the one ahead. Same
    /// pages as a crawl, so it is a crawl's worth of requests for two terms;
    /// run it when a term's notes have been added since it was crawled.
    /// </summary>
    static void RefreshPairings()
    {
        var updated = 0;
        foreach (var termCode in TermCodesNewestFirst().Take(2))
        {
            Console.WriteLine($"Refreshing companion pairings for term {termCode}");
            foreach (var campus in Campuses)
                updated += new Crawler().RefreshPairings(campus, termCode);
        }
        Console.WriteLine($"Done. Pairings changed on {updated} sections.");
    }

    /// <summary>
    /// Term codes, generated rather than scraped so upcoming terms are included.
    /// Starts one term ahead of the one under way and walks backwards forever.
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
    static string TermCode(int year, int term) => TermCodes.Code(year, term);
}
