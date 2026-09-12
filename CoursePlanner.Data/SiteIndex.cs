namespace CoursePlanner.Data;

/// <summary>
/// Reference data that changes only when the crawler runs, held in memory.
///
/// Every builder page load used to re-read the term list, the subject list and
/// the requirement designations - 19, 240 and 36 rows, about 16ms of the
/// request - and the About page re-counted the whole database for seven
/// figures. None of it moves between crawls. Read once here; the pages read
/// fields.
///
/// Built at startup and not watched. After a re-crawl or a grade load, restart
/// the site or call <see cref="Rebuild"/>.
/// </summary>
public class SiteIndex(CourseQueries db, GradeIndex grades)
{
    private sealed record Snapshot(
        IReadOnlyList<string> Terms,
        IReadOnlyList<string> GradeTerms,
        IReadOnlyList<string> Departments,
        IReadOnlyList<string> Designations,
        CoverageCounts Coverage,
        IReadOnlyDictionary<string, double> CourseAverages,
        IReadOnlyList<(string Subject, string Number)> Courses);

    private volatile Snapshot _now = Build(db, grades);

    private static Snapshot Build(CourseQueries db, GradeIndex grades) => new(
        db.GetTerms(),
        db.GradeTerms(),
        db.Departments(),
        db.Designations(),
        db.Coverage(),
        grades.CourseAverages,
        db.AllCourses());

    /// <summary>Every term with a section, in storage order. Pages sort as they need.</summary>
    public IReadOnlyList<string> Terms => _now.Terms;

    /// <summary>Every term with published grades - the historical lookup's range.</summary>
    public IReadOnlyList<string> GradeTerms => _now.GradeTerms;

    /// <summary>Every subject code that has a section.</summary>
    public IReadOnlyList<string> Departments => _now.Departments;

    /// <summary>Gen-ed designation atoms, for the requirement filter.</summary>
    public IReadOnlyList<string> Designations => _now.Designations;

    /// <summary>The size of the data, for the About page.</summary>
    public CoverageCounts Coverage => _now.Coverage;

    /// <summary>"SUBJ|NUMBER" to the course's headcount-weighted GPA across every term.</summary>
    public IReadOnlyDictionary<string, double> CourseAverages => _now.CourseAverages;

    /// <summary>Every course, for the sitemap.</summary>
    public IReadOnlyList<(string Subject, string Number)> Courses => _now.Courses;

    /// <summary>Re-reads everything. For after a crawl or a grade load.</summary>
    public void Rebuild() => _now = Build(db, grades);
}
