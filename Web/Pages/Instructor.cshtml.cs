using Microsoft.AspNetCore.OutputCaching;
using CoursePlanner.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

/// <summary>
/// One professor's grades, one section at a time. Term and class narrow the
/// sections; a picker chooses among them when there are several.
/// </summary>
[OutputCache(Duration = 60)]
[ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
public class InstructorModel(CourseQueries db, GradeIndex grades) : PageModel
{
    // The uNID identifies the person; names are not unique.
    [BindProperty(SupportsGet = true)] public string Unid { get; set; } = "";

    /// <summary>The term, or empty for the latest with grades.</summary>
    [BindProperty(SupportsGet = true)] public string? Term { get; set; }

    /// <summary>The term the visitor is planning, from the builder. A summer plan wants summer figures.</summary>
    [BindProperty(SupportsGet = true)] public string? Planning { get; set; }

    /// <summary>The class, as "CS 2420" or "cs-2420". Empty means the term's first.</summary>
    [BindProperty(SupportsGet = true)] public string? Course { get; set; }

    /// <summary>Whether a class was asked for. Then this is that class's own page.</summary>
    public bool ClassRequested { get; private set; }
    public string? ClassTitle { get; private set; }
    public double? ClassAverage { get; private set; }
    public int ClassSections { get; private set; }

    /// <summary>The section shown, as a key. Empty means the first.</summary>
    [BindProperty(SupportsGet = true, Name = "section")] public string? SectionKey { get; set; }

    /// <summary>One option in the section picker.</summary>
    public record Choice(string Key, string Label);

    public string Name { get; private set; } = "";
    public string Display => Names.Natural(Name);

    /// <summary>The professor's own address.</summary>
    public string Base => $"/professor/{Unid}/{Names.Slug(Name)}";

    /// <summary>The page's canonical address.</summary>
    public string Canonical => ClassRequested && Course is not null ? $"{Base}/{Names.CourseSlug(Course)}" : Base;

    public string PageTitle => ClassRequested && Course is not null ? $"{Display} {Course}" : Display;

    /// <summary>The average across every graded section.</summary>
    public double? Average { get; private set; }
    public int GradedSections { get; private set; }

    /// <summary>The search-result description.</summary>
    public string Description
    {
        get
        {
            if (ClassRequested && Course is not null)
            {
                var named = $"{Course}{(ClassTitle is null ? "" : $" ({ClassTitle})")}";
                var sections = $"{ClassSections} section{(ClassSections == 1 ? "" : "s")}";
                // A class with withheld counts has no pooled average.
                return ClassAverage is double inClass
                    ? $"{Display}'s grades in {named} at the University of Utah: average GPA {inClass:0.00} across {sections}."
                    : $"{Display}'s grades in {named} at the University of Utah: {sections} with published grades.";
            }
            return Average is double avg
                ? $"{Display}'s grades at the University of Utah: average GPA {avg:0.00} across {GradedSections:N0} class sections."
                : $"{Display} at the University of Utah: classes taught, with grades where the University has published them.";
        }
    }

    /// <summary>Terms with grades, newest first.</summary>
    public IReadOnlyList<string> Terms { get; private set; } = [];

    /// <summary>Classes with grades in the term in force.</summary>
    public IReadOnlyList<string> Classes { get; private set; } = [];

    /// <summary>The sections in play. The picker shows when there are several.</summary>
    public IReadOnlyList<Choice> Sections { get; private set; } = [];

    /// <summary>The section shown, as published.</summary>
    public GradeDistribution Grades { get; private set; } = GradeDistribution.Empty;

    public string RateMyProfessorUrl =>
        "https://www.ratemyprofessors.com/search/professors?q=" + Uri.EscapeDataString(Display);

    public void OnGet()
    {
        if (string.IsNullOrWhiteSpace(Unid)) return;
        Name = db.InstructorName(Unid) ?? Unid;
        // "cs-2420" in the address becomes "CS 2420".
        if (Names.ParseCourseSlug(Course) is { } slugged) Course = $"{slugged.Subject} {slugged.Number}";
        var requested = Course;

        var all = grades.InstructorSections(Unid);
        Average = grades.InstructorAverage(Unid);
        GradedSections = all.Count;
        Terms = Pages.Terms.NewestFirst(all.Select(r => r.Term).Distinct());
        static string Code(GradeIndex.Row r) => $"{r.Subject} {r.CourseNumber}";

        // Default term: the latest with grades for the class, skipping summer
        // for someone who mostly teaches fall and spring, unless planning a summer.
        if (Term is null || !Terms.Contains(Term))
        {
            var candidates = Course is null ? all : all.Where(r => Code(r) == Course).ToList();
            if (candidates.Count == 0) candidates = all;
            var newest = Pages.Terms.NewestFirst(candidates.Select(r => r.Term).Distinct());
            var usuallyNotSummer = all.Count(r => !Pages.Terms.IsSummer(r.Term)) > all.Count(r => Pages.Terms.IsSummer(r.Term));
            var planningSummer = Planning is not null && Pages.Terms.IsSummer(Planning);
            Term = (usuallyNotSummer && !planningSummer ? newest.FirstOrDefault(t => !Pages.Terms.IsSummer(t)) : null)
                   ?? newest.FirstOrDefault();
        }

        var inTerm = all.Where(r => r.Term == Term).ToList();
        Classes = inTerm.Select(Code).Distinct().OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        // Always one class at a time.
        if (Course is null || !Classes.Contains(Course)) Course = Classes.FirstOrDefault();
        ClassRequested = requested is not null && requested == Course;
        if (Course is not null)
        {
            var split = Course.LastIndexOf(' ');
            var (subject, number) = (Course[..split], Course[(split + 1)..]);
            ClassTitle = db.Course(subject, number)?.Title;
            ClassAverage = grades.InstructorCourseAverage(Unid, subject, number);
            ClassSections = grades.InstructorSections(Unid, subject: subject, courseNumber: number).Count;
        }

        var order = Terms.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
        var inPlay = inTerm.Where(r => Code(r) == Course)
            .OrderBy(r => order.GetValueOrDefault(r.Term, int.MaxValue))
            .ThenBy(r => r.Subject).ThenBy(r => r.CourseNumber).ThenBy(r => r.SectionNumber)
            .ToList();
        if (inPlay.Count == 0) return;

        Sections = inPlay.Select(r => new Choice(
            GradeIndex.Key(r.Term, r.Subject, r.CourseNumber, r.SectionNumber), $"Section {r.SectionNumber}")).ToList();

        var shown = inPlay.FirstOrDefault(r => GradeIndex.Key(r.Term, r.Subject, r.CourseNumber, r.SectionNumber) == SectionKey)
                    ?? inPlay[0];
        SectionKey = GradeIndex.Key(shown.Term, shown.Subject, shown.CourseNumber, shown.SectionNumber);
        Grades = GradeIndex.Pool([shown]);
    }
}
