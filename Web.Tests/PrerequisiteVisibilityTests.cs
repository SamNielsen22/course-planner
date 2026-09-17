using CoursePlanner.Data;

namespace Web.Tests;

/// <summary>
/// A no-credit course that another course requires stays searchable, while a
/// no-credit course nothing depends on is hidden as a non-credit shadow.
/// </summary>
public class PrerequisiteVisibilityTests
{
    private static CourseQueries Db() => new($"Data Source={Site.DatabasePath};Mode=ReadOnly");

    [Fact]
    public void APrerequisiteCourseWithNoCreditStaysInTheCatalogSearch()
    {
        var db = Db();
        var prereqs = db.PrereqCourses;

        // A three-digit course that never carried credit but is named as a prerequisite.
        var (subject, number) = ThreeDigitNoCredit(db, mustBePrereq: true);
        Assert.Contains($"{subject}|{number}", prereqs);

        var results = db.SearchAllCourses($"{subject} {number}");
        Assert.Contains(results, c => c.Subject == subject && c.CourseNumber == number);
    }

    [Fact]
    public void ANoCreditCourseNothingRequiresIsHidden()
    {
        var db = Db();
        var (subject, number) = ThreeDigitNoCredit(db, mustBePrereq: false);

        var results = db.SearchAllCourses($"{subject} {number}");
        Assert.DoesNotContain(results, c => c.Subject == subject && c.CourseNumber == number);
    }

    /// <summary>A three-digit course with no credit-bearing section, that is (or is not) a prerequisite.</summary>
    private static (string Subject, string Number) ThreeDigitNoCredit(CourseQueries db, bool mustBePrereq)
    {
        var prereqs = db.PrereqCourses;
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Site.DatabasePath};Mode=ReadOnly");
        conn.Open();
        var rows = Dapper.SqlMapper.Query<(string Subject, string Number)>(conn, @"
            SELECT DISTINCT c.subject AS Subject, c.course_number AS Number
            FROM courses c
            WHERE LENGTH(c.course_number) = 3
              AND NOT EXISTS (SELECT 1 FROM sections s WHERE s.subject = c.subject
                              AND s.course_number = c.course_number AND s.units > 0)
            ORDER BY c.subject, c.course_number");
        foreach (var r in rows)
            if (prereqs.Contains($"{r.Subject}|{r.Number}") == mustBePrereq)
                return r;
        throw new InvalidOperationException($"no three-digit no-credit course with mustBePrereq={mustBePrereq}");
    }
}
