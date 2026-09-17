using Dapper;
using HtmlAgilityPack;
using Microsoft.Data.Sqlite;

namespace Ingest.Schedule.Database;

/// <summary>
/// Replaces a course's short title with the full one from its description
/// page, for courses stored before the crawler read headings. One request per
/// course, two seconds apart, resumable through data/titles_done.txt.
/// </summary>
public static class Titles
{
    private const string Base = "https://class-schedule.app.utah.edu/main/";
    private const string DoneFile = "data/titles_done.txt";
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>"Fall2026" as the registrar's "1268".</summary>
    public static string TermCode(string term)
    {
        var season = term[..^4];
        var year = term[^4..];
        var digit = season switch { "Spring" => 4, "Summer" => 6, "Fall" => 8, _ => throw new ArgumentException($"unknown season in '{term}'") };
        return $"1{year[2..]}{digit}";
    }

    /// <summary>The full title from the page heading, or empty.</summary>
    public static string FullTitle(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return DescriptionScraper.Scrape(doc).Title;
    }

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

    /// <summary>The `titles` command.</summary>
    public static int Run(string databasePath, int? limit)
    {
        if (!File.Exists(databasePath)) { Console.Error.WriteLine($"no database at {databasePath}"); return 1; }
        var done = File.Exists(DoneFile) ? File.ReadLines(DoneFile).Select(l => l.TrimEnd('\r', '\n')).ToHashSet() : [];

        using var database = new SqliteConnection($"Data Source={databasePath}");
        database.Open();
        // The page address needs a term and a section, so take the newest stored one.
        var courses = database.Query<(string Subject, string Number, string? Title, string Term, string Section, long RowId)>("""
            SELECT c.subject, c.course_number, c.title, s.term, s.section_number, MAX(s.rowid)
            FROM courses c
            JOIN sections s ON s.subject = c.subject AND s.course_number = c.course_number
            GROUP BY c.subject, c.course_number
            ORDER BY c.subject, c.course_number
            """).ToList();
        var todo = courses.Where(c => !done.Contains($"{c.Subject}|{c.Number}")).ToList();
        if (limit is { } n) todo = todo.Take(n).ToList();
        Console.WriteLine($"{courses.Count:N0} courses, {done.Count:N0} already visited, {todo.Count:N0} to do (about {todo.Count * Pause.TotalSeconds / 3600:0.0} hours)");

        var changed = 0; var kept = 0; var missing = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(DoneFile)!);
        using var log = new StreamWriter(DoneFile, append: true);
        for (var i = 0; i < todo.Count; i++)
        {
            var (subject, number, title, term, section, _) = todo[i];
            var url = Base + TermCode(term) + "/description.html?subj=" + Uri.EscapeDataString(subject) + "&catno=" + Uri.EscapeDataString(number) + "&section=" + Uri.EscapeDataString(section);
            var page = FetchAsync(url).GetAwaiter().GetResult();
            if (page is null) { Thread.Sleep(Pause); continue; }   // not marked done, so the next run retries

            var found = FullTitle(page);
            if (found.Length == 0) missing++;
            else if (found != title)
            {
                database.Execute("UPDATE courses SET title = @Title WHERE subject = @Subject AND course_number = @Number", new { Title = found, Subject = subject, Number = number });
                changed++;
            }
            else kept++;

            log.WriteLine($"{subject}|{number}");
            log.Flush();
            if ((i + 1) % 100 == 0 || i + 1 == todo.Count)
                Console.WriteLine($"  {i + 1:N0}/{todo.Count:N0}  changed {changed:N0}  same {kept:N0}  no heading {missing:N0}  latest: {subject} {number}: '{title}' -> '{found}'");
            Thread.Sleep(Pause);
        }
        return 0;
    }
}
