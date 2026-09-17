using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using CoursePlanner.Data;

namespace Web.Pages;

/// <summary>
/// The order a blank search shows sections in. Fixed across restarts: keys
/// listed in data/spotlight-order.txt come first, in file order; the rest
/// follow in a stable weighted draw. The first card must have prerequisites,
/// both grade figures and a meeting time.
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

    /// <summary>The kept order: line number by key. Empty when there is no file.</summary>
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

    /// <summary>The order, with the first full card brought to the front.</summary>
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

    /// <summary>Higher comes first. Kept keys score above 1 by line; the rest draw below 1.</summary>
    public double Key(Section s)
    {
        var key = $"{s.Term}|{s.Subject}|{s.CourseNumber}|{s.SectionNumber}";
        if (_kept.TryGetValue(key, out var line)) return 2.0 + (_kept.Count - line);
        var u = StableHash(key) / (double)uint.MaxValue;
        return Math.Pow(Math.Clamp(u, 1e-9, 1 - 1e-9), 1.0 / Weight(s));
    }

    /// <summary>FNV-1a: the same number on every run, unlike HashCode.</summary>
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

    /// <summary>Has a meeting time, both grade figures, and prerequisites naming a course.</summary>
    private bool IsFullExample(Section s) =>
        s.Times is { } times && times.Contains('/')
        && s.Instructors.Count == 1
        && _grades.CourseAverage(s.Subject, s.CourseNumber) is not null
        && _grades.InstructorCourseAverage(s.Instructors[0].Unid, s.Subject, s.CourseNumber) is not null
        && NamesPrerequisiteCourses(s.Subject, s.CourseNumber);

    // Prerequisites are read once per course and remembered.
    private readonly ConcurrentDictionary<string, bool> _prerequisites = new();

    private bool NamesPrerequisiteCourses(string subject, string courseNumber) =>
        _prerequisites.GetOrAdd(subject + "|" + courseNumber,
            _ => CourseCode().IsMatch(_db.Course(subject, courseNumber)?.Prerequisites ?? ""));

    /// <summary>A course code inside prerequisite text.</summary>
    [GeneratedRegex(@"\b[A-Z]{2,5}(?: [A-Z]{1,2})? \d{3,4}[A-Z]?\b")]
    private static partial Regex CourseCode();
}
