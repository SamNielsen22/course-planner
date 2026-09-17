using HtmlAgilityPack;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
/// <summary>One instructor as the schedule lists them. The uNID, from the profile link, is the identity; the name is only a spelling.</summary>
record InstructorRef(string? Unid, string Name);

record SectionRecord(
    string Term,
    string Subject,
    string CourseNumber,
    string SectionNumber,
    string? Title,
    List<InstructorRef> Instructors,
    string? Component,
    string? Type,
    int? Units,
    string? Location,
    string? Times,
    string? Description, 
    string? Prerequisites,
    int? SeatsAvailable,
    // Whether the section can be waited on at all - the list's "Wait List:
    // Yes/No". How many are waiting is on the sections table, not here.
    bool? HasWaitlist = null,
    // For a companion section (lab, discussion, field work), the lecture it
    // registers you into, when the registrar's note says so. Null on lectures,
    // and on companions whose lecture never published one.
    string? PairsWith = null
);
class MainSearchScraper{
    // profiles.faculty.utah.edu/u0171400  and  faculty.utah.edu/u0171400/teaching
    static readonly Regex UnidPattern = new(@"faculty\.utah\.edu/(u\d+)", RegexOptions.Compiled);

    public static HashSet<SectionRecord> Scrape(HtmlDocument doc)
    {
        var header = HtmlUtils.CleanText(
        doc.DocumentNode.SelectSingleNode("//h1").InnerText);
        if (! TryParseHeader(header, out var semester, out var year))
        {
            Console.WriteLine($"WARNING: Couldent pase header {header}");
            return new HashSet<SectionRecord>();
        }

        var sectionCards = doc.DocumentNode.SelectNodes("//div[contains(@class,'class-info')]");
        if (sectionCards == null)
        {
            Console.WriteLine($"WARNING: Couldent find section cards");
            return new HashSet<SectionRecord>();
        }


        var sections = new HashSet<SectionRecord>();
        // Lecture cards carry the note that names their companion sections. Kept
        // aside and resolved after the walk, since those may be listed before or after.
        var lectureNotes = new Dictionary<(string, string, string), string>();

        foreach (var card in sectionCards)
        {
            var courseTitle = card.SelectSingleNode(".//h3");
            
            if (courseTitle == null)
            {
                Console.WriteLine($"WARNING: Couldent find h3 tags in card: {card.InnerHtml}:");
                continue;
            }
            var titleText = HtmlUtils.CleanText(courseTitle.InnerText);

            if (! TryParseSectionTitle(titleText, out var subject, out var courseNumber, out var section, out var title))
            {
                Console.WriteLine($"WARNING: couldent parse {titleText}");
                continue;
            }
            
            var lis = card.SelectNodes(".//div[contains(@class,'card-body')]//ul//li");
            if (lis == null)
            {
                Console.WriteLine($"WARNING: couldent find li tags in {card.InnerHtml}");
                continue;
            }
            ParseSectionInfo(lis, out var instructors, out var component, out var type, out var units, out var seats, out var hasWaitlist);
            var times = ParseDaysTimes(card);
            var location = ParseLocation(card);
            
            sections.Add(new SectionRecord(
                $"{semester}{year}", subject, courseNumber,
                   section, title, instructors, component,
                   type, units, location, times, null, null, seats, hasWaitlist)); // Description and prerequisites are in the details page. Records have to be updated later

            if (component == "Lecture")
                lectureNotes[(subject, courseNumber, section)] = HtmlUtils.CleanText(card.InnerText);
        }

        return PairCompanions(sections, lectureNotes);

    }

    /// <summary>
    /// Stamp each companion section with the lecture whose note names it. One named by two
    /// lectures is left unpaired: the page contradicts itself and no guess is
    /// better than none.
    /// </summary>
    static HashSet<SectionRecord> PairCompanions(HashSet<SectionRecord> sections,
                                           Dictionary<(string, string, string), string> lectureNotes)
    {
        var owner = new Dictionary<(string, string, string), string?>();
        foreach (var ((subject, course, lecture), note) in lectureNotes)
            foreach (var companion in CompanionNotes.Owned(note) ?? new List<string>())
            {
                var key = (subject, course, companion);
                owner[key] = owner.TryGetValue(key, out var other) && other != lecture ? null : lecture;
            }

        if (owner.Count == 0) return sections;

        var paired = new HashSet<SectionRecord>();
        foreach (var s in sections)
        {
            // The four components that ever accompany a lecture inside one course
            // (every term since 2020). Each registers you into the lecture the same
            // way, and the notes name them the same way. Anything else is left alone.
            var isCompanion = s.Component is "Laboratory" or "Lab/ Discussion" or "Discussion" or "Field Work";
            paired.Add(isCompanion && owner.TryGetValue((s.Subject, s.CourseNumber, s.SectionNumber), out var lecture) && lecture is not null
                ? s with { PairsWith = lecture }
                : s);
        }
        return paired;
    }

