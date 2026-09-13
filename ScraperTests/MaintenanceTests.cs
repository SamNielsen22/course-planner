using Dapper;
using Ingest.Schedule.Database;
using Microsoft.Data.Sqlite;

namespace ScraperTests;

/// <summary>
/// The grade loader and the two database helpers, against a throwaway
/// database built from the real schema.sql - the same footing the query
/// tests stand on.
/// </summary>
public class MaintenanceTests : IDisposable
{
    private readonly string path;
    private readonly string connectionString;

    public MaintenanceTests()
    {
        path = Path.Combine(Path.GetTempPath(), $"courseplanner-maint-{Guid.NewGuid():N}.db");
        connectionString = $"Data Source={path}";
        using var db = Open();
        db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema.sql")));
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    private const string Csv = """
        term,subject,catnbr,section,avg_gpa,p25,p50,p75,std_dev,grade_a,grade_b,grade_c,grade_d,grade_e,grade_cr,grade_nc,grade_w,grade_other
        Fall 2021,ACCTG,3000,(all),3.02,2.70,3.30,3.70,1.13,23,17,7,,,,,,
        Fall 2021,ACCTG,3000,001,3.10,2.70,3.30,3.70,1.00,12,9,4,,,,,,
        Fall 2021,ACCTG,3000,001,3.20,2.70,3.30,3.70,1.00,13,9,4,,,,,,
        (all),ACCTG,3000,(all),3.05,2.70,3.30,3.70,1.10,230,170,70,,,,,,
        Fall 2021,CS,2420,003,,,,,,,,,,,,,,
        Fall 2021,KINES,3092,001,0,0,0,0,0,,,,,,15,2,,
        Fall 2021,MATH,1210,002,3092,2.7,3.3,3.7,1.0,10,8,3,,,,,,
        """;

    [Fact]
    public void ReadCsv_RoutesRowsByGrainAndAppliesTheRules()
    {
        var read = GradeLoader.ReadCsv(new StringReader(Csv));

        Assert.Equal(1, read.Blank);        // the lab with nothing published
        Assert.Equal(1, read.Duplicates);   // ACCTG 3000-001 twice
        Assert.Equal(5, read.Graded.Count);

        // The dashboard's "Fall 2021" is stored as the crawler's "Fall2021"; the later duplicate won.
        var section = read.Graded[new GradeLoader.Key("Fall2021", "ACCTG", "3000", "001")];
        Assert.Equal(3.20, section.GpaAvg);
        Assert.Equal(13, section.A);

        // Every statistic zero is "no letter grades", not everyone failing: stats go, headcounts stay.
        var creditOnly = read.Graded[new GradeLoader.Key("Fall2021", "KINES", "3092", "001")];
        Assert.Null(creditOnly.GpaAvg);
        Assert.Equal(15, creditOnly.Cr);
        Assert.Equal(2, creditOnly.Nc);

        // A course number that strayed into the GPA cell is dropped, the rest of the row kept.
        var stray = read.Graded[new GradeLoader.Key("Fall2021", "MATH", "1210", "002")];
        Assert.Null(stray.GpaAvg);
        Assert.Equal(2.7, stray.GpaP25);
    }

    [Fact]
    public void Load_WritesEachGrainToItsOwnTableAndIsIdempotent()
    {
        var read = GradeLoader.ReadCsv(new StringReader(Csv));
        using var db = Open();

        var loaded = GradeLoader.Load(db, read.Graded);
        Assert.Equal(3, loaded.Sections);
        Assert.Equal(1, loaded.Courses);
        Assert.Equal(1, loaded.Totals);
        Assert.Equal(3.02, db.ExecuteScalar<double>("SELECT gpa_avg FROM course_term_grades WHERE term='Fall2021' AND subject='ACCTG' AND course_number='3000'"));
        Assert.Equal(230, db.ExecuteScalar<int>("SELECT grade_a FROM course_grades WHERE subject='ACCTG' AND course_number='3000'"));

        // Loading again overwrites rather than duplicates.
        GradeLoader.Load(db, read.Graded);
        Assert.Equal(3, db.ExecuteScalar<int>("SELECT COUNT(*) FROM section_grades"));
        Assert.Equal(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM course_grades"));
    }

    [Fact]
    public void Departments_FollowTeachingLoadWithTiesToTheBiggerDepartment()
    {
        using var db = Open();
        db.Execute("""
            INSERT INTO courses (subject, course_number, title) VALUES ('CS','1410','a'),('MATH','1210','b'),('ECE','1240','c'),('XYZ','1000','d');
            INSERT INTO sections (term, subject, course_number, section_number) VALUES
              ('Fall2026','CS','1410','001'),('Fall2026','CS','1410','002'),('Fall2026','MATH','1210','001'),
              ('Fall2026','ECE','1240','001'),('Fall2026','XYZ','1000','001');
            INSERT INTO instructors (unid, display_name) VALUES ('u1','A'),('u2','B'),('u3','C'),('u4','D');
            INSERT INTO section_instructors (term, subject, course_number, section_number, instructor_unid) VALUES
              ('Fall2026','CS','1410','001','u1'),('Fall2026','CS','1410','002','u1'),('Fall2026','MATH','1210','001','u1'),
              ('Fall2026','CS','1410','001','u2'),('Fall2026','ECE','1240','001','u2'),
              ('Fall2026','XYZ','1000','001','u3');
            """);
        var mapping = new Dictionary<string, string> { ["CS"] = "School of Computing", ["MATH"] = "Mathematics", ["ECE"] = "Electrical and Computer Engineering" };

        var result = Departments.Assign(db, mapping);

        Assert.Equal("School of Computing", db.ExecuteScalar<string>("SELECT department FROM instructors WHERE unid='u1'"));   // two CS sections beat one MATH
        Assert.Equal("School of Computing", db.ExecuteScalar<string>("SELECT department FROM instructors WHERE unid='u2'"));   // a tie, broken toward the bigger department
        Assert.Null(db.ExecuteScalar<string>("SELECT department FROM instructors WHERE unid='u3'"));                           // only an unmapped subject
        Assert.Null(db.ExecuteScalar<string>("SELECT department FROM instructors WHERE unid='u4'"));                           // teaches nothing
        Assert.Equal(4, result.Instructors);
        Assert.Equal(2, result.Named);
        Assert.Equal(1, result.Unmapped["XYZ"]);
    }

    [Fact]
    public void Departments_MappingFileCoversTheSubjects()
    {
        var mapping = Departments.LoadMapping(Path.Combine(AppContext.BaseDirectory, "Database", "departments.csv"));
        Assert.True(mapping.Count > 200);
        Assert.Equal("School of Accounting", mapping["ACCTG"]);
    }

    [Fact]
    public void Titles_ReadTheTermCodeAndTheHeading()
    {
        Assert.Equal("1268", Titles.TermCode("Fall2026"));
        Assert.Equal("1274", Titles.TermCode("Spring2027"));
        Assert.Equal("1246", Titles.TermCode("Summer2024"));
        var page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "CS2420.html"));
        Assert.Equal("Introduction to Algorithms & Data Structures", Titles.FullTitle(page));
        Assert.Equal("", Titles.FullTitle("<html><body><p>no heading</p></body></html>"));
    }
}
