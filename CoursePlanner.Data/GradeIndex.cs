namespace CoursePlanner.Data;

/// <summary>
/// Every published grade row, in memory. All grade figures on the site come
/// from here. Nothing is inferred: only the average is ever pooled, weighted
/// by headcount. Built at startup; call <see cref="Rebuild"/> after a grade load.
/// </summary>
public class GradeIndex(CourseQueries db)
{
    /// <summary>One published row. A blanked count (under five students) reads as 0.</summary>
    public sealed record Row(
        string Term, string Subject, string CourseNumber, string? SectionNumber,
        double Avg, double? P25, double? P50, double? P75, double? Sd,
        int A, int B, int C, int D, int E, int Cr, int Nc, int W, int Other)
    {
        /// <summary>Students with a letter grade, the weight for pooling.</summary>
        public int Graded => A + B + C + D + E;
    }

    private sealed record Snapshot(
        Dictionary<string, Row> Sections,                  // term|subject|number|section
        Dictionary<string, Row> CourseTerms,               // term|subject|number
        Dictionary<string, List<Row>> TermsOfCourse,       // subject|number -> term rows
        Dictionary<string, Row> AllTermsOfCourse,          // subject|number -> the registrar's pooled row
        Dictionary<string, List<Row>> SectionsOfInstructor,// unid -> graded sections taught
        Dictionary<string, double> CourseAverages,         // subject|number
        Dictionary<string, double> InstructorAverages,     // unid
        Dictionary<string, double> InstructorCourseAverages); // unid|subject|number

    private volatile Snapshot _now = Build(db);

    public static string Key(params string?[] parts) => string.Join("|", parts);

    private static Snapshot Build(CourseQueries db)
    {
        var sections = Rows(db.GradeRows("section_grades"));
        var courseTerms = Rows(db.GradeRows("course_term_grades"));
        var allTerms = Rows(db.GradeRows("course_grades"));

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

        // Averages for tiles and cards, pooled once here.
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

    private static List<Row> Rows(IReadOnlyList<CourseQueries.GradeRow> published) =>
        published.Select(p => new Row(
            p.Term, p.Subject, p.CourseNumber, p.SectionNumber,
            p.Avg, p.P25, p.P50, p.P75, p.Sd,
            p.A ?? 0, p.B ?? 0, p.C ?? 0, p.D ?? 0, p.E ?? 0, p.Cr, p.Nc, p.W, p.Other)).ToList();

    /// <summary>Re-reads everything, for after a grade load.</summary>
    public void Rebuild() => _now = Build(db);

    /// <summary>One course, for one term or across all. All-terms uses the registrar's own pooled row when there is one.</summary>
    public GradeDistribution Course(string subject, string courseNumber, string? term = null)
    {
        var s = _now;
        if (term is not null)
            return s.CourseTerms.TryGetValue(Key(term, subject, courseNumber), out var one) ? Pool([one]) : GradeDistribution.Empty;
        if (s.AllTermsOfCourse.TryGetValue(Key(subject, courseNumber), out var all) && all.A + all.B + all.C + all.Cr > 0)
            return Pool([all]);
        return s.TermsOfCourse.TryGetValue(Key(subject, courseNumber), out var rows) ? Pool(rows) : GradeDistribution.Empty;
    }

    /// <summary>One section's published grades.</summary>
    public GradeDistribution Section(string term, string subject, string courseNumber, string sectionNumber) =>
        _now.Sections.TryGetValue(Key(term, subject, courseNumber, sectionNumber), out var row) ? Pool([row]) : GradeDistribution.Empty;

    /// <summary>One instructor's graded sections, optionally narrowed to a term or a class.</summary>
    public IReadOnlyList<Row> InstructorSections(string unid, string? term = null, string? subject = null, string? courseNumber = null)
    {
        if (!_now.SectionsOfInstructor.TryGetValue(unid, out var rows)) return [];
        return rows.Where(r =>
            (term is null || r.Term == term) &&
            (subject is null || r.Subject == subject) &&
            (courseNumber is null || r.CourseNumber == courseNumber)).ToList();
    }

    /// <summary>Every course's pooled average, keyed "SUBJ|NUMBER".</summary>
    public IReadOnlyDictionary<string, double> CourseAverages => _now.CourseAverages;

    public double? CourseAverage(string subject, string courseNumber) =>
        _now.CourseAverages.TryGetValue(Key(subject, courseNumber), out var a) ? a : null;

    public double? InstructorAverage(string unid) =>
        _now.InstructorAverages.TryGetValue(unid, out var a) ? a : null;

    /// <summary>Every professor-and-course pair with grades, for the sitemap.</summary>
    public IEnumerable<(string Unid, string Subject, string CourseNumber)> GradedClasses =>
        _now.SectionsOfInstructor.SelectMany(kv => kv.Value.Select(r => (kv.Key, r.Subject, r.CourseNumber))).Distinct();

    /// <summary>This professor's average in this course.</summary>
    public double? InstructorCourseAverage(string unid, string subject, string courseNumber) =>
        _now.InstructorCourseAverages.TryGetValue(Key(unid, subject, courseNumber), out var a) ? a : null;

    /// <summary>Rows as one distribution. One row is read as published; several are summed, with a headcount-weighted average and no percentiles.</summary>
    public static GradeDistribution Pool(IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0) return GradeDistribution.Empty;
        if (rows.Count == 1)
        {
            var r = rows[0];
            return new GradeDistribution(r.A, r.B, r.C, r.D, r.E, r.Cr, r.Nc, r.W, r.Other,
                                         r.Avg, r.P25, r.P50, r.P75, r.Sd);
        }
        var graded = rows.Sum(r => (double)r.Graded);
        double? avg = graded > 0 ? rows.Sum(r => r.Avg * r.Graded) / graded : null;
        return new GradeDistribution(
            rows.Sum(r => r.A), rows.Sum(r => r.B), rows.Sum(r => r.C), rows.Sum(r => r.D), rows.Sum(r => r.E),
            rows.Sum(r => r.Cr), rows.Sum(r => r.Nc), rows.Sum(r => r.W), rows.Sum(r => r.Other),
            avg);
    }
}
