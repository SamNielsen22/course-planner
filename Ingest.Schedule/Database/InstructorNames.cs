using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ingest.Schedule.Database;

/// <summary>
/// The registrar's spelling of a person's name, cleaned for display.
///
/// Two problems, both measured against the 7,921 names in the catalogue:
/// 1,914 of them (24%) arrive in BLOCK CAPITALS while the rest are mixed case,
/// so an alphabetical list and a name search both behave oddly; and a handful
/// carry a pronoun parenthetical the registrar's form let people type into the
/// name field.
///
/// A preferred name in parentheses - "Feng, Tianli (Andy)" - is NOT a defect and
/// is left alone. Only pronoun parentheticals are stripped.
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

    /// <summary>
    /// Title case that respects the shapes real surnames take. A plain
    /// ToTitleCase turns MCDONALD into "Mcdonald" and O'BRIEN into "O'brien",
    /// which is a different kind of wrong from leaving them shouting.
    /// </summary>
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
