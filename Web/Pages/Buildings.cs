using System.Text;

namespace Web.Pages;

/// <summary>
/// The University's building list - every abbreviation with its full name and
/// street address - held in memory so a location like "SFEBB 3180" can carry
/// its building's name on hover and its address in a calendar event.
///
/// The list is the Office of Space Planning's own (space.utah.edu, "Request a
/// building list"), saved to data/buildings.tsv, plus the few spellings the
/// class schedule uses that the list does not (M LI, SANDY, HPEB, HON CTR).
/// About a fifth of locations name no building at all - CANVAS, SLC UTAH,
/// Online - and those resolve to nothing and are shown as written.
/// </summary>
public class Buildings
{
    public sealed record Building(string Name, string Address);

    /// <summary>A location split into its building, the code as the schedule wrote it, and the room.</summary>
    public sealed record Place(Building Building, string Code, string Room);

    private readonly Dictionary<string, Building> _byCode = new(StringComparer.OrdinalIgnoreCase);

    public Buildings(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "data", "buildings.tsv");
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || parts[0].Length == 0) continue;
            _byCode[parts[0]] = new Building(parts[1], parts[2]);
        }
    }

    /// <summary>
    /// The building and room in a registrar location - "SFEBB 3180", "HON CTR
    /// 150" - or null when it names no building. Codes can contain spaces, so
    /// the longest leading run of words that is a known code wins. A room of
    /// "." is the registrar's way of writing none.
    /// </summary>
    public Place? Resolve(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        var words = location.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var n = Math.Min(3, words.Length); n >= 1; n--)
        {
            var code = string.Join(' ', words[..n]);
            if (!_byCode.TryGetValue(code, out var building)) continue;
            var room = string.Join(' ', words[n..]);
            return new Place(building, code, room == "." ? "" : room);
        }
        return null;
    }

}
