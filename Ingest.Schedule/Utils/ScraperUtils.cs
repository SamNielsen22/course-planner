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
}
