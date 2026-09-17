using HtmlAgilityPack;
using System.Text.RegularExpressions;

/// <summary>Small helpers shared by the scrapers.</summary>
static class HtmlUtils
{
    /// <summary>An element's text with entities decoded and runs of whitespace collapsed.</summary>
    public static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return Regex
            .Replace(HtmlEntity.DeEntitize(text), @"\s+", " ")
            .Trim();
    }


}
