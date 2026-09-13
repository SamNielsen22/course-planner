using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using CoursePlanner.Data;

namespace Web.Pages;

/// <summary>
/// The order a blank search shows sections in.
///
/// It is a fixed order, the same on every start of the server. The one the
/// user chose to keep is written down in data/spotlight-order.txt, one
/// section key per line, first line first; sections named there come in
/// that order. Sections not named - another term, a section added since -
/// follow in a draw that is random but stable, hashed from the section's
/// key with a fixed seed rather than the process's, and weighted toward the
/// sections worth a look: one professor, grades on file, and that professor
/// grading the course at least 0.1 away from the course's own average.
///
/// The first card is held to more: it is the example of what a card can
/// show, so it must have prerequisites that name courses, a course average
/// and its professor's average, and a meeting time. The earliest section in
/// the order that has all three is moved to the front; nothing else moves.
/// </summary>
public sealed partial class Spotlight
{
    private readonly GradeIndex _grades;
    private readonly CourseQueries _db;
    private readonly Dictionary<string, int> _kept;

    public Spotlight(GradeIndex grades, CourseQueries db, IConfiguration config, IWebHostEnvironment env)
    {
        _grades = grades;
        _db = db;
        _kept = Load(config["Spotlight:Order"] is { Length: > 0 } relative ? Path.Combine(env.ContentRootPath, relative) : null);
    }

    /// <summary>The kept order: line number by section key. Empty when there is no file.</summary>
    private static Dictionary<string, int> Load(string? path)
    {
        var kept = new Dictionary<string, int>();
        if (path is null || !File.Exists(path)) return kept;
        var line = 0;
        foreach (var raw in File.ReadLines(path))
        {
            var key = raw.Trim();
            if (key.Length == 0 || kept.ContainsKey(key)) continue;
            kept[key] = line++;
        }
        return kept;
    }

    /// <summary>The order, with the first fully furnished section brought to the front.</summary>
    public List<Section> Order(IEnumerable<Section> sections)
    {
        var drawn = sections.OrderByDescending(Key).ToList();
        var lead = drawn.FindIndex(IsFullExample);
        if (lead > 0)
        {
            var first = drawn[lead];
            drawn.RemoveAt(lead);
            drawn.Insert(0, first);
        }
        return drawn;
    }

    /// <summary>
    /// Higher comes first. A kept section's key is above 1, earlier lines
    /// higher; anything else is a weighted draw without replacement below 1,
    /// u^(1/w) per section.
    /// </summary>
    public double Key(Section s)
    {
        var key = $"{s.Term}|{s.Subject}|{s.CourseNumber}|{s.SectionNumber}";
        if (_kept.TryGetValue(key, out var line)) return 2.0 + (_kept.Count - line);
        var u = StableHash(key) / (double)uint.MaxValue;
        return Math.Pow(Math.Clamp(u, 1e-9, 1 - 1e-9), 1.0 / Weight(s));
    }

    /// <summary>FNV-1a over the key: the same number on every run, unlike HashCode, which reseeds per process.</summary>
    private static uint StableHash(string key)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes("spotlight:" + key))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }

    private double Weight(Section s)
    {
        if (s.Instructors.Count != 1) return 1;
        var course = _grades.CourseAverage(s.Subject, s.CourseNumber);
        var theirs = _grades.InstructorCourseAverage(s.Instructors[0].Unid, s.Subject, s.CourseNumber);
        return course is double c && theirs is double t && Math.Abs(t - c) >= 0.1 ? 80 : 1;
    }

    /// <summary>A meeting time, both grade figures, and prerequisites that name at least one course.</summary>
    private bool IsFullExample(Section s) =>
        s.Times is { } times && times.Contains('/')
        && s.Instructors.Count == 1
        && _grades.CourseAverage(s.Subject, s.CourseNumber) is not null
        && _grades.InstructorCourseAverage(s.Instructors[0].Unid, s.Subject, s.CourseNumber) is not null
        && NamesPrerequisiteCourses(s.Subject, s.CourseNumber);

    // Prerequisites are read from the database and remembered per course, so
    // the search for a first card costs a handful of lookups once, not per visit.
    private readonly ConcurrentDictionary<string, bool> _prerequisites = new();

    private bool NamesPrerequisiteCourses(string subject, string courseNumber) =>
        _prerequisites.GetOrAdd(subject + "|" + courseNumber,
            _ => CourseCode().IsMatch(_db.Course(subject, courseNumber)?.Prerequisites ?? ""));

    /// <summary>A course code inside prerequisite prose: "CS 1410", "MATH 1210", "CH EN 2300".</summary>
    [GeneratedRegex(@"\b[A-Z]{2,5}(?: [A-Z]{1,2})? \d{3,4}[A-Z]?\b")]
    private static partial Regex CourseCode();
}
