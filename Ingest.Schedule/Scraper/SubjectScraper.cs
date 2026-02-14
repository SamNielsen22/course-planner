using System.Text.RegularExpressions;
using HtmlAgilityPack;

class SubjectScraper
{    
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
