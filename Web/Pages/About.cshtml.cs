using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Mvc;
using CoursePlanner.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

/// <summary>The About page. Its figures are counted from the database at startup.</summary>
[OutputCache(Duration = 60)]
[ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
public class AboutModel(SiteIndex site) : PageModel
{
    public int Courses { get; private set; }
    public int Sections { get; private set; }
    public int Instructors { get; private set; }
    public int GradedSections { get; private set; }
    public string? FirstGradeTerm { get; private set; }
    public string? LastGradeTerm { get; private set; }
    public string? LastScheduleTerm { get; private set; }

    public void OnGet()
    {
        var counts = site.Coverage;
        Courses = counts.Courses;
        Sections = counts.Sections;
        Instructors = counts.Instructors;
        GradedSections = counts.GradedSections;
        FirstGradeTerm = counts.FirstGradeTerm;
        LastGradeTerm = counts.LastGradeTerm;
        LastScheduleTerm = counts.LastScheduleTerm;
    }
}
