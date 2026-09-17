using System.Globalization;
using System.Text.Json;

namespace Ingest.Gpa.Vizql;

/// <summary>
/// A decoded worksheet: columns named "field-value" and "field-alias". A
/// missing cell reads as 0, as it did in the Python.
/// </summary>
public sealed class Table
{
    public Dictionary<string, List<JsonElement>> Columns { get; } = new();
    public int Rows => Columns.Count == 0 ? 0 : Columns.Values.Max(c => c.Count);

    private static readonly JsonElement Zero = JsonDocument.Parse("0").RootElement;

    /// <summary>The cell, or null when the table has no such column (Python's row.get).</summary>
    public JsonElement? Cell(int row, string column) =>
        Columns.TryGetValue(column, out var cells) ? (row < cells.Count ? cells[row] : Zero) : null;

    /// <summary>The cell as Python's str() would print it: strings as they are, numbers as written, None for a missing column.</summary>
    public string Text(int row, string column) => Decoder.Text(Cell(row, column));
}

public static class Decoder
{
    /// <summary>Python's str() of a decoded JSON value.</summary>
    public static string Text(JsonElement? value) => value switch
    {
        null => "None",
        { ValueKind: JsonValueKind.String } v => v.GetString()!,
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        { ValueKind: JsonValueKind.True } => "True",
        { ValueKind: JsonValueKind.False } => "False",
        { ValueKind: JsonValueKind.Null } => "None",
        { } v => v.GetRawText(),
    };

    /// <summary>float(value), or null where Python would raise.</summary>
    public static double? Number(JsonElement? value)
    {
        if (value is not { } v) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString()!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
        return null;
    }

    /// <summary>The interning table in slot-number order, which is how the server indexes it. Arrival order and text order both decode wrongly.</summary>
    public static List<KeyValuePair<string, JsonElement>> Ordered(IReadOnlyDictionary<string, JsonElement> segments) =>
        segments.OrderBy(s => int.TryParse(s.Key, out _) ? 0 : 1)
                .ThenBy(s => int.TryParse(s.Key, out var n) ? n : 0)
                .ThenBy(s => s.Key, StringComparer.Ordinal)
                .ToList();

    /// <summary>Every segment's dataColumns concatenated, per data type - the list the worksheets index into.</summary>
    public static Dictionary<string, List<JsonElement>> DataFull(IEnumerable<KeyValuePair<string, JsonElement>> orderedSegments)
    {
        var full = new Dictionary<string, List<JsonElement>>();
        foreach (var (_, segment) in orderedSegments)
        {
            if (segment.ValueKind != JsonValueKind.Object || !segment.TryGetProperty("dataColumns", out var columns)) continue;
            foreach (var column in columns.EnumerateArray())
            {
                var type = column.TryGetProperty("dataType", out var t) ? t.GetString() ?? "" : "";
                if (!full.TryGetValue(type, out var list)) full[type] = list = new List<JsonElement>();
                if (column.TryGetProperty("dataValues", out var values))
                    list.AddRange(values.EnumerateArray());
            }
        }
        return full;
    }

    /// <summary>Values held per data type - the interning table's fingerprint, logged beside every row.</summary>
    public static Dictionary<string, int> DictionarySize(IReadOnlyDictionary<string, JsonElement> segments)
    {
        var totals = new Dictionary<string, int>();
        foreach (var segment in segments.Values)
        {
            if (segment.ValueKind != JsonValueKind.Object || !segment.TryGetProperty("dataColumns", out var columns)) continue;
            foreach (var column in columns.EnumerateArray())
            {
                var type = column.TryGetProperty("dataType", out var t) ? t.GetString() ?? "" : "";
                var count = column.TryGetProperty("dataValues", out var values) ? values.GetArrayLength() : 0;
                totals[type] = totals.GetValueOrDefault(type) + count;
            }
        }
        return totals;
    }

    /// <summary>The applicationPresModel of a command response, or an empty object.</summary>
    public static JsonElement PresModel(JsonElement response)
    {
        if (response.TryGetProperty("vqlCmdResponse", out var cmd)
            && cmd.TryGetProperty("layoutStatus", out var layout)
            && layout.TryGetProperty("applicationPresModel", out var pres))
            return pres;
        return JsonDocument.Parse("{}").RootElement;
    }

