using HtmlAgilityPack;

class SubjectScraper
{
    public static List<string> Scrape(HtmlDocument doc)
    {

        var nodes = doc.DocumentNode.SelectNodes("//a[contains(@href,'class_list.html?subject=')]");
        if (nodes == null) return new List<string>();
        var subjects = new HashSet<string>();

        foreach (var aTag in nodes)
        {
            var href = aTag.GetAttributeValue("href", "");
            var parts = href.Split("subject=");
            if (parts.Length < 2) 
            {
                Console.WriteLine($"WARNING: Couldent find not find \"subject=\" in href {href}");
                continue; 
            }

            var subject = parts[1];
            subjects.Add(subject);
        }

        return subjects.ToList();
    }
}
