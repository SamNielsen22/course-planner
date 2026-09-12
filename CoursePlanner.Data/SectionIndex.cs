using System.Collections.Concurrent;

namespace CoursePlanner.Data;

/// <summary>
/// A term's sections, held in memory and searched there.
///
/// The builder was the slowest page on the site by a wide margin - 60% of all
/// capacity for two of every eleven requests - because each load pulled the
/// whole term out of the database (7,500 sections, 8,700 instructor rows),
/// built the objects, sorted the lot, and showed twenty-four. The same query
/// restricted to what the page shows takes 0.1ms; the cost was the
/// materialisation, and this stops repeating it.
///
/// Filtering and sorting cannot simply move into SQL, because two of the
/// builder's features live in C#: the time sorts parse "TuTh/09:10AM-10:30AM",
/// and "no time conflicts" reads the visitor's own schedule. So the term is
/// built once and those run over cached objects instead.
///
/// It is NOT built once and forgotten, unlike <see cref="SiteIndex"/>: the
/// seat crawl rewrites seats_available every ten minutes, and "open seats
/// only" is a filter on exactly that column. Each term is rebuilt when it is
/// older than <see cref="MaxAge"/> - about 40ms once a minute, which is
/// nothing - and readers keep the old copy while one thread refreshes.
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

    /// <summary>"Term|Subject|Number|Section" - the same shape the schedule uses.</summary>
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

    /// <summary>
    /// The builder's search, with the same meaning as
    /// <see cref="CourseQueries.FindSections"/> - each test here mirrors one
    /// clause of that WHERE, LIKE included, so the two cannot disagree on what
    /// a query matches.
    /// </summary>
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

        // "CS 2420" is read as subject then number, as the SQL does.
        var parts = q.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var head = parts.Length == 2 ? parts[0] : "";
        var tail = parts.Length == 2 ? parts[1] : "";

        const StringComparison Like = StringComparison.OrdinalIgnoreCase;

        return entry.Sections.Where(s =>
            (string.IsNullOrEmpty(campus) || s.Campus == campus)
            && (string.IsNullOrEmpty(subject) || s.Subject == subject)
            && (string.IsNullOrEmpty(courseNumber) || s.CourseNumber == courseNumber)
            && (q.Length == 0
                || s.Subject.Contains(q, Like)
                || s.CourseNumber.Contains(q, Like)
                || (s.Title?.Contains(q, Like) ?? false)
                || (s.Subject + " " + s.CourseNumber).Contains(q, Like)
                || (head.Length > 0 && s.Subject.StartsWith(head, Like)
                    && s.CourseNumber.StartsWith(tail, Like)))
            && (req.Length == 0
                || (entry.Requirements.GetValueOrDefault(s.Subject + "|" + s.CourseNumber)
                        ?.Contains(req, Like) ?? false))
            && (!openOnly || s.SeatsAvailable > 0)
            // Continuing Education: three-digit number and no credit. NULL units
            // fail the SQL's NOT(...) the same way zero does, so both are
            // excluded here too - the test must read exactly as the query's.
            && (!creditOnly
                || !(s.CourseNumber.Length == 3 && (s.Units is null || s.Units == 0))))
            .ToList();
    }

    /// <summary>Throws away every cached term. The next read rebuilds.</summary>
    public void Clear() => _terms.Clear();

    private Entry Get(string term)
    {
        var have = _terms.GetValueOrDefault(term);
        if (have is not null && DateTime.UtcNow - have.BuiltUtc < MaxAge) return have;

        var gate = _gates.GetOrAdd(term, _ => new object());

        if (have is not null)
        {
            // Stale but present. One thread refreshes; everyone else keeps
            // serving the copy they have rather than queueing behind it.
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

        // Nothing yet: build once, and anyone arriving meanwhile waits for it.
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
        // creditOnly: false - the index holds everything, and the credit test
        // is applied per search like every other filter.
        var sections = db.FindSections(term, creditOnly: false);
        return new Entry(
            sections,
            sections.ToDictionary(s => KeyOf(s.Term, s.Subject, s.CourseNumber, s.SectionNumber)),
            db.RequirementDesignations(),
            DateTime.UtcNow);
    }
}
