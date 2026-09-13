using Microsoft.AspNetCore.OutputCaching;
using CoursePlanner.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

[OutputCache(Duration = 60)]
[ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
public class CourseModel(CourseQueries db, SectionIndex sections, GradeIndex grades) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Subject { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Number { get; set; } = "";

    /// <summary>Which term the chart covers. Empty means every term pooled.</summary>
    [BindProperty(SupportsGet = true)] public string? Term { get; set; }

    /// <summary>Within a term, one section - the room the student sat in. Empty is the whole term.</summary>
    [BindProperty(SupportsGet = true, Name = "section")] public string? SectionNumber { get; set; }

    /// <summary>
    /// Where the visitor came from. "builder" hides the section picker: the
    /// builder is for planning a term that has not been graded yet, so a
    /// section-level distribution is not a question that page can raise.
    /// </summary>
    [BindProperty(SupportsGet = true)] public string? From { get; set; }

    public bool ShowSections => From != "builder";

    public IReadOnlyList<string> Terms { get; private set; } = [];

    /// <summary>Sections of the chosen term with published grades; empty until a term is chosen.</summary>
    public IReadOnlyList<Section> GradedSections { get; private set; } = [];
    public Section? Picked { get; private set; }

    public CourseInfo? Info { get; private set; }
    public GradeDistribution Grades { get; private set; } = GradeDistribution.Empty;

    public string? Title => Info?.Title;

    /// <summary>The one address for this course, whatever term or section the page is showing.</summary>
    public string Canonical => $"/course/{Uri.EscapeDataString(Subject)}/{Uri.EscapeDataString(Number)}";

    /// <summary>What a search result says about the page: the course, and its figures across every term.</summary>
    public string Description
    {
        get
        {
            var name = Title is null ? $"{Subject} {Number}" : $"{Subject} {Number} ({Title})";
            var overall = grades.Course(Subject, Number);
            return overall.AvgGpa is double avg
                ? $"{name} at the University of Utah: average GPA {avg:0.00} across {overall.Total:N0} students, with grades by term and section."
                : $"{name} at the University of Utah: description, prerequisites and sections.";
        }
    }

    public void OnGet()
    {
        Info = db.Course(Subject, Number);
        Terms = Pages.Terms.NewestFirst(db.CourseTerms(Subject, Number));
        // An unrecognised term falls back to the pooled view rather than an
        // empty chart, so a stale link still shows something true.
        if (!Terms.Contains(Term)) Term = null;

        if (Term is not null)
        {
            GradedSections = sections.Find(Term, subject: Subject, courseNumber: Number, creditOnly: false)
                .Where(s => s.GpaAvg is not null)
                .ToList();
            Picked = GradedSections.FirstOrDefault(s => s.SectionNumber == SectionNumber);
        }

        Grades = Picked is null
            ? grades.Course(Subject, Number, Term)
            : grades.Section(Term!, Subject, Number, Picked.SectionNumber);
    }

    public static string Professor(Section s) =>
        Names.Line(s.Instructors.Select(i => Names.Natural(i.Name)).ToList()) ?? "Staff";
}
