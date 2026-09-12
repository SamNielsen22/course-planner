using Microsoft.AspNetCore.OutputCaching;
using CoursePlanner.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

/// <summary>
/// One instructor: how they grade, always at the section grain - the one
/// grain where every figure shown is the registrar's own. The chart is one
/// section's. Term (defaulting to their latest with grades) and class narrow
/// which sections are in play; when more than one is, a section picker
/// chooses among them, and the first is shown until it does.
/// </summary>
[OutputCache(Duration = 60)]
[ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
public class InstructorModel(CourseQueries db, GradeIndex grades) : PageModel
{
    // The uNID identifies the person; two instructors can share a name, so the
    // page cannot be addressed by one.
    [BindProperty(SupportsGet = true)] public string Unid { get; set; } = "";

    /// <summary>A term; empty for the latest one with grades. After OnGet, the term in force.</summary>
    [BindProperty(SupportsGet = true)] public string? Term { get; set; }

    /// <summary>
    /// "CS 2420" as picked from the class dropdown, or "cs-2420" from the
    /// address; empty means the first class of the term. After OnGet, the
    /// class in force.
    /// </summary>
    [BindProperty(SupportsGet = true)] public string? Course { get; set; }

    /// <summary>
    /// Whether a class was asked for. Then the page is that class's own, with
    /// its own address - the page a search for "professor plus course" lands on.
    /// </summary>
    public bool ClassRequested { get; private set; }
    public string? ClassTitle { get; private set; }
    public double? ClassAverage { get; private set; }
    public int ClassSections { get; private set; }

    /// <summary>The section shown, as "Term|Subject|Number|Section"; empty is the first in play.</summary>
    [BindProperty(SupportsGet = true, Name = "section")] public string? SectionKey { get; set; }

    /// <summary>One section the picker can choose: its key and how to name it.</summary>
    public record Choice(string Key, string Label);

    public string Name { get; private set; } = "";
    public string Display => Names.Natural(Name);

    /// <summary>The person's own address, which the pickers browse from.</summary>
    public string Base => $"/professor/{Unid}/{Names.Slug(Name)}";

    /// <summary>The person's address, or the class's own when one was asked for.</summary>
    public string Canonical => ClassRequested && Course is not null ? $"{Base}/{Names.CourseSlug(Course)}" : Base;

    public string PageTitle => ClassRequested && Course is not null
        ? $"{Display} – {Course} – University of Utah grades"
        : $"{Display} – University of Utah grades";

    /// <summary>Their average across every graded section, for the page's description.</summary>
    public double? Average { get; private set; }
    public int GradedSections { get; private set; }

    /// <summary>What a search result says about the page.</summary>
    public string Description
    {
        get
        {
            if (ClassRequested && Course is not null)
            {
                var named = $"{Course}{(ClassTitle is null ? "" : $" ({ClassTitle})")}";
                var sections = $"{ClassSections} section{(ClassSections == 1 ? "" : "s")}";
                // A class whose letter counts the University withheld has no
                // poolable average; the page still shows each section's own.
                return ClassAverage is double inClass
                    ? $"How {Display} grades {named} at the University of Utah: average GPA {inClass:0.00} across {sections}, by term and section."
                    : $"How {Display} grades {named} at the University of Utah: {sections} with published grades, by term and section.";
            }
            return Average is double avg
                ? $"How {Display} grades at the University of Utah: average GPA {avg:0.00} across {GradedSections:N0} class sections with published grades, by term and section."
                : $"{Display} at the University of Utah: classes taught, with grade distributions where the University has published them.";
        }
    }

    /// <summary>Terms this professor has published grades in, newest first.</summary>
    public IReadOnlyList<string> Terms { get; private set; } = [];

    /// <summary>Classes with grades in the term in force.</summary>
    public IReadOnlyList<string> Classes { get; private set; } = [];

    /// <summary>The sections in play. The picker is drawn only when there is more than one.</summary>
    public IReadOnlyList<Choice> Sections { get; private set; } = [];

    /// <summary>The section on show - its published figures, never pooled.</summary>
    public GradeDistribution Grades { get; private set; } = GradeDistribution.Empty;

    public string RateMyProfessorUrl =>
        "https://www.ratemyprofessors.com/search/professors?q=" + Uri.EscapeDataString(Display);

    public void OnGet()
    {
        if (string.IsNullOrWhiteSpace(Unid)) return;
        Name = db.InstructorName(Unid) ?? Unid;
        // The address form of a class, "cs-2420", reads as the dropdown's "CS 2420".
        if (Names.ParseCourseSlug(Course) is { } slugged) Course = $"{slugged.Subject} {slugged.Number}";
        var requested = Course;

        var all = grades.InstructorSections(Unid);
        Average = grades.InstructorAverage(Unid);
        GradedSections = all.Count;
        Terms = Pages.Terms.NewestFirst(all.Select(r => r.Term).Distinct());
        static string Code(GradeIndex.Row r) => $"{r.Subject} {r.CourseNumber}";

        // The term: as asked, or the latest one - for the class asked for, if
        // a search tile named one, so the tile lands where that class has grades.
        if (Term is null || !Terms.Contains(Term))
        {
            var candidates = Course is null ? all : all.Where(r => Code(r) == Course).ToList();
            if (candidates.Count == 0) candidates = all;
            Term = Pages.Terms.NewestFirst(candidates.Select(r => r.Term).Distinct()).FirstOrDefault();
        }

        var inTerm = all.Where(r => r.Term == Term).ToList();
        Classes = inTerm.Select(Code).Distinct().OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        // Always one class: a mix of classes has no single honest chart.
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
