using Dapper;
using Microsoft.Data.Sqlite;

namespace Ingest.Schedule.Database;

/// <summary>
/// Companion-to-lecture pairings (lab, discussion, field work) entered by hand,
/// for the courses whose lecture never published a note. Read from data/companion-pairs.tsv and written onto the lab's
/// pairs_with. A later crawl keeps them unless the registrar publishes a note,
/// which then wins.
///
/// Columns: term, campus, subject, course, lab, lecture; "lab" is any companion section. The lecture is a
/// section number, or "*" when the registrar lets any lab go with any lecture.
/// A row naming a lab or lecture that is not in the database is refused and
/// reported, never guessed at.
/// </summary>
public static class CompanionOverrides
{
    public const string DefaultFile = "data/companion-pairs.tsv";

    public sealed record Row(string Term, string Campus, string Subject, string Course, string Lab, string Lecture, int Line);

    public static List<Row> Load(string path)
    {
        var rows = new List<Row>();
        var line = 0;
        foreach (var raw in File.ReadLines(path))
        {
            line++;
            var text = raw.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;

            var cells = text.Split('\t');
            if (cells[0].Equals("term", StringComparison.OrdinalIgnoreCase)) continue;   // the header, wherever the comments leave it
            if (cells.Length != 6)
                throw new InvalidDataException($"{path} line {line}: expected 6 tab-separated columns, found {cells.Length}");

            rows.Add(new Row(cells[0].Trim(), cells[1].Trim(), cells[2].Trim(), cells[3].Trim(),
                             Pad(cells[4].Trim()), cells[5].Trim() == "*" ? "*" : Pad(cells[5].Trim()), line));
        }
        return rows;
    }

    /// <summary>"19" and "019" are the same section.</summary>
    static string Pad(string section) => int.TryParse(section, out var n) ? $"{n:D3}" : section;

    public sealed record Result(int Applied, List<string> Refused);

    public static Result Apply(SqliteConnection database, IReadOnlyList<Row> rows)
    {
        var applied = 0;
        var refused = new List<string>();

        foreach (var r in rows)
        {
            var scope = new { r.Term, r.Campus, r.Subject, r.Course };
            var sections = database.Query<(string Number, string? Component)>(
                "SELECT section_number, component FROM sections WHERE term = @Term AND campus = @Campus AND subject = @Subject AND course_number = @Course",
                scope).ToList();

            var lab = sections.FirstOrDefault(s => s.Number == r.Lab);
            if (lab.Number is null)
            {
                refused.Add($"line {r.Line}: {r.Subject} {r.Course} {r.Term} has no section {r.Lab}");
                continue;
            }
            if (lab.Component is not ("Laboratory" or "Lab/ Discussion" or "Discussion" or "Field Work"))
            {
                refused.Add($"line {r.Line}: {r.Subject} {r.Course} section {r.Lab} is a {lab.Component}, not a lab, discussion or field work section");
                continue;
            }
            if (r.Lecture != "*" && !sections.Any(s => s.Number == r.Lecture && s.Component == "Lecture"))
            {
                refused.Add($"line {r.Line}: {r.Subject} {r.Course} {r.Term} has no lecture section {r.Lecture}");
                continue;
            }

            applied += database.Execute(
                "UPDATE sections SET pairs_with = @Lecture WHERE term = @Term AND campus = @Campus AND subject = @Subject AND course_number = @Course AND section_number = @Lab",
                new { r.Lecture, r.Term, r.Campus, r.Subject, r.Course, r.Lab });
        }

        return new Result(applied, refused);
    }

    public static int Run(string databasePath, string file)
    {
        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"no override file at {file}");
            return 1;
        }
        List<Row> rows;
        try { rows = Load(file); }
        catch (InvalidDataException e)
        {
            // A malformed line is the user's to fix; say which, without a stack trace.
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        using var database = new SqliteConnection($"Data Source={databasePath}");
        database.Open();
        var result = Apply(database, rows);

        Console.WriteLine($"{file}: {rows.Count} rows, {result.Applied} applied");
        foreach (var why in result.Refused) Console.WriteLine($"  refused {why}");
        return result.Refused.Count == 0 ? 0 : 2;
    }
}
