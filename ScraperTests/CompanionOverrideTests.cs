using Dapper;
using Ingest.Schedule.Database;
using Microsoft.Data.Sqlite;

namespace ScraperTests;

/// <summary>
/// The hand-entered pairing file: what it accepts, what it refuses, and that a
/// refusal names the line. Its own fixture, since Apply writes to the database.
/// </summary>
public class CompanionOverrideTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase database;
    public CompanionOverrideTests(TestDatabase database) => this.database = database;

    private static string Tsv(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"companion-pairs-{Guid.NewGuid():N}.tsv");
        File.WriteAllLines(path, lines);
        return path;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(database.ConnectionString);
        db.Open();
        return db;
    }

    [Fact]
    public void Load_SkipsCommentsAndTheHeaderWhereverItSitsAndPadsSectionNumbers()
    {
        var path = Tsv(
            "# a comment",
            "",
            "# another, then the header on line 4",
            "term\tcampus\tsubject\tcourse\tlab\tlecture",
            "Fall2026\tmain\tCS\t2420\t3\t2",
            "Fall2026\tmain\tCS\t2420\t004\t*");

        var rows = CompanionOverrides.Load(path);

        Assert.Equal(2, rows.Count);
        Assert.Equal(("003", "002", 5), (rows[0].Lab, rows[0].Lecture, rows[0].Line));
        Assert.Equal(("004", "*", 6), (rows[1].Lab, rows[1].Lecture, rows[1].Line));
    }

    [Fact]
    public void Load_RefusesALineWithTheWrongNumberOfColumnsAndSaysWhich()
    {
        var path = Tsv("term\tcampus\tsubject\tcourse\tlab\tlecture", "Fall2026\tmain\tCS\t2420\t003");
        var error = Assert.Throws<InvalidDataException>(() => CompanionOverrides.Load(path));
        Assert.Contains("line 2", error.Message);
        Assert.Contains("found 5", error.Message);
    }

    [Fact]
    public void Apply_WritesAValidRowAndRefusesEachKindOfBadOne()
    {
        using var db = Open();
        db.Execute("UPDATE sections SET pairs_with = NULL WHERE term = 'Fall2026' AND subject = 'CS' AND course_number = '2420'");

        var rows = new[]
        {
            new CompanionOverrides.Row("Fall2026", "main", "CS", "2420", "003", "002", 1),   // fine: lab 003 to lecture 002
            new CompanionOverrides.Row("Fall2026", "main", "CS", "2420", "009", "002", 2),   // no section 009
            new CompanionOverrides.Row("Fall2026", "main", "CS", "2420", "001", "002", 3),   // 001 is a lecture, not a lab
            new CompanionOverrides.Row("Fall2026", "main", "CS", "2420", "003", "007", 4),   // no lecture 007
            new CompanionOverrides.Row("Fall2026", "main", "CS", "2420", "003", "*", 5),     // any lecture is allowed
        };

        var result = CompanionOverrides.Apply(db, rows);

        Assert.Equal(2, result.Applied);
        Assert.Equal(3, result.Refused.Count);
        Assert.Contains(result.Refused, r => r.StartsWith("line 2:") && r.Contains("no section 009"));
        Assert.Contains(result.Refused, r => r.StartsWith("line 3:") && r.Contains("is a Lecture"));
        Assert.Contains(result.Refused, r => r.StartsWith("line 4:") && r.Contains("no lecture section 007"));

        // The last valid row wins, and the refused ones changed nothing.
        Assert.Equal("*", db.ExecuteScalar<string>("SELECT pairs_with FROM sections WHERE term = 'Fall2026' AND subject = 'CS' AND course_number = '2420' AND section_number = '003'"));
        Assert.Null(db.ExecuteScalar<string?>("SELECT pairs_with FROM sections WHERE term = 'Fall2026' AND subject = 'CS' AND course_number = '2420' AND section_number = '001'"));
    }
}
