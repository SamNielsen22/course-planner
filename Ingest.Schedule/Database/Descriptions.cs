using Dapper;
using HtmlAgilityPack;
using Microsoft.Data.Sqlite;

namespace Ingest.Schedule.Database;

/// <summary>
/// Re-reads course description pages, oldest first, a slice at a time.
///
/// A course holds one description, prerequisite list and requirement designation
/// for every term it has ever run in, and the registrar edits them between terms
/// - prerequisites most of all. The crawl only fetches a description page for a
/// course it has never seen, so without this the first reading would stand
/// forever and a changed prerequisite would never show.
///
/// Each run takes the courses whose pages were read longest ago and re-reads a
/// bounded number of them, so no single run is long. Called with a small limit
/// from every collect cycle, the whole catalogue comes round in about a day
/// while no cycle grows by more than a few minutes.
/// </summary>
public static class Descriptions
{
    // The registrar serves a description under the schedule the section belongs to.
    // Asking the main schedule for an Asia Campus or UOnline course answers 200 with
    // an empty shell - no heading, no cards - so the campus has to come from the row.
    private const string Root = "https://class-schedule.app.utah.edu/";
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(3);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<string?> FetchAsync(string url)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                using var response = await Http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                if (attempt == 2) { Console.WriteLine($"  giving up on {url}: {error.Message}"); return null; }
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }
        return null;
    }

    /// <summary>
    /// Records that this course was tried, without touching what is stored. A
    /// read that failed is stamped as well as one that worked: unstamped rows
    /// sort first, so leaving a failure unstamped would park it at the head of
    /// the queue and spend part of every run retrying it. Stamped, it comes
    /// round again on the next full pass.
    /// </summary>
    private static void Stamp(SqliteConnection database, string subject, string number) =>
        database.Execute(
            "UPDATE courses SET details_updated = @Now WHERE subject = @Subject AND course_number = @Number",
            new { Now = DateTime.UtcNow.ToString("o"), Subject = subject, Number = number });

    /// <summary>
    /// The `descriptions` command. A limit of 0 or less means every candidate.
    /// Only courses running in <paramref name="terms"/> are re-read: a course
    /// nobody can register for now does not need today's prerequisites, and
    /// overwriting a finished term's description with today's text would make
    /// the older record less true, not more. A course that comes back is picked
    /// up as soon as it appears in one of these terms.
    /// </summary>
    public static int Run(string databasePath, int? limit, IReadOnlyCollection<string> terms)
    {
        if (!File.Exists(databasePath)) { Console.Error.WriteLine($"no database at {databasePath}"); return 1; }

        using var database = new SqliteConnection($"Data Source={databasePath}");
        database.Open();

        // A description page's address needs a term and a section, so each course
        // is paired with a section from one of the terms under way. Oldest
        // reading first, never-read before that, so nothing is starved.
        var take = limit is { } n && n > 0 ? n : int.MaxValue;
        const string candidates = """
            FROM courses c
            JOIN sections s ON s.subject = c.subject AND s.course_number = c.course_number
                           AND s.rowid = (SELECT MAX(x.rowid) FROM sections x
                                          WHERE x.subject = c.subject AND x.course_number = c.course_number
                                            AND x.term IN @Terms)
            WHERE s.term IN @Terms
            """;
        var todo = database.Query<(string Subject, string Number, string Term, string Section, string Campus)>($"""
            SELECT c.subject AS Subject, c.course_number AS Number, s.term AS Term,
                   s.section_number AS Section, s.campus AS Campus
            {candidates}
            ORDER BY c.details_updated IS NOT NULL, c.details_updated, c.subject, c.course_number
            LIMIT @Take
            """, new { Take = take, Terms = terms }).ToList();

        var total = database.ExecuteScalar<int>($"SELECT COUNT(*) {candidates}", new { Terms = terms });
        var never = database.ExecuteScalar<int>($"SELECT COUNT(*) {candidates} AND c.details_updated IS NULL", new { Terms = terms });
        Console.WriteLine($"Re-reading {todo.Count:N0} of {total:N0} course pages in {string.Join(", ", terms)} ({never:N0} never read)");

        var changed = 0; var same = 0; var failed = 0;
        foreach (var (subject, number, term, section, campus) in todo)
        {
            var url = Root + campus + "/" + Titles.TermCode(term) + "/description.html?subj=" + Uri.EscapeDataString(subject)
                      + "&catno=" + Uri.EscapeDataString(number) + "&section=" + Uri.EscapeDataString(section);
            var page = FetchAsync(url).GetAwaiter().GetResult();
            if (page is null) { failed++; Stamp(database, subject, number); Thread.Sleep(Pause); continue; }

            var doc = new HtmlDocument();
            doc.LoadHtml(page);
            var read = DescriptionScraper.Scrape(doc);

            // A page that yields no description at all is treated as a failure
            // rather than allowed to blank out what is already stored.
            if (read.Description.Length == 0 && read.Title.Length == 0) { failed++; Stamp(database, subject, number); Thread.Sleep(Pause); continue; }

            var before = database.QueryFirstOrDefault<(string? Description, string? Prerequisites, string? Designation)>(
                "SELECT description AS Description, prerequisites AS Prerequisites, requirement_designation AS Designation " +
                "FROM courses WHERE subject = @Subject AND course_number = @Number",
                new { Subject = subject, Number = number });

            var moved = (before.Description ?? "") != read.Description
                        || (before.Prerequisites ?? "") != read.Prerequisites
                        || (before.Designation ?? "") != read.RequirementDesignation;

            database.Execute("""
                UPDATE courses
                   SET title = CASE WHEN @Title <> '' THEN @Title ELSE title END,
                       description = @Description,
                       prerequisites = @Prerequisites,
                       requirement_designation = @Designation,
                       details_updated = @Now
                 WHERE subject = @Subject AND course_number = @Number
                """, new
            {
                read.Title, read.Description, read.Prerequisites,
                Designation = read.RequirementDesignation,
                Now = DateTime.UtcNow.ToString("o"), Subject = subject, Number = number
            });

            if (moved) { changed++; Console.WriteLine($"  changed {subject} {number}"); } else same++;
            Thread.Sleep(Pause);
        }

        Console.WriteLine($"Done. {changed:N0} changed, {same:N0} unchanged, {failed:N0} could not be read.");
        return 0;
    }
}
