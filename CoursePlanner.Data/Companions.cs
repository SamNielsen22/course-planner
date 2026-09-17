namespace CoursePlanner.Data;

/// <summary>One section of a course, as the pairing needs to see it.</summary>
public readonly record struct CourseSection(string SectionNumber, string? Component, string? PairsWith);

/// <summary>
/// Which companion section goes with which lecture. A companion's pairs_with is
/// what the registrar published, or what was entered by hand. The only thing
/// derived here is the case that needs no data: a course with exactly one
/// lecture, where a companion can only register you into that one.
///
/// Nothing is inferred from enrollment counts, section numbering or meeting
/// times. Where the registrar published nothing and the course has several
/// lectures, the companion stays unpaired and the page says so.
///
/// Every answer is computed from a course's WHOLE section list, never from the
/// sections that happen to be on screen. A search result is one page of one
/// filter, so deriving from it would make a lecture's companion list change
/// with the page number.
/// </summary>
public static class Companions
{
    public const string Any = "*";

    /// <summary>
    /// A section that registers you into a lecture. These four are the only
    /// components that share a course with a lecture in any term since 2020,
    /// and on every one-lecture course with counts the lecture's enrollment is
    /// exactly the sum of theirs. A Seminar or Studio beside a lecture is rare
    /// and unverified, so it is not paired.
    /// </summary>
    public static bool IsCompanion(string? component) => component is "Laboratory" or "Lab/ Discussion" or "Discussion" or "Field Work";

    /// <summary>The word for a companion on a card: "lab", "discussion", "field work", or "lab or discussion" when a course mixes them.</summary>
    public static string? Kind(IEnumerable<string?> companionComponents)
    {
        var kinds = new SortedSet<string>();
        foreach (var c in companionComponents)
            kinds.Add(c switch
            {
                "Laboratory" => "lab",
                "Discussion" => "discussion",
                "Field Work" => "field work",
                "Lab/ Discussion" => "lab or discussion",
                _ => "section",
            });
        if (kinds.Count == 0) return null;
        if (kinds.Count == 1) return kinds.First();
        // A course listing both, e.g. "lab or discussion".
        return string.Join(" or ", kinds).Replace("lab or discussion or", "lab, discussion or");
    }

    /// <summary>
    /// The companion sections a student may choose for a given lecture, by
    /// section number. When the registrar published the pairing, only that
    /// lecture's companions; when it published nothing, every companion of the
    /// course, since the student must be allowed to pick one somehow.
    /// </summary>
    public static IReadOnlyList<string> ChoicesFor(IEnumerable<CourseSection> courseSections, string lecture)
    {
        var resolved = Resolve(courseSections);
        var mine = resolved.Companions.GetValueOrDefault(lecture);
        if (mine is { Count: > 0 }) return mine;

        // No published companion for this lecture: offer the unpaired ones, or
        // if none are paired at all, every companion of the course.
        var all = courseSections.Where(s => IsCompanion(s.Component)).Select(s => s.SectionNumber);
        var unpaired = all.Where(s => resolved.PairsWith.GetValueOrDefault(s) is null).ToList();
        return (unpaired.Count > 0 ? unpaired : all.ToList())
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// What one course's sections mean for pairing: the lecture each companion
    /// feeds, and the companions each lecture takes. HasLecture is false for a
    /// course whose lab IS the course, where there is no lecture to pair with
    /// and so nothing to say.
    /// </summary>
    public sealed record Course(bool HasLecture,
                                IReadOnlyDictionary<string, string?> PairsWith,
                                IReadOnlyDictionary<string, IReadOnlyList<string>> Companions);

    /// <summary>
    /// Resolve one course from its complete section list. <paramref name="all"/>
    /// must be every section of that course in that term and campus.
    /// </summary>
    public static Course Resolve(IEnumerable<CourseSection> all)
    {
        var sections = all.ToList();
        var lectures = sections.Where(s => s.Component == "Lecture").Select(s => s.SectionNumber).ToList();
        var companions = sections.Where(s => IsCompanion(s.Component)).ToList();

        // With one lecture there is nothing to publish: a companion can only feed that one.
        var onlyLecture = lectures.Count == 1 ? lectures[0] : null;

        var pairsWith = new Dictionary<string, string?>();
        var feeds = new Dictionary<string, List<string>>();
        var anyLecture = new List<string>();

        foreach (var companion in companions)
        {
            var lecture = companion.PairsWith ?? onlyLecture;
            // A stored pairing can outlive its lecture when sections are
            // renumbered. Never point a student at a lecture that is not there.
            if (lecture is not null && lecture != Any && !lectures.Contains(lecture)) lecture = null;
            pairsWith[companion.SectionNumber] = lecture;

            if (lecture == Any) anyLecture.Add(companion.SectionNumber);
            else if (lecture is not null)
            {
                if (!feeds.TryGetValue(lecture, out var list)) feeds[lecture] = list = new();
                list.Add(companion.SectionNumber);
            }
        }

        var byLecture = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var lecture in lectures)
        {
            // A companion that any lecture accepts belongs on every lecture's list.
            var mine = new List<string>(feeds.GetValueOrDefault(lecture, new()));
            mine.AddRange(anyLecture);
            mine.Sort(StringComparer.Ordinal);
            byLecture[lecture] = mine;
        }

        return new Course(lectures.Count > 0, pairsWith, byLecture);
    }
}
