/// <summary>
/// The registrar's four-digit term code and the term name the database stores,
/// each way. Fall 2026 is 1268: a leading 1 for the century, the two-digit
/// year, then 4 for spring, 6 for summer or 8 for fall.
/// </summary>
static class TermCodes
{
    public const int Spring = 4;
    public const int Summer = 6;
    public const int Fall = 8;

    /// <summary>2026 and Fall to "1268".</summary>
    public static string Code(int year, int season)
    {
        if (season is not (Spring or Summer or Fall))
            throw new ArgumentOutOfRangeException(nameof(season), season, "expected 4, 6 or 8");
        return $"1{year % 100:00}{season}";
    }

    /// <summary>"Fall2026" to "1268". Refuses anything that is not a term rather than guessing a season.</summary>
    public static string Parse(string term)
    {
        var season = term switch
        {
            _ when term.StartsWith("Spring") => Spring,
            _ when term.StartsWith("Summer") => Summer,
            _ when term.StartsWith("Fall") => Fall,
            _ => throw new ArgumentException($"unknown season in '{term}'", nameof(term))
        };
        var year = term[^4..];
        if (!int.TryParse(year, out var yearNumber))
            throw new ArgumentException($"no year in '{term}'", nameof(term));
        return Code(yearNumber, season);
    }

    /// <summary>"1268" to "Fall2026". Refuses anything that is not a term code rather than guessing a season.</summary>
    public static string Display(string code)
    {
        if (code.Length != 4 || !code.All(char.IsDigit) || code[0] != '1')
            throw new ArgumentException($"not a term code: '{code}'", nameof(code));
        var year = 2000 + int.Parse(code.Substring(1, 2));
        var season = code[3] switch
        {
            '4' => "Spring",
            '6' => "Summer",
            '8' => "Fall",
            _ => throw new ArgumentException($"not a term code: '{code}' has no season digit", nameof(code))
        };
        return $"{season}{year}";
    }
}
