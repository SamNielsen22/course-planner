namespace CoursePlanner.Data;

/// <summary>
/// Lists that change only when the crawler runs, read once at startup.
/// Call <see cref="Rebuild"/> after a crawl or a grade load.
/// </summary>
public class SiteIndex(CourseQueries db, GradeIndex grades)
{
    private sealed record Snapshot(
        IReadOnlyList<string> Terms,
        IReadOnlyList<string> GradeTerms,
        IReadOnlyList<string> Departments,
        IReadOnlyList<string> Designations,
        IReadOnlyDictionary<string, double> CourseAverages,
        IReadOnlyList<(string Subject, string Number)> Courses);

    private volatile Snapshot _now = Build(db, grades);

    private static Snapshot Build(CourseQueries db, GradeIndex grades) => new(
        db.GetTerms(),
        db.GradeTerms(),
        db.Departments(),
        db.Designations(),
        grades.CourseAverages,
        db.AllCourses());

    /// <summary>Every offered term with sections, in storage order.</summary>
    public IReadOnlyList<string> Terms => _now.Terms;

    /// <summary>Every term with published grades.</summary>
    public IReadOnlyList<string> GradeTerms => _now.GradeTerms;

    /// <summary>Every subject code that has a section.</summary>
    public IReadOnlyList<string> Departments => _now.Departments;

    /// <summary>Requirement designations, for the filter.</summary>
    public IReadOnlyList<string> Designations => _now.Designations;

    /// <summary>"SUBJ|NUMBER" to the course's average across every term.</summary>
    public IReadOnlyDictionary<string, double> CourseAverages => _now.CourseAverages;

    /// <summary>Every course, for the sitemap.</summary>
    public IReadOnlyList<(string Subject, string Number)> Courses => _now.Courses;

    /// <summary>Re-reads everything, for after a crawl or a grade load.</summary>
    public void Rebuild() => _now = Build(db, grades);
}
