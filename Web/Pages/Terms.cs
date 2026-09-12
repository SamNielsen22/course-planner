namespace Web.Pages;

/// <summary>
/// Terms are stored as "Fall2020", which sorts alphabetically rather than
/// chronologically - every Fall, then every Spring, then every Summer. Pages
/// order them here so dropdowns and trend charts read in real time order.
/// </summary>
public static class Terms
{
    private static readonly string[] SeasonOrder = ["Spring", "Summer", "Fall"];

    public static (int Year, int Season) Key(string term)
    {
        var year = int.TryParse(term[^4..], out var y) ? y : 0;
        var season = Array.IndexOf(SeasonOrder, term[..^4]);
        return (year, season < 0 ? SeasonOrder.Length : season);
    }

    /// <summary>Newest first - what a term picker should default to.</summary>
    public static IReadOnlyList<string> NewestFirst(IEnumerable<string> terms) =>
        terms.OrderByDescending(Key).ToList();

    /// <summary>"Fall2026" -> "Fall 2026".</summary>
    public static string Display(string term) =>
        term.Length > 4 ? $"{term[..^4]} {term[^4..]}" : term;
}

public static class Names
{
    /// <summary>
    /// "Kopta, Daniel" as "Daniel Kopta". The registrar stores sortable order;
    /// a page heading reads better the way a person says their own name.
    /// Anything that is not a single "Last, First" is left exactly as it came.
    /// </summary>
    public static string Natural(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "";
        var parts = stored.Split(',', 2);
        if (parts.Length != 2) return stored.Trim();
        var last = parts[0].Trim();
        var first = parts[1].Trim();
        return last.Length == 0 || first.Length == 0 ? stored.Trim() : $"{first} {last}";
    }

    /// <summary>
    /// "Kopta, Daniel" as "daniel-kopta": the readable part of a professor's
    /// URL, for people and search engines. The uNID beside it stays the key,
    /// so a misspelt or changed slug still lands on the right person.
    /// </summary>
    public static string Slug(string? stored)
    {
        var plain = Natural(stored).Normalize(System.Text.NormalizationForm.FormD);
        var ascii = new string(plain.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                                                != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        var slug = System.Text.RegularExpressions.Regex.Replace(ascii.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "professor" : slug;
    }

    /// <summary>"CS 2420" as "cs-2420", and "ME EN 2650" as "me-en-2650": a class in a professor's URL.</summary>
    public static string CourseSlug(string subject, string number) => $"{subject} {number}".ToLowerInvariant().Replace(' ', '-');
    public static string CourseSlug(string code) => code.ToLowerInvariant().Replace(' ', '-');

    /// <summary>"me-en-2650" back to ("ME EN", "2650"); null for anything that is not a class slug.</summary>
    public static (string Subject, string Number)? ParseCourseSlug(string? slug)
    {
        if (slug is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(slug, @"^([a-z][a-z-]*)-(\d{3,4}[a-z]?)$");
        return m.Success ? (m.Groups[1].Value.Replace('-', ' ').ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant()) : null;
    }

    /// <summary>"Kopta, Daniel" as "Kopta" - the label under a figure, where a whole name would not fit.</summary>
    public static string Family(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "";
        var comma = stored.IndexOf(',');
        return (comma > 0 ? stored[..comma] : stored).Trim();
    }

    /// <summary>Several people on one line: "A", "A and B", or "A and 3 more".</summary>
    public static string? Line(IReadOnlyList<string> names) => names.Count switch
    {
        0 => null,
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{names[0]} and {names.Count - 1} more",
    };
}

/// <summary>
/// The registrar's three class schedules, as the site names them. A schedule
/// is for one of them, chosen when it is created: main unless said otherwise,
/// since that is where nearly everything is. Main goes unlabelled; the other
/// two are called out so a section in Incheon or online is not mistaken for
/// one in Salt Lake.
/// </summary>
public static class Campus
{
    public const string Main = "main";

    /// <summary>Code and name, in the order a chooser lists them.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> All =
        [(Main, "Main Campus"), ("uac", "Asia Campus"), ("online", "UOnline")];

    /// <summary>A code the site knows, or main - never a typed-in string.</summary>
    public static string Known(string? code) => All.Any(c => c.Code == code) ? code! : Main;

    public static string Name(string? code) => All.First(c => c.Code == Known(code)).Name;

    /// <summary>The campus's name when it is not the main one, which goes unsaid.</summary>
    public static string? Label(string? code) => Known(code) == Main ? null : Name(code);
}
