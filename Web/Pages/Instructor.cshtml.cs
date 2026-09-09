using Microsoft.AspNetCore.OutputCaching;
using CoursePlanner.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

/// <summary>One instructor: how they grade.</summary>
[OutputCache(Duration = 60)]
[ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
public class InstructorModel(CourseQueries db, SectionIndex sections, GradeIndex grades) : PageModel
{
    // The uNID identifies the person; two instructors can share a name, so the
    // page cannot be addressed by one.
    [BindProperty(SupportsGet = true)] public string Unid { get; set; } = "";

    /// <summary>Narrow the chart and boxes to one term; empty is every term.</summary>
    [BindProperty(SupportsGet = true)] public string? Term { get; set; }

    /// <summary>"CS 2420" as picked from the class dropdown; empty is every class.</summary>
    [BindProperty(SupportsGet = true)] public string? Course { get; set; }

    /// <summary>Within a term and class, one section - the room. Empty is all of them.</summary>
    [BindProperty(SupportsGet = true, Name = "section")] public string? SectionNumber { get; set; }

    /// <summary>
    /// Where the visitor came from. "builder" hides the section picker: it is a
    /// professor-search feature, and the builder's cards already name a section.
    /// </summary>
    [BindProperty(SupportsGet = true)] public string? From { get; set; }

    public bool ShowSections => From != "builder";

    public string Name { get; private set; } = "";
    public IReadOnlyList<string> Terms { get; private set; } = [];
    public IReadOnlyList<InstructorCourse> Courses { get; private set; } = [];

    /// <summary>This professor's graded sections of the chosen class in the chosen term; empty until both are chosen.</summary>
    public IReadOnlyList<Section> GradedSections { get; private set; } = [];
    public Section? Picked { get; private set; }
    public bool ClassChosen => Term is not null && Course is not null;

    /// <summary>The selection: every published section, or the term and class chosen.</summary>
    public GradeDistribution Grades { get; private set; } = GradeDistribution.Empty;

    public string Display => Names.Natural(Name);

    public string RateMyProfessorUrl =>
        "https://www.ratemyprofessors.com/search/professors?q=" + Uri.EscapeDataString(Display);

    public void OnGet()
    {
        if (string.IsNullOrWhiteSpace(Unid)) return;

        Name = db.InstructorName(Unid) ?? Unid;
        Terms = Pages.Terms.NewestFirst(db.InstructorTerms(Unid));
        // Which classes count as graded, and their averages, come from GradeIndex
        // like every other figure - so the dropdown and the chart agree even for a
        // class whose every section is too small to show letter counts.
        Courses = db.InstructorCourses(Unid)
            .Select(c => c with { AvgGpa = grades.InstructorCourseAverage(Unid, c.Subject, c.CourseNumber) })
            .ToList();
        if (!Terms.Contains(Term)) Term = null;

        // The dropdown carries "SUBJ NUMBER" as one value, so the two halves are
        // split back apart here rather than passed as two query parameters.
        string? subject = null, number = null;
        var chosen = Courses.FirstOrDefault(c => c.AvgGpa is not null && $"{c.Subject} {c.CourseNumber}" == Course);
        if (chosen is not null) (subject, number) = (chosen.Subject, chosen.CourseNumber);
        else Course = null;

        if (ShowSections && Term is not null && subject is not null)
        {
            GradedSections = sections.Find(Term, subject: subject, courseNumber: number, creditOnly: false)
                .Where(s => s.GpaAvg is not null && s.Instructors.Any(i => i.Unid == Unid))
                .ToList();
            Picked = GradedSections.FirstOrDefault(s => s.SectionNumber == SectionNumber);
        }

        // One section is the dashboard's own figures; anything wider is pooled.
        Grades = Picked is null
            ? grades.Instructor(Unid, Term, subject, number)
            : grades.Section(Term!, subject!, number!, Picked.SectionNumber);
    }
}
