namespace CoursePlanner.Data;

/// <summary>
/// Every published grade row - each section, each term of a course, and the
/// all-terms row where the scrape has produced it - with its reconstruction,
/// held in memory. Every grade figure on the site is read or pooled from
/// here, so a search tile, a builder card and a page can never disagree.
///
/// Built once at startup: <see cref="GradeReconstruction"/> runs for each row
/// then and never again; a request only sums. Pooled averages and deviations
/// are weighted by the reconstruction's inferred headcounts, which include
/// the students the registrar blanked - weighting by the visible counts put
/// the pooled mean off by more than 0.01 a third of the time.
///
/// Not watched. After a grade load, restart the site or call <see cref="Rebuild"/>.
/// </summary>
public class GradeIndex(CourseQueries db)
{
    /// <summary>
    /// One published row and its reconstruction. Bucket counts are the visible
    /// ones (blanked reads as 0, as the chart draws it); List is the rebuilt
    /// running total at each of the twelve marks; Headcount includes the
    /// inferred hidden students.
    /// </summary>
    public sealed record Row(
        string Term, string Subject, string CourseNumber, string? SectionNumber,
        double Avg, double? P25, double? P50, double? P75, double? Sd,
        int A, int B, int C, int D, int E, int Cr, int Nc, int W, int Other,
        double[] List, int Headcount);

    private sealed record Snapshot(
        Dictionary<string, Row> Sections,                  // term|subject|number|section
        Dictionary<string, Row> CourseTerms,               // term|subject|number
        Dictionary<string, List<Row>> TermsOfCourse,       // subject|number -> every term row
        Dictionary<string, Row> AllTermsOfCourse,          // subject|number -> the registrar's own pooled row
        Dictionary<string, List<Row>> SectionsOfInstructor,// unid -> every section they taught with grades
        Dictionary<string, double> CourseAverages,         // subject|number
        Dictionary<string, double> InstructorAverages,     // unid
        Dictionary<string, double> InstructorCourseAverages); // unid|subject|number

    private volatile Snapshot _now = Build(db);

    public static string Key(params string?[] parts) => string.Join("|", parts);

    private static Snapshot Build(CourseQueries db)
    {
        var sections = Reconstruct(db.GradeRows("section_grades"));
        var courseTerms = Reconstruct(db.GradeRows("course_term_grades"));
        var allTerms = Reconstruct(db.GradeRows("course_grades"));

        var sectionByKey = new Dictionary<string, Row>();
        foreach (var r in sections) sectionByKey[Key(r.Term, r.Subject, r.CourseNumber, r.SectionNumber)] = r;
        var courseTermByKey = new Dictionary<string, Row>();
        foreach (var r in courseTerms) courseTermByKey[Key(r.Term, r.Subject, r.CourseNumber)] = r;
        var termsOfCourse = courseTerms
            .GroupBy(r => Key(r.Subject, r.CourseNumber))
            .ToDictionary(g => g.Key, g => g.ToList());
        var allTermsOfCourse = new Dictionary<string, Row>();
        foreach (var r in allTerms) allTermsOfCourse[Key(r.Subject, r.CourseNumber)] = r;

        var ofInstructor = new Dictionary<string, List<Row>>();
        foreach (var link in db.SectionInstructorLinks())
        {
            if (!sectionByKey.TryGetValue(Key(link.Term, link.Subject, link.CourseNumber, link.SectionNumber), out var row)) continue;
            if (!ofInstructor.TryGetValue(link.Unid, out var list)) ofInstructor[link.Unid] = list = [];
            list.Add(row);
        }

        // The three averages the tiles and cards show, pooled once here so they
        // match what the pages compute from the same rows.
        var courseAverages = new Dictionary<string, double>();
        foreach (var (course, rows) in termsOfCourse)
        {
            var avg = allTermsOfCourse.TryGetValue(course, out var all) && all.A + all.B + all.C + all.Cr > 0
                ? all.Avg
                : Pool(rows).AvgGpa;
            if (avg is double a) courseAverages[course] = a;
        }
        var instructorAverages = new Dictionary<string, double>();
        var instructorCourseAverages = new Dictionary<string, double>();
        foreach (var (unid, rows) in ofInstructor)
        {
            if (Pool(rows).AvgGpa is double a) instructorAverages[unid] = a;
            foreach (var g in rows.GroupBy(r => Key(r.Subject, r.CourseNumber)))
                if (Pool(g.ToList()).AvgGpa is double b) instructorCourseAverages[Key(unid, g.Key)] = b;
        }

        return new Snapshot(sectionByKey, courseTermByKey, termsOfCourse, allTermsOfCourse, ofInstructor,
                            courseAverages, instructorAverages, instructorCourseAverages);
    }

    // The reconstruction is the only expensive step; the rows are independent.
    private static List<Row> Reconstruct(IReadOnlyList<CourseQueries.GradeRow> published) =>
        published.AsParallel().AsOrdered().Select(p =>
        {
            var fit = GradeReconstruction.Rebuild(new GradeReconstruction.Row(
                p.Avg, p.P25, p.P50, p.P75, p.Sd, p.A, p.B, p.C, p.D, p.E));
            return new Row(p.Term, p.Subject, p.CourseNumber, p.SectionNumber,
                           p.Avg, p.P25, p.P50, p.P75, p.Sd,
                           p.A ?? 0, p.B ?? 0, p.C ?? 0, p.D ?? 0, p.E ?? 0, p.Cr, p.Nc, p.W, p.Other,
                           fit?.List ?? new double[12], fit?.Headcount ?? 0);
        }).ToList();