    /// <summary>presModel.workbookPresModel.dashboardPresModel.zones, zone id to zone (a zone may be null).</summary>
    public static Dictionary<string, JsonElement> Zones(JsonElement presModel)
    {
        var zones = new Dictionary<string, JsonElement>();
        if (presModel.TryGetProperty("workbookPresModel", out var wb)
            && wb.TryGetProperty("dashboardPresModel", out var dash)
            && dash.TryGetProperty("zones", out var z) && z.ValueKind == JsonValueKind.Object)
            foreach (var zone in z.EnumerateObject())
                zones[zone.Name] = zone.Value;
        return zones;
    }

    public static bool HasVizData(JsonElement zone) =>
        zone.ValueKind == JsonValueKind.Object
        && zone.TryGetProperty("presModelHolder", out var holder)
        && holder.TryGetProperty("visual", out var visual)
        && visual.TryGetProperty("vizData", out _);

    public static string? WorksheetOf(JsonElement zone) =>
        zone.ValueKind == JsonValueKind.Object && zone.TryGetProperty("worksheet", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;

    /// <summary>One column's addressing: where in the dictionary its cells live.</summary>
    public sealed record ColumnIndices(string FieldCaption, string DataType, string Fn, List<int> ValueIndices, List<int> AliasIndices);

    /// <summary>The columns of a zone's vizData that carry a field caption, with their indices - null when the zone has no pane data.</summary>
    public static List<ColumnIndices>? Indices(JsonElement zone)
    {
        var details = zone.GetProperty("presModelHolder").GetProperty("visual").GetProperty("vizData");
        if (!details.TryGetProperty("paneColumnsData", out var columnsData)) return null;
        var panes = columnsData.GetProperty("paneColumnsList");
        var result = new List<ColumnIndices>();
        foreach (var t in columnsData.GetProperty("vizDataColumns").EnumerateArray())
        {
            var caption = t.TryGetProperty("fieldCaption", out var fc) && fc.ValueKind == JsonValueKind.String ? fc.GetString()! : "";
            if (caption.Length == 0) continue;
            var pane = panes[t.GetProperty("paneIndices")[0].GetInt32()];
            var cell = pane.GetProperty("vizPaneColumns")[t.GetProperty("columnIndices")[0].GetInt32()];
            result.Add(new ColumnIndices(
                caption,
                t.TryGetProperty("dataType", out var dt) && dt.ValueKind == JsonValueKind.String ? dt.GetString()! : "",
                t.TryGetProperty("fn", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString()! : "",
                cell.GetProperty("valueIndices").EnumerateArray().Select(i => i.GetInt32()).ToList(),
                cell.GetProperty("aliasIndices").EnumerateArray().Select(i => i.GetInt32()).ToList()));
        }
        return result;
    }

    /// <summary>tableauscraper's getData: a table from a zone's indices and the dictionary. Null when the zone has no pane data.</summary>
    public static Table? Decode(JsonElement zone, Dictionary<string, List<JsonElement>> dataFull)
    {
        var indices = Indices(zone);
        if (indices is null) return null;
        var cstring = dataFull.GetValueOrDefault("cstring") ?? new List<JsonElement>();
        var table = new Table();
        foreach (var column in indices)
        {
            var t = dataFull.GetValueOrDefault(column.DataType) ?? cstring;
            Add(table, column, "value", column.ValueIndices, t, cstring);
            Add(table, column, "alias", column.AliasIndices, t, cstring);
        }
        return table;
    }

    private static void Add(Table table, ColumnIndices column, string kind, List<int> indices, List<JsonElement> t, List<JsonElement> cstring)
    {
        if (indices.Count == 0) return;
        var values = new List<JsonElement>();
        foreach (var index in indices)
            if (index < t.Count)                      // an index past the end is silently dropped, as the library drops it
                values.Add(index >= 0 ? t[index] : cstring[-index - 1]);
        var name = $"{column.FieldCaption}-{kind}";
        if (table.Columns.ContainsKey(name)) name = $"{column.FieldCaption}-{column.Fn}-{kind}";
        table.Columns[name] = values;
    }
}
