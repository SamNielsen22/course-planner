using System.Text.RegularExpressions;

/// <summary>
/// Which companion section (lab, discussion, field work) belongs to which
/// lecture, from the note the
/// registrar prints on the lecture's own card. Registration is by the lab, and
/// the lab drags its lecture in with it, so the pairing decides what a student
/// can actually take. Discussion sections work the same way and the notes
/// name them the same way.
///
/// Only the lecture side is parsed. Labs almost never name their lecture, and
/// the enrollment counts are not used at all: they are zero on a term that has
/// just opened, which is exactly when someone is planning.
///
/// Anything that does not match one of the two phrasings below is left
/// unpaired rather than guessed at. Checked against 48 class-list pages: every
/// number the parser read named a lab or discussion section, never a lecture,
/// and on 164 of 170 lectures with counts the named sections' enrollment
/// summed to the lecture's exactly (the other six are notes missing a section).
/// </summary>
static class CompanionNotes
{
    // The registrar's template sentence, "Sections 14, 15, and 19 belong to
    // this lecture" (sometimes "this section"), carries 668 of the 691 notes
    // seen. ME EN alone writes "correspond to", and that one line is kept
    // because it decides a real four-lecture course. Three rarer phrasings
    // were dropped: on four terms of data they changed no course's answer,
    // since the courses using them have one lecture or stay unresolved anyway.
    // Everything after the number list is ignored.
    static readonly Regex[] Owns =
    {
        new(@"sections?\s+(?<list>[0-9][0-9,\s\-and]*?)\s+belong(?:s)?\s+to\s+this",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"lab(?:oratory)?\s+sections?\s+(?<list>[0-9][0-9,\s\-and]*?)\s+correspond\s+to\s+this",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <summary>
    /// The companion sections a lecture's note claims, or null if it claims
    /// none. Every matching sentence counts, so a note that lists its labs in
    /// two sentences is read whole rather than half.
    /// </summary>
    public static List<string>? Owned(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;

        var sections = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var pattern in Owns)
            foreach (Match match in pattern.Matches(note))
                sections.UnionWith(Expand(match.Groups["list"].Value));

        return sections.Count > 0 ? sections.ToList() : null;
    }

    /// <summary>"16, 17, 40, and 41", "31-32, 51-52" and "2 - 3" all to 3-digit numbers.</summary>
    static List<string> Expand(string list)
    {
        var sections = new List<string>();

        foreach (var piece in Regex.Split(list, @",|\band\b", RegexOptions.IgnoreCase))
        {
            var part = piece.Trim();
            if (part.Length == 0) continue;

            var range = Regex.Match(part, @"^(\d+)\s*-\s*(\d+)$");
            if (range.Success)
            {
                var from = int.Parse(range.Groups[1].Value);
                var to = int.Parse(range.Groups[2].Value);
                // A backwards or absurd range is a parse we do not understand.
                if (to < from || to - from > 60) return new List<string>();
                for (var n = from; n <= to; n++) sections.Add($"{n:D3}");
            }
            else if (Regex.IsMatch(part, @"^\d+$"))
                sections.Add($"{int.Parse(part):D3}");
            else
                return new List<string>();   // an unrecognised token: take none of it
        }

        return sections;
    }
}
