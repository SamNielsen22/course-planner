namespace CoursePlanner.Data;

/// <summary>Terms kept off every picker, from the Terms:Hidden setting. Their data stays stored.</summary>
public sealed record HiddenTerms(IReadOnlySet<string> Names)
{
    public static readonly HiddenTerms None = new(new HashSet<string>());
}

/// <summary>
/// Lists that change only when the crawler runs, read once at startup.
/// Call <see cref="Rebuild"/> after a crawl or a grade load.
/// </summary>
public class SiteIndex(CourseQueries db, GradeIndex grades, HiddenTerms hidden)
{
    private sealed record Snapshot(
        IReadOnlyList<string> Terms,
        IReadOnlyList<string> GradeTerms,
        IReadOnlyList<string> Departments,
        IReadOnlyList<string> Designations,
        CoverageCounts Coverage,
        IReadOnlyDictionary<string, double> CourseAverages,
        IReadOnlyList<(string Subject, string Number)> Courses);

    private volatile Snapshot _now = Build(db, grades, hidden);

    private static Snapshot Build(CourseQueries db, GradeIndex grades, HiddenTerms hidden) => new(
        db.GetTerms().Where(t => !hidden.Names.Contains(t)).ToList(),
        db.GradeTerms(),
        db.Departments(),
        db.Designations(),
        db.Coverage(),
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

    /// <summary>How much data there is.</summary>
    public CoverageCounts Coverage => _now.Coverage;

    /// <summary>"SUBJ|NUMBER" to the course's average across every term.</summary>
    public IReadOnlyDictionary<string, double> CourseAverages => _now.CourseAverages;

    /// <summary>Every course, for the sitemap.</summary>
    public IReadOnlyList<(string Subject, string Number)> Courses => _now.Courses;

    /// <summary>Re-reads everything, for after a crawl or a grade load.</summary>
    public void Rebuild() => _now = Build(db, grades, hidden);
}
