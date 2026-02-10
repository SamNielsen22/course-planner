using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using HtmlAgilityPack;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Numerics;
record SectionRecord(
    string Term,
    string Subject,
    string CourseNumber,
    string SectionNumber,
    string Title,
    List<string> instructors,
    string component,
    string type,
    int units
);
class MainSearchScraper{
    static Dictionary<string, SectionRecord> Scrape(string url)
    {
        var doc = new HtmlDocument();
        doc.Load("samples/cs.html");
        using IDbConnection conn = new SqliteConnection("Data Source=data/courseplanner.db");
        conn.Open();
        conn.Execute("PRAGMA foreign_keys = ON;");

        var header = CleanText(
            doc.DocumentNode.SelectSingleNode("//h1").InnerText);

        var sectionCards = doc.DocumentNode.SelectNodes(
            "//div[contains(@class,'class-info')]");
        if (sectionCards == null)
        {
            Console.WriteLine($"WARNING: Couldent find section cards");
            return new Dictionary<string, SectionRecord>();
        }

        if (! TryParseHeader(header, out var semester, out var year))
        {
            Console.WriteLine($"WARNING: Couldent pase header {header}");
            return new Dictionary<string, SectionRecord>();
        }
        var sections = new Dictionary<string, SectionRecord>();

        foreach (var card in sectionCards)
        {
            var courseTitle = card.SelectSingleNode(".//h3");
            
            if (courseTitle == null){
                Console.WriteLine($"WARNING: Couldent find h3 tags in card: {card.InnerHtml}:");
                continue;
            }
            var titleText = CleanText(courseTitle.InnerText);

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
            if (! TryParseSectionInfo(lis, card.InnerHtml , out var instructors, out var component, out var type, out var units)){
                continue;
            }
            var id = $"{semester}{year}:{subject}:{courseNumber}:{section}";
            sections[id] = new SectionRecord($"{semester}{year}", subject, courseNumber, section, title, instructors, component, type, units);


        }
        return sections;

    }
    static string CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        return Regex
            .Replace(HtmlEntity.DeEntitize(text), @"\s+", " ")
            .Trim();
    }
    static string GetFirstSpanText(HtmlNode li)
    {
        var span = li.SelectSingleNode(".//span");
        if (span == null)
        {
            Console.WriteLine($"WARNING: Couldent extract span from {li.InnerText}");
            return string.Empty;
        }
        return CleanText(span.InnerText);
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

        var titleMatch = Regex.Match(h3, @"^(?<subj>[A-Z]+)\s+(?<num>\d+)\s*-\s*(?<sec>\S+)\s+(?<title>.+)$");
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
    static bool TryParseSectionInfo(HtmlNodeCollection lis ,string cardText, out List<string> instructors, out string component, out string type, out int units)
    {
        instructors = new List<string>();
        component = type = string.Empty;
        units = 0;
        var unitsMissing = false;

        foreach (var li in lis)
        {
            var liText = CleanText(li.InnerText);
            var parts = liText.Split(':', 2);
            if (parts.Length < 2) continue;

            var label = parts[0];

            switch (label)
            {
                case "Instructor":
                    foreach (var a in li.SelectNodes(".//a") ?? Enumerable.Empty<HtmlNode>())
                    {
                        if (string.IsNullOrWhiteSpace(a.InnerText))
                        {
                            instructors = new List<string>();
                            break;
                        }
                        instructors.Add(CleanText(a.InnerText));
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
                    if (unitText == "--") unitsMissing = true; 
                    else if (double.TryParse(unitText, out var doubleUnits))
                        units = (int)doubleUnits;
                    break;
            }
        }
        // Validation:
        if (instructors.Count == 0)
        {
            Console.WriteLine($"WARNING: Could not parse instructor for {cardText}");
            return false;
        }
        if (component == string.Empty)
        {
            Console.WriteLine($"WARNING: Could not parse component for {cardText}");
            return false;
        }
        if (type == string.Empty)
        {
            Console.WriteLine($"WARNING: Could not parse type for {cardText}");
            return false;
        }
        if (units == 0)
        {
            // Units may be "--" for labs or other non-credit sections
            if (unitsMissing) return true;

            Console.WriteLine($"WARNING: Could not parse units for {cardText}");
            return false;
        }

        return true;
    }

}



