using HtmlAgilityPack;

class SubjectScraper
{
    public static List<string> ScrapeSubjects(string url)
    {
        using var http = new HttpClient();
        var html = http.GetStringAsync(url).Result;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var subjectNodes = doc.DocumentNode
            .SelectNodes("//a[contains(@href,'class_list.html?subject=')]");

        if (subjectNodes == null){
            Console.WriteLine("WARNING: Couldent find subjects");
            return new List<string>();
        }

        var subjects = new HashSet<string>();
        foreach (var aTag in subjectNodes)
        {
            var href = aTag.GetAttributeValue("href", "");
            var parts = href.Split("subject=");
            if (parts.Length < 2) 
            {
                Console.WriteLine($"WARNING: Couldent find not find \"subject=\" in href {href}");
                continue; 
            }

            var subject = Uri.UnescapeDataString(parts[1]).Trim();
            if (subject.Length == 0)            
            {
                Console.WriteLine($"WARNING: Subject is empty: {href}");
                continue; 
            }

            subjects.Add(subject);
        }

        return subjects.ToList();
    }
}
