using System.Text.RegularExpressions;

namespace Ingest.Gpa;

/// <summary>The scraper's settings.</summary>
public static class Config
{
    public static readonly TimeSpan SecondsBetweenLoads = TimeSpan.FromSeconds(1.5);

    // One session per worker. Six is where the host starts slowing down.
    public static int Workers = 6;

    public const string Host = "https://tableau.dashboard.utah.edu";
    public const string DashboardUrl = Host + "/t/UAIR/views/OfficialUUGradeSummary_17192658137620/GradeSummary";
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    // Relative to the repo root. The progress file must move with the output
    // file, or a run skips everything it already marks.
    public static string OutputFile = "data/gpa4.csv";
    public static string ProgressFile = "data/done4.txt";
    public static string LogFile = "data/scrape4.log";

    public const long LogMaxBytes = 250_000_000;
    public const int LogBackups = 2;

    public const int LoadAttempts = 3;
    public const int SecondsBetweenRetries = 5;

    // The dashboard goes quiet for minutes at a time and always comes back,
    // so a session that cannot be opened waits, doubling the pause.
    public const int OutageFirstWait = 30;
    public const int OutageMaxWait = 600;

    // Sessions are retired often, to keep the interning dictionary small.
    public const int SubjectsPerSession = 1;

    public const string DashboardName = "Grade Summary";
    public const string HeadcountSheet = "Grade Tabs";
    public const string GpaSheet = "Avg GPA";

    public static readonly IReadOnlyDictionary<string, string> FilterLabels = new Dictionary<string, string>
    {
        ["term"] = "Term/Snap",
        ["subject"] = "Subject",
        ["course"] = "Catnbr",
        ["section"] = "Section",
    };

    /// <summary>Widest to narrowest. Selecting one clears everything to its right.</summary>
    public static readonly string[] FilterOrder = ["term", "subject", "course", "section"];

    // A term pin renders nothing, so the subject list comes from a real
    // selection instead. MATH is offered in every term.
    public const string BootstrapSubject = "MATH - Mathematics";

    public static readonly IReadOnlyDictionary<string, string> StatColumns = new Dictionary<string, string>
    {
        ["Average GPA"] = "avg_gpa",
        ["25th Percentile"] = "p25",
        ["50th Percentile"] = "p50",
        ["75th Percentile"] = "p75",
        ["Standard Deviation"] = "std_dev",
    };

    // Grade Tabs reports one row per grade group, headcount in CNT(Emplid Proxy).
    public static readonly IReadOnlyDictionary<string, string> GradeColumns = new Dictionary<string, string>
    {
        ["A"] = "grade_a", ["B"] = "grade_b", ["C"] = "grade_c", ["D"] = "grade_d", ["E"] = "grade_e",
        ["CR"] = "grade_cr", ["NC"] = "grade_nc", ["W"] = "grade_w", ["OTHER"] = "grade_other",
    };

    // Healthy runs of empty responses top out at 3. A false trip costs one session.
    public const int WedgedAfterEmpty = 6;

    /// <summary>Only letter grades carry grade points.</summary>
    public static readonly string[] GpaBearing = ["grade_a", "grade_b", "grade_c", "grade_d", "grade_e"];

    /// <summary>Every statistic is published rounded to two decimals; allow back the half-unit the rounding can add.</summary>
    public const double DisplayRounding = 0.005;

    public const double GradeSpan = 4.0;
    /// <summary>The n=2 ceiling of the sample deviation, 2.83 - the most forgiving there is.</summary>
    public static readonly double MostForgivingStdDev = GradeSpan / 2 * Math.Sqrt(2);

    /// <summary>Section value for the whole-course row. Parenthesised so it cannot collide with a real section.</summary>
    public const string CourseSection = "(all)";
    /// <summary>Term value for the all-terms pass, swept as if it were one more term.</summary>
    public const string AllTerms = "(all)";

    /// <summary>The grade columns come last so rows written before they existed still line up.</summary>
    public static readonly string[] CsvColumns =
    [
        "term", "subject", "catnbr", "section",
        "avg_gpa", "p25", "p50", "p75", "std_dev",
        "grade_a", "grade_b", "grade_c", "grade_d", "grade_e",
        "grade_cr", "grade_nc", "grade_w", "grade_other",
    ];

    /// <summary>https://host/t/SITE/views/WORKBOOK/SHEET -> /vizql/t/SITE/w/WORKBOOK/v/SHEET</summary>
    public static string VizqlRoot(string url)
    {
        var m = Regex.Match(url, @"https://[^/]+/t/([^/]+)/views/([^/]+)/([^/?]+)");
        if (!m.Success) throw new ArgumentException($"not a Tableau view url: {url}");
        return $"/vizql/t/{m.Groups[1].Value}/w/{m.Groups[2].Value}/v/{m.Groups[3].Value}";
    }

    public static readonly string VizqlRootPath = VizqlRoot(DashboardUrl);
}
