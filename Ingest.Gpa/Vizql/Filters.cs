using System.Text.Json;

namespace Ingest.Gpa.Vizql;

/// <summary>One quick filter as a response reports it: its column caption, the values it offers, and the ones selected.</summary>
public sealed record FilterEntry(string Column, List<string> Values, List<string> Selection);

public static class Filters
{
    /// <summary>
    /// tableauscraper's getFiltersForAllWorksheet for a command response:
    /// for every worksheet zone carrying data, the quick filters found in
    /// zones of the same worksheet that carry a filtersJson. A filter that
    /// did not narrow reports every value it offers plus 'all'.
    /// </summary>
    public static Dictionary<string, List<FilterEntry>> ForAllWorksheets(JsonElement presModel)
    {
        var zones = Decoder.Zones(presModel);
        var result = new Dictionary<string, List<FilterEntry>>();
        foreach (var zone in zones.Values)
        {
            if (!Decoder.HasVizData(zone)) continue;
            var worksheet = Decoder.WorksheetOf(zone);
            if (worksheet is null) continue;
            result[worksheet] = List(zones, worksheet);
        }
        return result;
    }

    private static List<FilterEntry> List(Dictionary<string, JsonElement> zones, string worksheet)
    {
        var entries = new List<FilterEntry>();
        foreach (var zone in zones.Values)
        {
            if (Decoder.WorksheetOf(zone) != worksheet) continue;
            if (!zone.TryGetProperty("presModelHolder", out var holder) || !holder.TryGetProperty("visual", out var visual)
                || !visual.TryGetProperty("filtersJson", out var filtersJson) || filtersJson.ValueKind != JsonValueKind.String) continue;
            using var doc = JsonDocument.Parse(filtersJson.GetString()!);
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                if (!t.TryGetProperty("table", out var table) || !table.TryGetProperty("schema", out var schema) || !table.TryGetProperty("tuples", out var tuples)) continue;
                var values = new List<string>();
                var selection = new List<string>();
                foreach (var tuple in tuples.EnumerateArray())
                {
                    if (!tuple.TryGetProperty("t", out var tv) || tv.ValueKind != JsonValueKind.Array || tv.GetArrayLength() == 0) continue;
                    var v = Decoder.Text(tv[0].TryGetProperty("v", out var vv) ? vv : null);
                    values.Add(v);
                    if (tuple.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.True) selection.Add(v);
                }
                var all = (t.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.True)
                       || (t.TryGetProperty("allChecked", out var ac) && ac.ValueKind == JsonValueKind.True);
                foreach (var column in schema.EnumerateArray())
                {
                    var caption = column.TryGetProperty("caption", out var c) ? Decoder.Text(c) : "";
                    entries.Add(new FilterEntry(caption, values, all ? [.. values, "all"] : selection));
                }
            }
        }
        return entries;
    }
}
