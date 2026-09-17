using System.Collections.Concurrent;
using System.Globalization;
using Ingest.Gpa.Vizql;

namespace Ingest.Gpa;

/// <summary>The sweep: every term, subject, course and section, on a pool of sessions.</summary>
public static class Sweep
{
    // Workers append to the same two files. One lock keeps a row from ever
    // being torn and the set of rows unchanged.
    private static readonly object WriteLock = new();

    public static HashSet<string> LoadProgress()
    {
        if (!File.Exists(Config.ProgressFile)) return [];
        return File.ReadLines(Config.ProgressFile).Select(l => l.Trim()).ToHashSet();
    }

    public static void MarkFinished(string key)
    {
        lock (WriteLock) File.AppendAllText(Config.ProgressFile, key + Environment.NewLine);
    }

    /// <summary>Marks a whole subject done so reruns skip re-walking its courses.</summary>
    public static string SubjectKey(string term, string subject) => $"SUBJECT|{term}|{subject}";

    /// <summary>One csv row, as Python's csv module writes it: quoted only when needed, CRLF line ends, blank for a missing column.</summary>
    public static void WriteCsvRow(IReadOnlyDictionary<string, string> row)
    {
        lock (WriteLock)
        {
            var isNew = !File.Exists(Config.OutputFile);
            using var writer = new StreamWriter(Config.OutputFile, append: true, new System.Text.UTF8Encoding(false));
            writer.NewLine = "\r\n";
            if (isNew) writer.WriteLine(string.Join(",", Config.CsvColumns));
            writer.WriteLine(string.Join(",", Config.CsvColumns.Select(c => CsvField(row.GetValueOrDefault(c, "")))));
        }
    }

