using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Ingest.Schedule.Database;

/// <summary>
/// Loads the GPA csv into the grade tables, routed by grain: a real section
/// number is a section row, "(all)" is the course's row for that term, and
/// "(all)" in both columns is the course across every term. The three cannot
/// be derived from each other, because groups under five students are
/// suppressed. Nothing is matched against the schedule, and rerunning is safe.
/// </summary>
public static class GradeLoader
{
    /// <summary>The scraper's marker for a whole-course row and an all-terms row.</summary>
    public const string CourseSection = "(all)";
    public const string AllTerms = "(all)";

    public sealed record Key(string Term, string Subject, string CourseNumber, string SectionNumber);

    /// <summary>One row's figures. Null where nothing was published.</summary>
    public sealed record Figures(
        double? GpaAvg, double? GpaP25, double? GpaP50, double? GpaP75, double? GpaStdDev,
        int? A, int? B, int? C, int? D, int? E, int? Cr, int? Nc, int? W, int? Other)
    {
        public IEnumerable<double?> Stats => [GpaAvg, GpaP25, GpaP50, GpaP75, GpaStdDev];
        public IEnumerable<int?> Counts => [A, B, C, D, E, Cr, Nc, W, Other];
        public bool HasStats => Stats.Any(s => s is not null);
        public bool HasCounts => Counts.Any(c => c is not null);
        public Figures WithoutStats() => this with { GpaAvg = null, GpaP25 = null, GpaP50 = null, GpaP75 = null, GpaStdDev = null };
    }

    /// <summary>What a csv held, keyed by term, subject, course and section. The last row wins.</summary>
    public sealed record Read(Dictionary<Key, Figures> Graded, int Blank, int Duplicates);

