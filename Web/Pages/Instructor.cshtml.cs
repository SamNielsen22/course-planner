using System.Text.Json;
using Microsoft.AspNetCore.OutputCaching;
using CoursePlanner.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

/// <summary>
/// One professor's grades, one section at a time. Term and class narrow the
/// sections; a picker chooses among them when there are several. Every graded
/// section is rendered into the page, so the pickers swap them in place and a
/// search engine reads the whole record at the person's one address.
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

    public string? ClassTitle { get; private set; }

    /// <summary>The section shown, as a key. Empty means the first.</summary>
    [BindProperty(SupportsGet = true, Name = "section")] public string? SectionKey { get; set; }

    /// <summary>One option in the section picker.</summary>
    public record Choice(string Key, string Label);

    /// <summary>One graded section, rendered whether or not it is the one showing.</summary>
    public record Block(string Key, GradeDistribution Grades);

    public string Name { get; private set; } = "";
    public string Display => Names.Natural(Name);

    /// <summary>The professor's own address, which is the page's canonical one.</summary>
    public string Base => $"/professor/{Unid}/{Names.Slug(Name)}";
    public string Canonical => Base;

    /// <summary>The average across every graded section.</summary>
    public double? Average { get; private set; }
    public int GradedSections { get; private set; }

    /// <summary>Every class, most taught first, for the title and description.</summary>
    public IReadOnlyList<string> Codes { get; private set; } = [];

    /// <summary>The name, then the classes, so a search for either finds the page.</summary>
    public string PageTitle
    {
        get
        {
            if (Codes.Count == 0) return Display;
            var named = string.Join(", ", Codes.Take(3)) + (Codes.Count > 3 ? $" and {Codes.Count - 3} more" : "");
            return $"{Display}: {named} grades";
        }
    }

    /// <summary>The search-result description.</summary>
    public string Description
    {
        get
        {
            if (Codes.Count == 0)
                return $"{Display} at the University of Utah: classes taught, with grades where the University has published them.";
            var named = string.Join(", ", Codes.Take(4)) + (Codes.Count > 4 ? $" and {Codes.Count - 4} more" : "");
            var sections = $"{GradedSections:N0} class section{(GradedSections == 1 ? "" : "s")}";
            return Average is double avg
                ? $"{Display}'s grades at the University of Utah: average GPA {avg:0.00} across {sections} of {named}."
                : $"{Display}'s grades at the University of Utah: {sections} of {named} with published grades.";
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

    /// <summary>Every graded section, in the page.</summary>
    public IReadOnlyList<Block> Blocks { get; private set; } = [];

    /// <summary>
    /// What the pickers need to swap sections without a reload: the terms newest
    /// first, each term's classes and their sections, the class titles, and the
    /// slugs a link may name a class by.
    /// </summary>
    public string DataJson { get; private set; } = "{}";

    public string RateMyProfessorUrl =>
        "https://www.ratemyprofessors.com/search/professors?q=" + Uri.EscapeDataString(Display);

    public IActionResult OnGet()
    {
        if (string.IsNullOrWhiteSpace(Unid)) return Page();
        Name = db.InstructorName(Unid) ?? Unid;

        // A class in the path, from when each had a page of its own: that page
        // is this one, opened on the class.
        if (RouteData.Values.TryGetValue("course", out var routed) && routed is string inPath && inPath.Length > 0)
        {
            var slug = Names.ParseCourseSlug(inPath) is { } p ? Names.CourseSlug(p.Subject, p.Number) : Names.CourseSlug(inPath);
            return RedirectPermanent($"{Base}#{slug}");
        }
        // "cs-2420" in the query becomes "CS 2420".
        if (Names.ParseCourseSlug(Course) is { } slugged) Course = $"{slugged.Subject} {slugged.Number}";

        var all = grades.InstructorSections(Unid);
        Average = grades.InstructorAverage(Unid);
        GradedSections = all.Count;
        Terms = Pages.Terms.NewestFirst(all.Select(r => r.Term).Distinct());
        static string Code(GradeIndex.Row r) => $"{r.Subject} {r.CourseNumber}";
        Codes = all.GroupBy(Code).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                   .Select(g => g.Key).ToList();

        // Default term: the latest with grades for the class, skipping summer
        // for someone who mostly teaches fall and spring, unless planning a summer.
        var usuallyNotSummer = all.Count(r => !Pages.Terms.IsSummer(r.Term)) > all.Count(r => Pages.Terms.IsSummer(r.Term));
        var planningSummer = Planning is not null && Pages.Terms.IsSummer(Planning);
        if (Term is null || !Terms.Contains(Term))
        {
            var candidates = Course is null ? all : all.Where(r => Code(r) == Course).ToList();
            if (candidates.Count == 0) candidates = all;
            var newest = Pages.Terms.NewestFirst(candidates.Select(r => r.Term).Distinct());
            Term = (usuallyNotSummer && !planningSummer ? newest.FirstOrDefault(t => !Pages.Terms.IsSummer(t)) : null)
                   ?? newest.FirstOrDefault();
        }

        var inTerm = all.Where(r => r.Term == Term).ToList();
        Classes = inTerm.Select(Code).Distinct().OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        // Always one class at a time.
        if (Course is null || !Classes.Contains(Course)) Course = Classes.FirstOrDefault();

        var order = Terms.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
        var ordered = all.OrderBy(r => order.GetValueOrDefault(r.Term, int.MaxValue))
            .ThenBy(r => r.Subject).ThenBy(r => r.CourseNumber).ThenBy(r => r.SectionNumber)
            .ToList();
        static string KeyOf(GradeIndex.Row r) => GradeIndex.Key(r.Term, r.Subject, r.CourseNumber, r.SectionNumber);

        Blocks = ordered.Select(r => new Block(KeyOf(r), GradeIndex.Pool([r]))).ToList();
        var titles = Codes.ToDictionary(c => c, c =>
        {
            var split = c.LastIndexOf(' ');
            return db.Course(c[..split], c[(split + 1)..])?.Title;
        });
        DataJson = JsonSerializer.Serialize(new
        {
            terms = Terms,
            preferNonSummer = usuallyNotSummer && !planningSummer,
            titles,
            slugs = Codes.ToDictionary(Names.CourseSlug, c => c),
            tree = Terms.ToDictionary(t => t, t => ordered.Where(r => r.Term == t).GroupBy(Code)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(r => new { key = KeyOf(r), label = $"Section {r.SectionNumber}" }).ToList())),
        });

        if (Course is not null) ClassTitle = titles.GetValueOrDefault(Course);
        var inPlay = ordered.Where(r => r.Term == Term && Code(r) == Course).ToList();
        if (inPlay.Count == 0) return Page();

        Sections = inPlay.Select(r => new Choice(KeyOf(r), $"Section {r.SectionNumber}")).ToList();
        var shown = inPlay.FirstOrDefault(r => KeyOf(r) == SectionKey) ?? inPlay[0];
        SectionKey = KeyOf(shown);
        Grades = GradeIndex.Pool([shown]);
        return Page();
    }
}