    public static string CsvField(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    public static (string Term, string Subject) Shorten(string term, string subject) =>
        (term.Replace(" End of Term", "").Trim(), subject.Split(" - ")[0].Trim());

    /// <summary>A real grade point average and nothing else: anything outside 0-4 is a stray cell, whatever the worksheet claims.</summary>
    public static bool IsGpa(System.Text.Json.JsonElement? value) => Decoder.Number(value) is { } n && n is >= 0.0 and <= 4.0;

    private static double Float(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>The GPA table's figures, empty when nothing was published. Only rows naming this course are read.</summary>
    public static Dictionary<string, string> GpaStats(Reply reply, string catnbr, int students = 0)
    {
        var stats = new Dictionary<string, string>();
        var table = reply.Worksheet(Config.GpaSheet);
        for (var row = 0; row < table.Rows; row++)
        {
            if (table.Text(row, "Catnbr-alias") != catnbr) continue;
            var column = Config.StatColumns.GetValueOrDefault(table.Text(row, "Measure Names-alias"));
            var value = table.Cell(row, "Measure Values-alias");
            var text = value is null ? null : Decoder.Text(value);
            if (column is not null && text is not null && text != "" && text != "%null%" && IsGpa(value))
                stats[column] = text;
        }

        if (stats.TryGetValue("std_dev", out var deviation))
        {
            var ceiling = students > 1 ? Config.GradeSpan / 2 * Math.Sqrt(students / (students - 1.0)) : Config.MostForgivingStdDev;
            if (Float(deviation) - Config.DisplayRounding > ceiling)
            {
                Log.Note("impossible", ("catnbr", catnbr), ("reason", "std_dev"), ("value", deviation), ("students", students), ("ceiling", Math.Round(ceiling, 2)));
                throw new ImpossibleValueException($"{catnbr}: standard deviation {deviation} is above {ceiling:0.00}, the most that {(students > 0 ? students.ToString() : "any number of")} students scoring between 0 and 4 could produce");
            }
        }
        if (stats.TryGetValue("p25", out var low) && stats.TryGetValue("p50", out var mid) && stats.TryGetValue("p75", out var high)
            && !(Float(low) <= Float(mid) && Float(mid) <= Float(high)))
        {
            Log.Note("impossible", ("catnbr", catnbr), ("reason", "percentiles"), ("value", $"{low}/{mid}/{high}"));
            throw new ImpossibleValueException($"{catnbr}: percentiles {low}/{mid}/{high} do not increase");
        }

        // Every statistic zero means no letter grades were awarded - a credit/no credit section.
        if (stats.Count > 0 && stats.Values.All(v => Float(v) == 0.0)) return new Dictionary<string, string>();
        return stats;
    }

    /// <summary>Headcounts per grade group. Keeping only this course's rows drops the totals block and any sibling course.</summary>
    public static Dictionary<string, int> GradeCounts(Reply reply, string catnbr)
    {
        var counts = new Dictionary<string, int>();
        var table = reply.Worksheet(Config.HeadcountSheet);
        for (var row = 0; row < table.Rows; row++)
        {
            if (table.Text(row, "Catnbr-alias") != catnbr) continue;
            if (Decoder.Number(table.Cell(row, "CNT(Emplid Proxy)-alias")) is not { } headcount) continue;
            var cell = table.Cell(row, "Grade Group-alias");
            var grade = (cell is null ? "" : Decoder.Text(cell)).Trim().ToUpperInvariant();
            var column = Config.GradeColumns.GetValueOrDefault(grade, "grade_other");
            counts[column] = counts.GetValueOrDefault(column) + (int)Math.Truncate(headcount);
        }
        return counts;
    }

    /// <summary>Writes one row and marks it done. Returns the headcounts, or null when the key was already done.</summary>
    public static Dictionary<string, int>? Record(Progress finished, string term, string subject, string catnbr, string section, Reply reply, string tag = "")
    {
        var key = $"{term}|{subject}|{catnbr}|{section}";
        if (finished.Contains(key)) return null;

        var hasGpa = reply.Refreshed.Contains(Config.GpaSheet);
        var hasCounts = reply.Refreshed.Contains(Config.HeadcountSheet);
        if (!hasGpa && !hasCounts)
            throw new NotRefreshedException($"neither {Config.GpaSheet} nor {Config.HeadcountSheet} in this response (refreshed: [{string.Join(", ", reply.Refreshed.OrderBy(r => r, StringComparer.Ordinal).Select(r => $"'{r}'"))}])");
        var counts = hasCounts ? GradeCounts(reply, catnbr) : new Dictionary<string, int>();
        var graded = Config.GpaBearing.Sum(c => counts.GetValueOrDefault(c));
        var stats = hasGpa ? GpaStats(reply, catnbr, graded) : new Dictionary<string, string>();
        var (shortTerm, shortSubject) = Shorten(term, subject);
        if (stats.Count > 0 || counts.Count > 0)   // nothing published stays unpublished, not invented
        {
            var row = new Dictionary<string, string> { ["term"] = shortTerm, ["subject"] = shortSubject, ["catnbr"] = catnbr, ["section"] = section };
            foreach (var (k, v) in stats) row[k] = v;
            foreach (var (k, v) in counts) row[k] = v.ToString(CultureInfo.InvariantCulture);
            WriteCsvRow(row);
        }
        MarkFinished(key);
        finished.Add(key);
        Log.Note("row", ("key", key), ("wrote", stats.Count > 0 || counts.Count > 0), ("stats", stats), ("counts", counts),
                 ("refreshed", reply.Refreshed.OrderBy(r => r, StringComparer.Ordinal).ToList()), ("dictionary", reply.DictionarySize()), ("segments", reply.Segments.Count));
        Console.WriteLine($"{tag}  {shortSubject} {catnbr}-{section}: avg={stats.GetValueOrDefault("avg_gpa", "-")}");
        return new Dictionary<string, int>(counts);
    }

    /// <summary>Scrape one subject. True if every course came through cleanly.</summary>
    public static bool ScrapeSubject(DashboardSession session, Progress finished, string term, string subject, string tag = "")
    {
        var subjectReply = session.Select("subject", subject);
        var courses = subjectReply.Options("course");
        var (shortTerm, shortSubject) = Shorten(term, subject);
        Console.WriteLine($"{tag}{shortTerm} / {shortSubject}: {courses.Count} courses");
        if (courses.Count == 0) return true;   // a fresh response offering no courses is an answer, not a failure

        var complete = true;
        foreach (var catnbr in courses)
        {
            try
            {
                // A subject offering one course is already showing it; pinning it moves nothing.
                var reply = courses.Count == 1 ? subjectReply : session.Select("course", catnbr);
                var sections = reply.Options("section");
                if (sections.Count == 0) throw new RefusedException($"no sections listed for {catnbr}");

                var whole = Record(finished, term, subject, catnbr, Config.CourseSection, reply, tag);
                var parts = new Dictionary<string, int>();

                // The all-terms pass collects courses only: a section number is not stable across terms.
                if (term == Config.AllTerms) continue;

                if (sections.Count == 1)
                {
                    Record(finished, term, subject, catnbr, sections[0], reply, tag);   // the course view already IS the section view
                    continue;
                }

                foreach (var section in sections)
                {
                    var key = $"{term}|{subject}|{catnbr}|{section}";
                    if (finished.Contains(key)) continue;
                    Dictionary<string, int>? got;
                    try
                    {
                        reply = session.Select("section", section);
                        Log.Note("section", ("key", key), ("path", "direct"), ("refreshed", reply.Refreshed.OrderBy(r => r, StringComparer.Ordinal).ToList()), ("tiled", reply.Tiled));
                        if (!(reply.Refreshed.Contains(Config.GpaSheet) && reply.Refreshed.Contains(Config.HeadcountSheet)))
                        {
                            reply = session.RereadSection(catnbr, section);   // ask again in the order that forces a redraw
                            Log.Note("section", ("key", key), ("path", "reread"), ("refreshed", reply.Refreshed.OrderBy(r => r, StringComparer.Ordinal).ToList()), ("tiled", reply.Tiled));
                        }
                        got = Record(finished, term, subject, catnbr, section, reply, tag);
                    }
                    catch (NotRefreshedException error)
                    {
                        // A section we cannot read is one section lost, not the whole course.
                        complete = false;
                        Log.Note("skip", ("scope", "section"), ("term", term), ("subject", subject), ("catnbr", catnbr), ("section", section), ("error", error));
                        Console.WriteLine($"{tag}  ! {shortSubject} {catnbr}-{section} skipped: {Repr(error)}");
                        continue;
                    }
                    foreach (var (grade, n) in got ?? new Dictionary<string, int>())
                        parts[grade] = parts.GetValueOrDefault(grade) + n;
                }

                // Sections cannot hold more students than the whole course - the only trigger that fires on SILENT corruption.
                if (whole is { Count: > 0 })
                {
                    var over = parts.Where(p => p.Value > whole.GetValueOrDefault(p.Key)).Select(p => p.Key).ToList();
                    if (over.Count > 0)
                        Log.Note("sections_exceed_course", ("term", term), ("subject", subject), ("catnbr", catnbr), ("course", whole), ("sections", parts), ("over", over));
                }
            }
            catch (StaleDictionaryException error)
            {
                Log.Note("stale_dictionary", ("term", term), ("subject", subject), ("catnbr", catnbr), ("error", error.Message));
                throw;
            }
            catch (Exception error) when (Transport.IsTransportError(error) || error is SessionWedgedException)
            {
                throw;   // connection gone, or the session has stopped answering - neither is about this course
            }
            catch (ImpossibleValueException error)
            {
                // The dictionary no longer lines up with the responses; that does not heal. End the subject and retire the session.
                Log.Note("skip", ("scope", "subject"), ("term", term), ("subject", subject), ("catnbr", catnbr), ("error", error));
                Console.WriteLine($"{tag}! {shortSubject} abandoned at {catnbr}: {Repr(error)}");
                session.CloseSession();
                return false;
            }
            catch (Exception error)
            {
                complete = false;
                Log.Note("skip", ("scope", "course"), ("term", term), ("subject", subject), ("catnbr", catnbr), ("error", error));
                Console.WriteLine($"{tag}  ! {shortSubject} {catnbr} skipped: {Repr(error)}");
            }
        }
        return complete;
    }

    /// <summary>Every subject in one term, on one session.</summary>
    public static void SweepTerm(DashboardSession session, Progress finished, string term, IReadOnlyList<string>? subjectsOverride, string tag = "")
    {
        session.Select("term", term);

        // A term pin re-renders nothing; the first real selection is where the
        // subject list and the proof the term applied both have to come from.
        var reply = session.Select("subject", Config.BootstrapSubject);
        if (term != Config.AllTerms) reply.Confirm("term", term);
        var subjects = subjectsOverride is { Count: > 0 } ? subjectsOverride : reply.Options("subject");
        if (subjects.Count == 0) throw new RefusedException($"no subjects offered for {term} - refusing to report it swept");

        for (var position = 0; position < subjects.Count; position++)
        {
            var subject = subjects[position];
            var key = SubjectKey(term, subject);
            if (finished.Contains(key)) continue;
            if (position > 0 && position % Config.SubjectsPerSession == 0)
                session.CloseSession();   // retire the session before its dictionary has time to drift
            bool complete;
            try
            {
                complete = ScrapeSubject(session, finished, term, subject, tag);
            }
            catch (Exception error) when (Transport.IsTransportError(error) || error is StaleDictionaryException or SessionWedgedException)
            {
                throw;   // not this subject's problem: the worker drops the session, waits, and starts the term again
            }
            catch (Exception error)
            {
                Log.Note("skip", ("scope", "subject"), ("term", term), ("subject", subject), ("error", error));
                Console.WriteLine($"{tag}! {Shorten(term, subject).Subject} skipped: {Repr(error)}");
                continue;
            }
            if (complete)
            {
                MarkFinished(key);
                finished.Add(key);
            }
        }
    }

    /// <summary>Pull terms off the queue until it runs dry; a term that fails three times goes back on it.</summary>
    public static void Worker(int number, ConcurrentQueue<string> pending, Progress finished, IReadOnlyList<string>? subjects)
    {
        var tag = $"[{number}] ";
        Log.Worker = $"Thread-{number}";
        var session = new DashboardSession();
        try
        {
            while (pending.TryDequeue(out var term))
            {
                for (var attempt = 1; attempt <= Config.LoadAttempts; attempt++)
                {
                    try
                    {
                        session.Connect();
                        SweepTerm(session, finished, term, subjects, tag);
                        Console.WriteLine($"{tag}finished {term}");
                        break;
                    }
                    catch (Exception error)
                    {
                        Console.WriteLine($"{tag}error on {term}: {Repr(error)}");
                        session.SessionId = null;
                        session.Selected.Clear();
                        if (attempt == Config.LoadAttempts)
                        {
                            Log.Note("requeue", ("term", term), ("error", error));
                            Console.WriteLine($"{tag}putting {term} back on the queue.");
                            Thread.Sleep(TimeSpan.FromSeconds(Config.SecondsBetweenRetries * attempt));
                            pending.Enqueue(term);
                        }
                        else Thread.Sleep(TimeSpan.FromSeconds(Config.SecondsBetweenRetries * attempt));
                    }
                }
            }
        }
        finally
        {
            session.Close();
        }
    }

    public static int Run(IReadOnlyList<string>? termsOverride, IReadOnlyList<string>? subjectsOverride)
    {
        Log.Start();
        var finished = new Progress(LoadProgress());
        Console.WriteLine($"{finished.Count} sections already done; resuming.");

        var terms = termsOverride is { Count: > 0 } ? termsOverride.ToList() : null;
        if (terms is null)
        {
            var scout = new DashboardSession();   // one throwaway session just to read the list
            try { terms = scout.Connect()!.Options("term"); }
            finally { scout.Close(); }
        }
        if (terms.Count == 0) throw new RefusedException("no terms offered - refusing to sweep nothing and call it complete");

        // The all-terms pass rides the same queue as one more term, queued last.
        terms.Add(Config.AllTerms);

        var pending = new ConcurrentQueue<string>(terms);
        var count = Math.Max(1, Math.Min(Config.Workers, terms.Count));
        Console.WriteLine($"{terms.Count} terms across {count} workers.\n");

        var interrupted = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; interrupted = true; };
        var threads = Enumerable.Range(1, count)
            .Select(i => new Thread(() => Worker(i, pending, finished, subjectsOverride)) { IsBackground = true, Name = $"Thread-{i}" })
            .ToList();
        foreach (var thread in threads) thread.Start();
        while (threads.Any(t => t.IsAlive))
        {
            foreach (var thread in threads) thread.Join(500);
            if (interrupted)
            {
                Console.WriteLine("\ninterrupted - progress saved, rerun to resume.");
                return 130;
            }
        }
        Log.Note("complete", ("rows_marked", finished.Count));
        Console.WriteLine("SWEEP COMPLETE");
        return 0;
    }

    /// <summary>Python's repr() of an exception, near enough for the console.</summary>
    private static string Repr(Exception error) => $"{error.GetType().Name.Replace("Exception", "")}('{error.Message}')";
}

/// <summary>The set of finished keys, shared by every worker.</summary>
public sealed class Progress(HashSet<string> keys)
{
    private readonly object _gate = new();
    public int Count { get { lock (_gate) return keys.Count; } }
    public bool Contains(string key) { lock (_gate) return keys.Contains(key); }
    public void Add(string key) { lock (_gate) keys.Add(key); }
}
