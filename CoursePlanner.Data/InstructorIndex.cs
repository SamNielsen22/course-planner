namespace CoursePlanner.Data;

/// <summary>
/// Every instructor with their department and average, in memory. Computing
/// the averages per request cost about 450ms, so they are computed once.
/// Built at startup; call <see cref="Rebuild"/> after a grade load.
/// </summary>
public class InstructorIndex(CourseQueries db, GradeIndex grades)
{
    /// <summary>One instructor, as the search ranks them.</summary>
    public record Entry(string Unid, string Name, string? Department,
                        int SectionCount, int GradedCourses, double? AvgGpa);

    private volatile IReadOnlyList<Entry> _all = Shuffled(WithAverages(db.InstructorAverages(), grades));

    // Averages come from GradeIndex, so cards and pages agree.
    private static IReadOnlyList<Entry> WithAverages(IReadOnlyList<Entry> entries, GradeIndex grades) =>
        entries.Select(e => e with { AvgGpa = grades.InstructorAverage(e.Unid) }).ToList();

    /// <summary>
    /// The default order is a weighted shuffle, drawn once per start. People
    /// with 3 to 8 graded classes are favoured, or the front page would be
    /// mostly empty cards.
    /// </summary>
    private const double NudgeWeight = 8.0;

    private static IReadOnlyList<Entry> Shuffled(IReadOnlyList<Entry> entries)
    {
        // Weighted draw without replacement: sort by -ln(U)/w.
        var rng = Random.Shared;
        return entries
            .Select(e => (Entry: e,
                          Key: -Math.Log(rng.NextDouble()) /
                               (e.GradedCourses is >= 3 and <= 8 ? NudgeWeight : 1.0)))
            .OrderBy(x => x.Key)
            .Select(x => x.Entry)
            .ToList();
    }

    public int Count => _all.Count;

    /// <summary>Everyone, in index order. Used by the sitemap.</summary>
    public IReadOnlyList<Entry> All => _all;

    /// <summary>Re-reads everything, for after a grade load.</summary>
    public void Rebuild() => _all = Shuffled(WithAverages(db.InstructorAverages(), grades));

    /// <summary>Everyone whose name contains the query, in index order. There is no GPA sort on purpose.</summary>
    public (IReadOnlyList<InstructorCard> Cards, int Total) Search(
        string? query, int page = 1, int pageSize = 24)
    {
        var all = _all;   // one read, so a rebuild cannot change the list mid-search
        var q = (query ?? "").Trim();

        var matched = all
            .Where(e => q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var shown = matched.Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize).ToList();
        if (shown.Count == 0) return ([], matched.Count);

        var courses = db.InstructorCourseAverages(shown.Select(e => e.Unid).ToList());
        var cards = shown.Select(e => new InstructorCard(
            e.Unid, e.Name, e.Department, e.SectionCount, e.AvgGpa,
            courses.GetValueOrDefault(e.Unid, [])
                .Select(c => c with { AvgGpa = grades.InstructorCourseAverage(e.Unid, c.Subject, c.CourseNumber) })
                .ToList())).ToList();

        return (cards, matched.Count);
    }
}
