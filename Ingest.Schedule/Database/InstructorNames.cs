using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ingest.Schedule.Database;

/// <summary>
/// A name cleaned for display: about a quarter arrive in block capitals, and a
/// few carry a pronoun parenthetical. A preferred name in parentheses stays.
/// </summary>
public static partial class InstructorNames
{
    [GeneratedRegex(@"\s*\(\s*(he|she|they|him|her|them|ze|xe)\s*/[^)]*\)",
                    RegexOptions.IgnoreCase)]
    private static partial Regex Pronouns();

    /// <summary>Names already in mixed case are the registrar's own and are trusted.</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var name = Pronouns().Replace(raw, "").Trim();
        return name.Any(char.IsLower) ? Collapse(name) : TitleCase(name);
    }

    private static string Collapse(string name) =>
        Regex.Replace(name, @"\s+", " ").Trim();

    /// <summary>Title case that keeps McDonald and O'Brien, which ToTitleCase does not.</summary>
    private static string TitleCase(string name)
    {
        var builder = new StringBuilder(name.Length);
        var startOfWord = true;

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetter(c))
            {
                builder.Append(c);
                // A hyphen, apostrophe or space all begin a new word: Anne-Marie,
                // O'Brien, van Dijk.
                startOfWord = c is '-' or '\'' or ' ' or '.' or ',' or '(';
                continue;
            }

            builder.Append(startOfWord ? char.ToUpper(c, CultureInfo.InvariantCulture)
                                       : char.ToLower(c, CultureInfo.InvariantCulture));
            startOfWord = false;
        }

        var result = builder.ToString();

        // Mc and Mac take a capital on the following letter; a bare "Mac" or a
        // name like "Macey" must not be touched, so both need a length guard.
        result = Regex.Replace(result, @"\bMc([a-z])",
            m => "Mc" + char.ToUpper(m.Groups[1].Value[0]));
        result = Regex.Replace(result, @"\bMac([a-z])(?=[a-z]{2,})",
            m => "Mac" + char.ToUpper(m.Groups[1].Value[0]));

        return Collapse(result);
    }
}