    /// <summary>"Fall 2020" as the catalogue's "Fall2020".</summary>
    public static string NormalizeTerm(string term) => string.Concat(term.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>A grade point average, or null. Anything outside 0-4 is a stray cell.</summary>
    public static double? ToGpa(string? value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return null;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
        return number is >= 0.0 and <= 4.0 ? number : null;
    }

    /// <summary>A headcount, or null. Older rows have no grade columns.</summary>
    public static int? ToCount(string? value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return null;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
        var whole = (int)Math.Truncate(number);
        return whole >= 0 ? whole : null;
    }

    private static readonly string[] StatColumns = ["avg_gpa", "p25", "p50", "p75", "std_dev"];
    private static readonly string[] CountColumns = ["grade_a", "grade_b", "grade_c", "grade_d", "grade_e", "grade_cr", "grade_nc", "grade_w", "grade_other"];

    public static Read ReadCsv(TextReader reader)
    {
        var graded = new Dictionary<Key, Figures>();
        var blank = 0;
        var duplicates = 0;

        var headerLine = reader.ReadLine() ?? throw new InvalidDataException("the csv has no header");
        var header = SplitCsv(headerLine).Select((name, i) => (name, i)).ToDictionary(p => p.name, p => p.i);
        foreach (var required in new[] { "term", "subject", "catnbr", "section" })
            if (!header.ContainsKey(required)) throw new InvalidDataException($"the csv has no '{required}' column");

        string? Cell(string[] cells, string column) => header.TryGetValue(column, out var i) && i < cells.Length ? cells[i] : null;

        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            var cells = SplitCsv(line);
            var stats = StatColumns.Select(c => ToGpa(Cell(cells, c))).ToArray();
            var counts = CountColumns.Select(c => ToCount(Cell(cells, c))).ToArray();
            var figures = new Figures(stats[0], stats[1], stats[2], stats[3], stats[4],
                                      counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6], counts[7], counts[8]);

            // All zeroes means no letter grades were awarded, not that everyone
            // failed: drop the statistics, keep the headcounts.
            var published = figures.Stats.Where(s => s is not null).ToList();
            if (published.Count > 0 && published.All(s => s == 0.0)) figures = figures.WithoutStats();

            if (!figures.HasStats && !figures.HasCounts) { blank++; continue; }

            var key = new Key(NormalizeTerm(Cell(cells, "term") ?? ""), Cell(cells, "subject") ?? "", Cell(cells, "catnbr") ?? "", Cell(cells, "section") ?? "");
            if (graded.ContainsKey(key)) duplicates++;
            graded[key] = figures;
        }
        return new Read(graded, blank, duplicates);
    }

    /// <summary>A csv line's cells, quotes honoured.</summary>
    public static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells.ToArray();
    }

    public sealed record Loaded(int Sections, int Courses, int Totals);

    /// <summary>Routes each row to the table for its grain and overwrites it there.</summary>
    public static Loaded Load(SqliteConnection database, IReadOnlyDictionary<Key, Figures> graded)
    {
        var sections = new Dictionary<Key, Figures>();
        var courses = new Dictionary<(string Term, string Subject, string Course), Figures>();
        var totals = new Dictionary<(string Subject, string Course), Figures>();
        foreach (var (key, figures) in graded)
        {
            if (key.Term == AllTerms && key.SectionNumber == CourseSection) totals[(key.Subject, key.CourseNumber)] = figures;
            else if (key.SectionNumber == CourseSection) courses[(key.Term, key.Subject, key.CourseNumber)] = figures;
            else sections[key] = figures;
        }

        const string values = "gpa_avg, gpa_p25, gpa_p50, gpa_p75, gpa_std_dev, grade_a, grade_b, grade_c, grade_d, grade_e, grade_cr, grade_nc, grade_w, grade_other";
        const string holes = "@GpaAvg, @GpaP25, @GpaP50, @GpaP75, @GpaStdDev, @A, @B, @C, @D, @E, @Cr, @Nc, @W, @Other";

        using var tx = database.BeginTransaction();
        foreach (var (key, f) in sections)
            database.Execute($"INSERT OR REPLACE INTO section_grades (term, subject, course_number, section_number, {values}) VALUES (@Term, @Subject, @CourseNumber, @SectionNumber, {holes})",
                new { key.Term, key.Subject, key.CourseNumber, key.SectionNumber, f.GpaAvg, f.GpaP25, f.GpaP50, f.GpaP75, f.GpaStdDev, f.A, f.B, f.C, f.D, f.E, f.Cr, f.Nc, f.W, f.Other }, tx);
        foreach (var (key, f) in courses)
            database.Execute($"INSERT OR REPLACE INTO course_term_grades (term, subject, course_number, {values}) VALUES (@Term, @Subject, @CourseNumber, {holes})",
                new { key.Term, key.Subject, CourseNumber = key.Course, f.GpaAvg, f.GpaP25, f.GpaP50, f.GpaP75, f.GpaStdDev, f.A, f.B, f.C, f.D, f.E, f.Cr, f.Nc, f.W, f.Other }, tx);
        foreach (var (key, f) in totals)
            database.Execute($"INSERT OR REPLACE INTO course_grades (subject, course_number, {values}) VALUES (@Subject, @CourseNumber, {holes})",
                new { key.Subject, CourseNumber = key.Course, f.GpaAvg, f.GpaP25, f.GpaP50, f.GpaP75, f.GpaStdDev, f.A, f.B, f.C, f.D, f.E, f.Cr, f.Nc, f.W, f.Other }, tx);
        tx.Commit();
        return new Loaded(sections.Count, courses.Count, totals.Count);
    }

    /// <summary>The `grades` command. Several csvs can be given; a repeated key is taken from the later file.</summary>
    public static int Run(string databasePath, IReadOnlyList<string> csvPaths)
    {
        if (!File.Exists(databasePath)) { Console.Error.WriteLine($"no database at {databasePath}"); return 1; }
        if (csvPaths.Count == 0) { Console.Error.WriteLine("name at least one csv to load"); return 1; }
        foreach (var path in csvPaths)
            if (!File.Exists(path)) { Console.Error.WriteLine($"no csv at {path}"); return 1; }

        var graded = new Dictionary<Key, Figures>();
        var blank = 0;
        var duplicates = 0;
        foreach (var path in csvPaths)
        {
            using var reader = new StreamReader(path);
            var read = ReadCsv(reader);
            var overlap = read.Graded.Keys.Count(graded.ContainsKey);
            if (overlap > 0)
                Console.WriteLine($"warning: {overlap} keys in {Path.GetFileName(path)} were already read from an earlier file - the later one wins");
            foreach (var (key, figures) in read.Graded) graded[key] = figures;
            blank += read.Blank;
            duplicates += read.Duplicates;
        }

        Loaded loaded;
        using (var database = new SqliteConnection($"Data Source={databasePath}"))
        {
            database.Open();
            loaded = Load(database, graded);
        }

        Console.WriteLine($"read {graded.Count + blank} rows from {string.Join(", ", csvPaths.Select(Path.GetFileName))}");
        Console.WriteLine($"  {graded.Count,6} with grades or headcounts");
        Console.WriteLine($"  {blank,6} with none published (labs, discussions) - skipped");
        if (duplicates > 0) Console.WriteLine($"  {duplicates,6} repeated keys - last row won");
        Console.WriteLine();
        Console.WriteLine($"section_grades     {loaded.Sections,7:N0} rows");
        Console.WriteLine($"course_term_grades {loaded.Courses,7:N0} rows");
        Console.WriteLine($"course_grades      {loaded.Totals,7:N0} rows");
        Console.WriteLine("\n  by term:");
        foreach (var group in graded.Keys.GroupBy(k => k.Term).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"    {group.Key,-12} {group.Count()}");
        return 0;
    }
}
