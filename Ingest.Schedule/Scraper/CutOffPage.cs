/// <summary>What to keep of a page the server cut off partway.</summary>
static class CutOffPage
{
    /// <summary>The page without the half-written card the cut fell in. The cards before it are whole.</summary>
    public static string DropUnfinishedCard(string html)
    {
        var last = html.LastIndexOf("class-info", StringComparison.Ordinal);
        if (last < 0) return html;
        var open = html.LastIndexOf('<', last);
        return open < 0 ? html : html[..open];
    }
}
