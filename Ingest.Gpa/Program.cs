using Ingest.Gpa;

// The GPA scraper: the University's Tableau grade dashboard into a csv the
// `grades` command loads. Run from the repo root.
//
//   dotnet run --project Ingest.Gpa -- [--terms "..."] [--subjects "..."]
//        [--output csv] [--progress txt] [--log jsonl] [--workers 6]
//
// Terms and subjects are the dashboard's own option strings.

static string? Arg(string[] args, string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
static List<string>? Many(string? value) =>
    value is null ? null : value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("usage: Ingest.Gpa [--terms A|B] [--subjects A|B] [--output csv] [--progress txt] [--log jsonl] [--workers n]");
    return 0;
}

Config.OutputFile = Arg(args, "--output") ?? Config.OutputFile;
Config.ProgressFile = Arg(args, "--progress") ?? Config.ProgressFile;
Config.LogFile = Arg(args, "--log") ?? Config.LogFile;
if (int.TryParse(Arg(args, "--workers"), out var workers) && workers > 0) Config.Workers = workers;

if (Config.OutputFile.EndsWith("gpa2.csv", StringComparison.OrdinalIgnoreCase) || Config.OutputFile.EndsWith("gpa3.csv", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"refusing to write to {Config.OutputFile}: that file is a finished, verified sweep");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Config.OutputFile))!);
return Sweep.Run(Many(Arg(args, "--terms")), Many(Arg(args, "--subjects")));
