namespace CoursePlanner.Data;

/// <summary>
/// Every instructor, with the department they teach in and their headcount-
/// weighted average, held in memory and searched there.
///
/// The average is the reason this exists. Computing it in SQL means joining
/// 142,000 section assignments against 49,000 graded sections, grouping the
/// lot, and then keeping twenty-four rows - about 450ms of work to answer a
/// question about one page. Under twenty concurrent readers that put the
/// professor search at 6.7 requests a second with a five-second p95, roughly a
/// hundredth of what every other page manages.
///
/// So it is computed once and the whole set is kept: 7,900 records, a little
/// over a megabyte, sorted and paged in memory. Only the courses shown on the
/// visible cards are fetched per request.
///
/// The index is built at startup and does not watch the database. Reloading
/// grades - a new GPA sweep landing in section_grades - means restarting the
/// site, or calling <see cref="Rebuild"/>.
/// </summary>
public class InstructorIndex(CourseQueries db, GradeIndex grades)
{
    /// <summary>One instructor, flattened to what the search needs to rank them.</summary>
    public record Entry(string Unid, string Name, string? Department,
                        int SectionCount, int GradedCourses, double? AvgGpa);

    private volatile IReadOnlyList<Entry> _all = Shuffled(WithAverages(db.InstructorAverages(), grades));

    // The averages come from GradeIndex - pooled the same way the pages pool.
    private static IReadOnlyList<Entry> WithAverages(IReadOnlyList<Entry> entries, GradeIndex grades) =>
        entries.Select(e => e with { AvgGpa = grades.InstructorAverage(e.Unid) }).ToList();

    /// <summary>
    /// The default order is a weighted shuffle: random, with people who have
    /// between 3 and 8 graded classes drawn a little more readily than the
    /// rest. Nearly half of all instructors have no graded class at all, and
    /// a plain shuffle put a front page of empty cards in front of every
    /// visitor. Measured base rate 18.9%; weight 8 makes that group about
    /// two-thirds of page 1, a clear majority without being a wall of them -
    /// and because it samples without replacement the effect thins out by
    /// itself: later pages drift back toward plain random.
    ///
    /// Shuffled ONCE, here, not per request: paging walks a fixed sequence,
    /// so page 2 never repeats a person from page 1 or skips one. The deal
    /// changes on restart or <see cref="Rebuild"/>.
    /// </summary>
    private const double NudgeWeight = 8.0;

    private static IReadOnlyList<Entry> Shuffled(IReadOnlyList<Entry> entries)
    {
        // Efraimidis-Spirakis: a key of -ln(U)/w per item, sorted ascending, is
        // a weighted draw without replacement. Weight 1 is an ordinary shuffle.
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

    /// <summary>Re-reads the whole index. For after a fresh grade load.</summary>
    public void Rebuild() => _all = Shuffled(WithAverages(db.InstructorAverages(), grades));

    /// <summary>
    /// Everyone whose name contains the query, in the index's own order - the
    /// weighted shuffle above, the same shuffle for every page of it. There is
    /// no other order: a professor list sorted by GPA is a ranking, and that
    /// is not what this page is for.
    /// </summary>
    public (IReadOnlyList<InstructorCard> Cards, int Total) Search(
        string? query, int page = 1, int pageSize = 24)
    {
        var all = _all;   // one read of the field: a rebuild mid-search must
                          // not change the list under the paging arithmetic
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
