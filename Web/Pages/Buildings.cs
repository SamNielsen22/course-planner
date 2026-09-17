using System.Text;

namespace Web.Pages;

/// <summary>
/// The University's building codes with names and addresses, from
/// data/buildings.tsv. Locations that name no building resolve to null.
/// </summary>
public class Buildings
{
    public sealed record Building(string Name, string Address);

    /// <summary>A location split into building, code and room.</summary>
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

    /// <summary>The building and room in "SFEBB 3180" or "HON CTR 150", or null. The longest known code wins.</summary>
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
