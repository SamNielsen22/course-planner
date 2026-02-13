using HtmlAgilityPack;
using System.Text.RegularExpressions;

static class ScraperUtils
{
    public static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return Regex
            .Replace(HtmlEntity.DeEntitize(text), @"\s+", " ")
            .Trim();
    }
    static readonly HttpClient http = new HttpClient();
    public static HtmlDocument LoadFromUrl(string url)
    {
        Thread.Sleep(150);
        var html = http.GetStringAsync(url).Result;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        return doc;
    }

}