    static string GetFirstSpanText(HtmlNode li)
    {
        var span = li.SelectSingleNode(".//span");
        if (span == null)
            return string.Empty;

        return HtmlUtils.CleanText(span.InnerText);
    }

    static bool TryParseHeader(string header, out string? semester, out string? year)
    {
        semester = year = null;

        var termMatch = Regex.Match(header, @"\b(Spring|Summer|Fall)\s+(\d{4})\b");
        if (!termMatch.Success)
        {
            Console.WriteLine($"WARNING: Failed to parse header {header}");
            return false;
        }

        semester = termMatch.Groups[1].Value;
        year = termMatch.Groups[2].Value;
        return true;

    }

    static bool TryParseSectionTitle(string h3, out string subject, out string courseNumber, out string section, out string title)
    {

        var titleMatch = Regex.Match(h3, @"^(?<subj>[A-Z ]+)\s+(?<num>\d+)\s*-\s*(?<sec>\S+)\s+(?<title>.+)$");
        if (!titleMatch.Success)
        {
            Console.WriteLine($"WARNING: Failed to parse section title {h3}");
            subject = courseNumber = section = title = "";
            return false;
        }
        

        subject = titleMatch.Groups["subj"].Value;
        courseNumber = titleMatch.Groups["num"].Value;
        section = titleMatch.Groups["sec"].Value;
        title = titleMatch.Groups["title"].Value;
        return true;
    }
    static void ParseSectionInfo(HtmlNodeCollection lis, out List<InstructorRef> instructors, out string? component, out string? type, out int? units, out int? seatsAvailable, out bool? hasWaitlist)
    {
        instructors = new List<InstructorRef>();
        component = type = null;
        units = null;
        seatsAvailable = null;
        hasWaitlist = null;

        foreach (var li in lis)
        {
            var liText = HtmlUtils.CleanText(li.InnerText);
            var parts = liText.Split(':', 2);
            if (parts.Length < 2) continue;

            var label = parts[0];

            switch (label)
            {
                case "Instructor":
                    // The page repeats each instructor per breakpoint, so dedupe on the uNID.
                    foreach (var a in li.SelectNodes(".//a") ?? Enumerable.Empty<HtmlNode>())
                    {
                        var name = HtmlUtils.CleanText(a.InnerText);
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        var match = UnidPattern.Match(a.GetAttributeValue("href", ""));
                        var unid = match.Success ? match.Groups[1].Value : null;

                        var key = unid ?? name;
                        if (!instructors.Any(i => (i.Unid ?? i.Name) == key))
                            instructors.Add(new InstructorRef(unid, name));
                    }
                    break;

                case "Component":
                    component = GetFirstSpanText(li);
                    break;

                case "Type":
                    type = GetFirstSpanText(li);
                    break;

                case "Units":
                    var unitText = GetFirstSpanText(li);
                    if (double.TryParse(unitText, out var doubleUnits))
                        units = (int)doubleUnits;
                    break;

                // The card is rendered twice, once per breakpoint, so this label
                // appears twice with the same value - the second write is a no-op.
                case "Seats Available":
                    if (int.TryParse(GetFirstSpanText(li), out var openSeats))
                        seatsAvailable = openSeats;
                    break;

                case "Wait List":
                    var flag = GetFirstSpanText(li);
                    if (flag.Equals("Yes", StringComparison.OrdinalIgnoreCase)) hasWaitlist = true;
                    else if (flag.Equals("No", StringComparison.OrdinalIgnoreCase)) hasWaitlist = false;
                    break;
            }
        }
    }
    static string? ParseDaysTimes(HtmlNode card)
    {
        var table = card.SelectSingleNode(".//table[contains(@class,'time-table')]");
        if (table == null)
            return null;

        var days = table.SelectNodes(".//span[@data-day]");
        var times = table.SelectNodes(".//span[@data-time]");

        if (days == null || times == null)
            return null;

        var results = new List<string>();

        for (int i = 0; i < Math.Min(days.Count, times.Count); i++)
        {
            var dayText = HtmlUtils.CleanText(days[i].InnerText);
            var timeText = HtmlUtils.CleanText(times[i].InnerText);
            results.Add($"{dayText}/{timeText}");
        }

        return results.Count > 0 ? string.Join("; ", results) : null;
    }
    static string? ParseLocation(HtmlNode card)
    {
        var table = card.SelectSingleNode(".//table[contains(@class,'time-table')]");
        if (table == null)
            return null;

        // Salt Lake rooms are map links, the Asia Campus's plain text; the cell's text is the room either way.
        var cells = table.SelectNodes(".//th[@data-building-code]");
        if (cells == null)
            return null;

        var results = new List<string>();

        foreach (var cell in cells)
        {
            var text = HtmlUtils.CleanText(cell.InnerText);
            if (text.Length > 0) results.Add(text);
        }

        return results.Count > 0 ? string.Join(", ", results) : null;
    }


}