    /// <summary>Re-reads and re-reconstructs everything. For after a grade load.</summary>
    public void Rebuild() => _now = Build(db);

    /// <summary>
    /// One course, for a single term or pooled across every term. The pooled
    /// view prefers the registrar's own all-terms row when the scrape has
    /// produced it - the only figure that sees through per-term suppression -
    /// and otherwise pools the term rows.
    /// </summary>
    public GradeDistribution Course(string subject, string courseNumber, string? term = null)
    {
        var s = _now;
        if (term is not null)
            return s.CourseTerms.TryGetValue(Key(term, subject, courseNumber), out var one) ? Pool([one]) : GradeDistribution.Empty;
        if (s.AllTermsOfCourse.TryGetValue(Key(subject, courseNumber), out var all) && all.A + all.B + all.C + all.Cr > 0)
            return Pool([all]);
        return s.TermsOfCourse.TryGetValue(Key(subject, courseNumber), out var rows) ? Pool(rows) : GradeDistribution.Empty;
    }

    /// <summary>One section's published grades - the finest grain, the room the student sat in.</summary>
    public GradeDistribution Section(string term, string subject, string courseNumber, string sectionNumber) =>
        _now.Sections.TryGetValue(Key(term, subject, courseNumber, sectionNumber), out var row) ? Pool([row]) : GradeDistribution.Empty;

    /// <summary>
    /// One instructor's grades, optionally narrowed to a term or a class. The
    /// class filter matters because a professor who teaches both a large
    /// first-year class and a graduate seminar has two quite different
    /// distributions, and pooling them describes neither.
    /// </summary>
    public GradeDistribution Instructor(string unid, string? term = null, string? subject = null, string? courseNumber = null)
    {
        if (!_now.SectionsOfInstructor.TryGetValue(unid, out var rows)) return GradeDistribution.Empty;
        return Pool(rows.Where(r =>
            (term is null || r.Term == term) &&
            (subject is null || r.Subject == subject) &&
            (courseNumber is null || r.CourseNumber == courseNumber)).ToList());
    }

    /// <summary>Every course's pooled average, keyed "SUBJ|NUMBER" - the builder's GPA sort.</summary>
    public IReadOnlyDictionary<string, double> CourseAverages => _now.CourseAverages;

    public double? CourseAverage(string subject, string courseNumber) =>
        _now.CourseAverages.TryGetValue(Key(subject, courseNumber), out var a) ? a : null;

    public double? InstructorAverage(string unid) =>
        _now.InstructorAverages.TryGetValue(unid, out var a) ? a : null;

    /// <summary>THIS professor's average in THIS course - the number the search tiles and builder cards show.</summary>
    public double? InstructorCourseAverage(string unid, string subject, string courseNumber) =>
        _now.InstructorCourseAverages.TryGetValue(Key(unid, subject, courseNumber), out var a) ? a : null;

    /// <summary>
    /// Several rows as one distribution. A single row is read as published.
    /// Several are pooled: buckets summed; the mean and deviation weighted by
    /// inferred headcount (the deviation by the pooled-variance identity); the
    /// percentiles read off the merged rebuilt lists, the dashboard's way.
    /// </summary>
    public static GradeDistribution Pool(IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0) return GradeDistribution.Empty;
        if (rows.Count == 1)
        {
            var r = rows[0];
            return new GradeDistribution(r.A, r.B, r.C, r.D, r.E, r.Cr, r.Nc, r.W, r.Other,
                                         r.Avg, r.P25, r.P50, r.P75, r.Sd);
        }

        var weighted = rows.Where(r => r.Headcount > 0).ToList();
        var n = weighted.Sum(r => (double)r.Headcount);
        double? avg = n > 0 ? weighted.Sum(r => r.Avg * r.Headcount) / n : null;

        double? sd = null;
        var withSd = weighted.Where(r => r.Sd is not null).ToList();
        if (withSd.Count > 0)
        {
            var m = withSd.Sum(r => (double)r.Headcount);
            var mean = withSd.Sum(r => r.Avg * r.Headcount) / m;
            var second = withSd.Sum(r => r.Headcount * (r.Sd!.Value * r.Sd.Value + r.Avg * r.Avg)) / m;
            sd = Math.Sqrt(Math.Max(0, second - mean * mean));
        }

        var list = new double[12]; var total = 0;
        foreach (var r in weighted)
        {
            for (var i = 0; i < 12; i++) list[i] += r.List[i];
            total += r.Headcount;
        }
        double? P(double q) => total > 0 ? GradeReconstruction.Percentile(list, total, q) : null;

        return new GradeDistribution(
            rows.Sum(r => r.A), rows.Sum(r => r.B), rows.Sum(r => r.C), rows.Sum(r => r.D), rows.Sum(r => r.E),
            rows.Sum(r => r.Cr), rows.Sum(r => r.Nc), rows.Sum(r => r.W), rows.Sum(r => r.Other),
            avg, P(.25), P(.5), P(.75), sd);
    }
}
