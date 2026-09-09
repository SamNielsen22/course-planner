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

    public static IReadOnlyList<string> Chronological(IEnumerable<string> terms) =>
        terms.OrderBy(Key).ToList();

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
}
