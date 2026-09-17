namespace Ingest.Gpa.Vizql;

public static class Frames
{
    /// <summary>
    /// A bootstrap response is "charcount;json" repeated. Each json is cut at
    /// its own closing brace, since the server's character count need not
    /// match UTF-16 code units.
    /// </summary>
    public static List<string> Split(string text)
    {
        var frames = new List<string>();
        var position = 0;
        while (position < text.Length)
        {
            var semicolon = text.IndexOf(';', position);
            if (semicolon < 0) break;
            var header = text[position..semicolon].Trim();
            if (header.Length == 0 || !header.All(char.IsAsciiDigit)) break;
            var start = semicolon + 1;
            var end = EndOfJsonValue(text, start);
            if (end < 0) break;
            frames.Add(text[start..end]);
            position = end;
        }
        return frames;
    }

    /// <summary>The index just past the JSON value that starts at <paramref name="start"/>, or -1 if it never closes.</summary>
    public static int EndOfJsonValue(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': case '[': depth++; break;
                case '}': case ']':
                    depth--;
                    if (depth == 0) return i + 1;
                    break;
            }
        }
        return -1;
    }
}
