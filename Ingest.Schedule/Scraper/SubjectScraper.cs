using System.Text.RegularExpressions;
using HtmlAgilityPack;

/// <summary>The subject index: every subject's class-list query string.</summary>
class SubjectScraper
{
    /// <summary>
    /// The subject a class-list query names, as the database stores it:
    /// "subject=ME%20EN&amp;type=AOCE" is "ME EN". The index percent-encodes
    /// the space; a plus sign is not decoded, since the index never writes one.
    /// </summary>
    public static string Subject(string query)
    {
        foreach (var part in query.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part.Substring(0, eq).Equals("subject", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part.Substring(eq + 1));
        }
        return "";
    }

    /// <summary>The query part of each class_list link, such as "subject=CS".</summary>
    public static List<string> Scrape(HtmlDocument doc)
    {
        var aTags = doc.DocumentNode.SelectNodes("//a[contains(@href,'class_list.html?subject=')]");
        if (aTags == null)
        {
            Console.WriteLine($"WARNING: couldent find a tags in subject page");
            return new List<string>();
        }
        
        var subjects = new HashSet<string>();

        foreach (var aTag in aTags)
        {
            var href = aTag.GetAttributeValue("href", "");
            href = System.Net.WebUtility.HtmlDecode(href);
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var hrefParts = href.Split("?");
            if (hrefParts.Length > 2)
            {
                Console.WriteLine($"WARNING: couldent find query in {href})");
                new List<string>();
            }
            var query = hrefParts[1];

            subjects.Add(query);

        }

        return subjects.ToList();
    }

}
