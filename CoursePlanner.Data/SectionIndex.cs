using System.Collections.Concurrent;

namespace CoursePlanner.Data;

/// <summary>
/// A term's sections, in memory, so the builder does not reload the term on
/// every search. Each term is rebuilt once it is older than <see cref="MaxAge"/>,
/// since the seats pass keeps changing the seat counts.
/// </summary>
public class SectionIndex(CourseQueries db)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    private sealed record Entry(
        IReadOnlyList<Section> Sections,
        Dictionary<string, Section> ByKey,
        Dictionary<string, string?> Requirements,
        DateTime BuiltUtc);

    private readonly ConcurrentDictionary<string, Entry> _terms = new();
    private readonly ConcurrentDictionary<string, object> _gates = new();

    /// <summary>"Term|Subject|Number|Section", the key a schedule stores.</summary>
    public static string KeyOf(string term, string subject, string courseNumber, string sectionNumber) =>
        $"{term}|{subject}|{courseNumber}|{sectionNumber}";

    /// <summary>Every section in a term, in subject/number/section order.</summary>
    public IReadOnlyList<Section> All(string term) => Get(term).Sections;

    /// <summary>One section by its key, or null if the term has no such section.</summary>
    public Section? Find(string key)
    {
        var split = key.IndexOf('|');
        if (split < 0) return null;
        return Get(key[..split]).ByKey.GetValueOrDefault(key);
    }

    /// <summary>The builder's search. Each test mirrors a clause of <see cref="CourseQueries.FindSections"/>.</summary>
    public IReadOnlyList<Section> Find(
        string term,
        string? subject = null,
        string? courseNumber = null,
        string? query = null,
        string? requirement = null,
        bool openOnly = false,
        bool creditOnly = true,
        string? campus = null)
    {
        var entry = Get(term);
        var q = (query ?? "").Trim();
        var req = requirement ?? "";

        // "CS 2420" is read as subject then number.
        var parts = q.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var head = parts.Length == 2 ? parts[0] : "";
        var tail = parts.Length == 2 ? parts[1] : "";
        var roman = CourseQueries.RomanTitle(q);   // "calculus 1" also as "calculus I"

        const StringComparison Like = StringComparison.OrdinalIgnoreCase;

        return entry.Sections.Where(s =>
            (string.IsNullOrEmpty(campus) || s.Campus == campus)
            && (string.IsNullOrEmpty(subject) || s.Subject == subject)
            && (string.IsNullOrEmpty(courseNumber) || s.CourseNumber == courseNumber)
            && (q.Length == 0
                || s.Subject.Contains(q, Like)
                || s.CourseNumber.Contains(q, Like)
                || (s.Title?.Contains(q, Like) ?? false)
                || (roman is not null && (s.Title?.Contains(roman, Like) ?? false))
                || (s.Subject + " " + s.CourseNumber).Contains(q, Like)
                || (head.Length > 0 && s.Subject.StartsWith(head, Like)
                    && s.CourseNumber.StartsWith(tail, Like)))
            && (req.Length == 0
                || (entry.Requirements.GetValueOrDefault(s.Subject + "|" + s.CourseNumber)
                        ?.Contains(req, Like) ?? false))
            && (!openOnly || s.SeatsAvailable > 0)
            // Continuing Education: three digits and no credit. Null units count as none, as in SQL.
            // A course another course requires (e.g. MATH 980) is kept even so.
            && (!creditOnly
                || !(s.CourseNumber.Length == 3 && (s.Units is null || s.Units == 0))
                || db.PrereqCourses.Contains(s.Subject + "|" + s.CourseNumber)))
            .ToList();
    }

    /// <summary>Drops every cached term. The next read rebuilds.</summary>
    public void Clear() => _terms.Clear();

    private Entry Get(string term)
    {
        var have = _terms.GetValueOrDefault(term);
        if (have is not null && DateTime.UtcNow - have.BuiltUtc < MaxAge) return have;

        var gate = _gates.GetOrAdd(term, _ => new object());

        if (have is not null)
        {
            // Stale but present: one thread refreshes, the rest keep the old copy.
            if (Monitor.TryEnter(gate))
            {
                try
                {
                    if (ReferenceEquals(_terms.GetValueOrDefault(term), have))
                        _terms[term] = Build(term);
                }
                finally { Monitor.Exit(gate); }
            }
            return _terms[term];
        }

        // Nothing yet: build once; others wait.
        lock (gate)
        {
            if (!_terms.TryGetValue(term, out have))
            {
                have = Build(term);
                _terms[term] = have;
            }
            return have;
        }
    }

    private Entry Build(string term)
    {
        // The index holds everything; the credit filter is applied per search.
        var sections = db.FindSections(term, creditOnly: false);
        return new Entry(
            sections,
            sections.ToDictionary(s => KeyOf(s.Term, s.Subject, s.CourseNumber, s.SectionNumber)),
            db.RequirementDesignations(),
            DateTime.UtcNow);
    }
}
