using HtmlAgilityPack;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
record SectionRecord(
    string Term,
    string Subject,
    string CourseNumber,
    string SectionNumber,
    string? Title,
    List<string> Instructors,
    string? Component,
    string? Type,
    int? Units,
    string? Location,
    string? Times
);
class MainSearchScraper{
    public static HashSet<SectionRecord> Scrape(string url)
    {
        var doc = ScraperUtils.LoadFromUrl(url);
        var header = ScraperUtils.CleanText(
        doc.DocumentNode.SelectSingleNode("//h1").InnerText);
        var sectionCards = doc.DocumentNode.SelectNodes("//div[contains(@class,'class-info')]");

        if (sectionCards == null)
        {
            var branches = SubjectScraper.Scrape(url);

            if (branches.Count > 0)
            {
                var baseUrl = url.Split("subject=")[0];
                var combined = new HashSet<SectionRecord>();
                foreach (var branch in branches)
                {
                    var classListUrl = baseUrl + branch;
                    combined.UnionWith(Scrape(classListUrl));
                }

                return combined;
            }

            Console.WriteLine($"WARNING: Couldent find section cards");
            return new HashSet<SectionRecord>();
        }

        if (! TryParseHeader(header, out var semester, out var year))
        {
            Console.WriteLine($"WARNING: Couldent pase header {header}");
            return new HashSet<SectionRecord>();
        }
        var sections = new HashSet<SectionRecord>();

        foreach (var card in sectionCards)
        {
            var courseTitle = card.SelectSingleNode(".//h3");
            
            if (courseTitle == null)
            {
                Console.WriteLine($"WARNING: Couldent find h3 tags in card: {card.InnerHtml}:");
                continue;
            }
            var titleText = ScraperUtils.CleanText(courseTitle.InnerText);

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
            ParseSectionInfo(lis, out var instructors, out var component, out var type, out var units);
            var times = ParseDaysTimes(card);
            var location = ParseLocation(card);

            var id = $"{semester}{year}:{subject}:{courseNumber}:{section}";
            sections.Add(new SectionRecord(
                $"{semester}{year}", subject, courseNumber,
                   section, title, instructors, component,
                   type, units, location, times));
        }
        return sections;

    }

    static string GetFirstSpanText(HtmlNode li)
    {
        var span = li.SelectSingleNode(".//span");
        if (span == null)
            return string.Empty;

        return ScraperUtils.CleanText(span.InnerText);
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
    static void ParseSectionInfo(HtmlNodeCollection lis, out List<string> instructors, out string? component, out string? type, out int? units)
    {
        instructors = new List<string>();
        component = type = null;
        units = null;

        foreach (var li in lis)
        {
            var liText = ScraperUtils.CleanText(li.InnerText);
            var parts = liText.Split(':', 2);
            if (parts.Length < 2) continue;

            var label = parts[0];

            switch (label)
            {
                case "Instructor":
                    foreach (var a in li.SelectNodes(".//a") ?? Enumerable.Empty<HtmlNode>())
                    {
                        var name = ScraperUtils.CleanText(a.InnerText);
                        if (!string.IsNullOrWhiteSpace(name))
                            instructors.Add(name);       
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
            var dayText = ScraperUtils.CleanText(days[i].InnerText);
            var timeText = ScraperUtils.CleanText(times[i].InnerText);
            results.Add($"{dayText}/{timeText}");
        }

        return results.Count > 0 ? string.Join("; ", results) : null;
    }
    static string? ParseLocation(HtmlNode card)
    {
        var table = card.SelectSingleNode(".//table[contains(@class,'time-table')]");
        if (table == null)
            return null;

        var locations = table.SelectNodes(".//th[@data-building-code]//a");
        if (locations == null)
            return null;

        var results = new List<string>();

        foreach (var location in locations)
        {
            results.Add(ScraperUtils.CleanText(location.InnerText));
        }

        return results.Count > 0 ? string.Join(", ", results) : null;
    }


}



