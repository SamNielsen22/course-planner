using Dapper;
using Microsoft.Data.Sqlite;

namespace Ingest.Schedule.Database;

/// <summary>
/// Give every instructor a department, inferred from what they teach.
///
/// The registrar publishes no department for a person - only the subject
/// code on each section. So the department is worked out from teaching load:
/// map each subject to a department (departments.csv, beside this file), then
/// give the instructor whichever department they teach the most sections in.
///
/// The result is written to instructors.department rather than computed on
/// read. Deriving it means grouping every section a person has ever taught,
/// and the professor search shows twenty-four people at a time; that is
/// twenty-four such groupings per keystroke. It changes only when the
/// schedule is re-crawled, so run this after a crawl.
///
/// Ties are broken toward the department with more sections overall, which
/// keeps a person who taught one class in each of two units with the one
/// they belong to rather than with whichever sorted first.
///
/// Ported from AssignDepartments.py on 2026-09-13.
/// </summary>
public static class Departments
{
    /// <summary>subject -> department, from a two-column csv with a header.</summary>
    public static Dictionary<string, string> LoadMapping(string path)
    {
        var mapping = new Dictionary<string, string>();
        using var reader = new StreamReader(path);
        var header = GradeLoader.SplitCsv(reader.ReadLine() ?? "");
        var subjectAt = Array.IndexOf(header, "subject");
        var departmentAt = Array.IndexOf(header, "department");
        if (subjectAt < 0 || departmentAt < 0) throw new InvalidDataException($"{path} needs 'subject' and 'department' columns");
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            var cells = GradeLoader.SplitCsv(line);
            if (cells.Length > Math.Max(subjectAt, departmentAt) && cells[subjectAt].Length > 0)
                mapping[cells[subjectAt]] = cells[departmentAt];
        }
        return mapping;
    }

    public sealed record Result(int Instructors, int Named, IReadOnlyDictionary<string, int> Unmapped, IReadOnlyList<(string Department, int Count)> Largest);

    public static Result Assign(SqliteConnection database, IReadOnlyDictionary<string, string> subjectToDepartment)
    {
        // Every subject a person has taught, and how many sections in each.
        var taught = new Dictionary<string, Dictionary<string, int>>();
        foreach (var row in database.Query<(string Unid, string Subject)>("SELECT instructor_unid, subject FROM section_instructors"))
        {
            if (!taught.TryGetValue(row.Unid, out var subjects)) taught[row.Unid] = subjects = new Dictionary<string, int>();
            subjects[row.Subject] = subjects.GetValueOrDefault(row.Subject) + 1;
        }

        // How big each department is overall, for tie-breaking.
        var size = new Dictionary<string, int>();
        foreach (var subjects in taught.Values)
            foreach (var (subject, count) in subjects)
                if (subjectToDepartment.TryGetValue(subject, out var department))
                    size[department] = size.GetValueOrDefault(department) + count;

        var assignments = new List<(string Department, string Unid)>();
        var unmapped = new Dictionary<string, int>();
        foreach (var (unid, subjects) in taught)
        {
            var totals = new Dictionary<string, int>();
            foreach (var (subject, count) in subjects)
            {
                if (subjectToDepartment.TryGetValue(subject, out var department)) totals[department] = totals.GetValueOrDefault(department) + count;
                else unmapped[subject] = unmapped.GetValueOrDefault(subject) + count;
            }
            if (totals.Count == 0) continue;
            var best = totals.OrderByDescending(t => t.Value).ThenByDescending(t => size.GetValueOrDefault(t.Key)).First();
            assignments.Add((best.Key, unid));
        }

        using (var tx = database.BeginTransaction())
        {
            foreach (var (department, unid) in assignments)
                database.Execute("UPDATE instructors SET department = @Department WHERE unid = @Unid", new { Department = department, Unid = unid }, tx);
            tx.Commit();
        }

        var total = database.ExecuteScalar<int>("SELECT COUNT(*) FROM instructors");
        var named = database.ExecuteScalar<int>("SELECT COUNT(*) FROM instructors WHERE department IS NOT NULL");
        var largest = database.Query<(string Department, int Count)>(
            "SELECT department, COUNT(*) FROM instructors WHERE department IS NOT NULL GROUP BY department ORDER BY COUNT(*) DESC LIMIT 10").ToList();
        return new Result(total, named, unmapped, largest);
    }

    /// <summary>The command: `departments [--db path]`. The mapping is the departments.csv shipped beside the program.</summary>
    public static int Run(string databasePath)
    {
        if (!File.Exists(databasePath)) { Console.Error.WriteLine($"no database at {databasePath}"); return 1; }
        var mappingPath = Path.Combine(AppContext.BaseDirectory, "Database", "departments.csv");
        if (!File.Exists(mappingPath)) { Console.Error.WriteLine($"no mapping at {mappingPath}"); return 1; }

        Result result;
        using (var database = new SqliteConnection($"Data Source={databasePath}"))
        {
            database.Open();
            result = Assign(database, LoadMapping(mappingPath));
        }

        Console.WriteLine($"instructors            : {result.Instructors:N0}");
        Console.WriteLine($"  given a department   : {result.Named:N0}  ({(result.Instructors == 0 ? 0 : result.Named * 100.0 / result.Instructors):0.0}%)");
        Console.WriteLine($"  left without one     : {result.Instructors - result.Named:N0}  (no sections, or only unmapped subjects)");
        if (result.Unmapped.Count > 0)
            Console.WriteLine("  subjects with no mapping: " + string.Join(", ", result.Unmapped.OrderByDescending(u => u.Value).Take(8).Select(u => $"{u.Key} ({u.Value})")));
        Console.WriteLine("\nlargest departments by instructor count:");
        foreach (var (department, count) in result.Largest)
            Console.WriteLine($"   {department,-44} {count,5:N0}");
        return 0;
    }
}
