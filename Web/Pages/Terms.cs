namespace Web.Pages;

/// <summary>Terms are stored as "Fall2020". This puts them in date order.</summary>
public static class Terms
{
    private static readonly string[] SeasonOrder = ["Spring", "Summer", "Fall"];

    public static (int Year, int Season) Key(string term)
    {
        var year = int.TryParse(term[^4..], out var y) ? y : 0;
        var season = Array.IndexOf(SeasonOrder, term[..^4]);
        return (year, season < 0 ? SeasonOrder.Length : season);
    }

    /// <summary>Newest first.</summary>
    public static IReadOnlyList<string> NewestFirst(IEnumerable<string> terms) =>
        terms.OrderByDescending(Key).ToList();

    /// <summary>Whether a term is a summer term.</summary>
    public static bool IsSummer(string term) => term.StartsWith("Summer", StringComparison.Ordinal);

    /// <summary>"Fall2026" -> "Fall 2026".</summary>
    public static string Display(string term) =>
        term.Length > 4 ? $"{term[..^4]} {term[^4..]}" : term;
}

public static class Names
{
    /// <summary>"Kopta, Daniel" as "Daniel Kopta". Anything else is left as it came.</summary>
    public static string Natural(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "";
        var parts = stored.Split(',', 2);
        if (parts.Length != 2) return stored.Trim();
        var last = parts[0].Trim();
        var first = parts[1].Trim();
        return last.Length == 0 || first.Length == 0 ? stored.Trim() : $"{first} {last}";
    }

    /// <summary>"Kopta, Daniel" as "daniel-kopta", the readable part of a professor's URL. The uNID stays the key.</summary>
    public static string Slug(string? stored)
    {
        var plain = Natural(stored).Normalize(System.Text.NormalizationForm.FormD);
        var ascii = new string(plain.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                                                != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        var slug = System.Text.RegularExpressions.Regex.Replace(ascii.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "professor" : slug;
    }

    /// <summary>"ME EN 2650" as "me-en-2650".</summary>
    public static string CourseSlug(string subject, string number) => $"{subject} {number}".ToLowerInvariant().Replace(' ', '-');
    public static string CourseSlug(string code) => code.ToLowerInvariant().Replace(' ', '-');

    /// <summary>"me-en-2650" back to ("ME EN", "2650"), or null.</summary>
    public static (string Subject, string Number)? ParseCourseSlug(string? slug)
    {
        if (slug is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(slug, @"^([a-z][a-z-]*)-(\d{3,4}[a-z]?)$");
        return m.Success ? (m.Groups[1].Value.Replace('-', ' ').ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant()) : null;
    }

    /// <summary>"Kopta, Daniel" as "Kopta".</summary>
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

/// <summary>The registrar's three class schedules. Main goes unlabelled; the others are named.</summary>
public static class Campus
{
    public const string Main = "main";

    /// <summary>Every campus in the catalogue.</summary>
    private static readonly IReadOnlyList<(string Code, string Name)> Every =
        [(Main, "Main Campus"), ("uac", "Asia Campus"), ("online", "UOnline")];

    /// <summary>The campuses a chooser offers. UOnline is held back for now; its data stays.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> All =
        Every.Where(c => c.Code != "online").ToList();

    /// <summary>A known code, or main.</summary>
    public static string Known(string? code) => Every.Any(c => c.Code == code) ? code! : Main;

    public static string Name(string? code) => Every.First(c => c.Code == Known(code)).Name;

    /// <summary>The campus name, or null for main.</summary>
    public static string? Label(string? code) => Known(code) == Main ? null : Name(code);
}
