/// <summary>
/// What to keep of a page the server cut off partway. The scraper side of the
/// crawler's cut-off handling, kept here so the scraper tests can reach it.
/// </summary>
static class CutOffPage
{
    /// <summary>
    /// A cut-off page, without the card the cut fell in. The server failed
    /// while writing that card, so it is missing its lines and would be stored
    /// as a section with no time, room or seats; the cards before it are whole.
    /// </summary>
    public static string DropUnfinishedCard(string html)
    {
        var last = html.LastIndexOf("class-info", StringComparison.Ordinal);
        if (last < 0) return html;
        var open = html.LastIndexOf('<', last);
        return open < 0 ? html : html[..open];
    }
}
