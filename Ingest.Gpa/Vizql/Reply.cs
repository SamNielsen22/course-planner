using System.Text.Json;

namespace Ingest.Gpa.Vizql;

/// <summary>
/// One command response, read only through itself. The data segments are the
/// exception: they accumulate across the session, because later responses
/// index into earlier ones. The documents are never disposed, since the
/// segments point into them.
/// </summary>
public sealed class Reply
{
    public JsonElement Response { get; }
    public JsonElement PresModel { get; }
    /// <summary>The session's interning table, this response's segments now included.</summary>
    public Dictionary<string, JsonElement> Segments { get; }
    /// <summary>Segment key to the number of values it brought, for the log.</summary>
    public Dictionary<string, int> Absorbed { get; } = new();
    /// <summary>Zones the server redrew and sent as data, by zone id.</summary>
    public Dictionary<string, JsonElement> Zones { get; } = new();
    public Dictionary<string, List<FilterEntry>> Filters { get; }
    /// <summary>Worksheets this response carries data for.</summary>
    public HashSet<string> Refreshed { get; }
    /// <summary>Zones the server redrew but sent as an image instead of data - not the same as zones it did not redraw at all.</summary>
    public List<string> Tiled { get; }

    private Dictionary<string, Table?>? _tables;

    public Reply(Dictionary<string, JsonElement> segments, JsonDocument document)
    {
        Response = document.RootElement;
        PresModel = Decoder.PresModel(Response);
        Segments = segments;

        if (PresModel.TryGetProperty("dataDictionary", out var dictionary) && dictionary.TryGetProperty("dataSegments", out var fresh) && fresh.ValueKind == JsonValueKind.Object)
            foreach (var segment in fresh.EnumerateObject())
            {
                if (segment.Value.ValueKind == JsonValueKind.Null) continue;
                var count = 0;
                if (segment.Value.TryGetProperty("dataColumns", out var columns))
                    foreach (var column in columns.EnumerateArray())
                        count += column.TryGetProperty("dataValues", out var values) ? values.GetArrayLength() : 0;
                Absorbed[segment.Name] = count;
                segments[segment.Name] = segment.Value;
            }

        var all = Decoder.Zones(PresModel);
        foreach (var (id, zone) in all)
            if (zone.ValueKind != JsonValueKind.Null && Decoder.HasVizData(zone))
                Zones[id] = zone;
        Filters = Vizql.Filters.ForAllWorksheets(PresModel);
        Refreshed = Zones.Values.Select(Decoder.WorksheetOf).Where(w => w is not null).Select(w => w!).ToHashSet();
        Tiled = all.Values.Where(z => z.ValueKind != JsonValueKind.Null && !Decoder.HasVizData(z))
                          .Select(Decoder.WorksheetOf).Where(w => w is not null && !Refreshed.Contains(w))
                          .Select(w => w!).Distinct().OrderBy(w => w, StringComparer.Ordinal).ToList();
    }

    /// <summary>The interning table in the order the server indexes it.</summary>
    public List<KeyValuePair<string, JsonElement>> Ordered() => Decoder.Ordered(Segments);

    public Dictionary<string, int> DictionarySize() => Decoder.DictionarySize(Segments);

    /// <summary>Refuses a worksheet whose values cannot all be looked up. Every zone is checked, since the decoder decodes them all.</summary>
    private void CheckDictionary(string name)
    {
        var dataFull = Decoder.DataFull(Ordered());
        var strings = dataFull.GetValueOrDefault("cstring") ?? new List<JsonElement>();
        foreach (var zone in Zones.Values)
        {
            var columns = Decoder.Indices(zone);
            if (columns is null) continue;
            foreach (var column in columns)
            {
                var values = dataFull.GetValueOrDefault(column.DataType) ?? strings;
                foreach (var index in column.ValueIndices.Concat(column.AliasIndices))
                {
                    var room = index < 0 ? strings.Count : values.Count;
                    var wanted = index < 0 ? -index - 1 : index;
                    if (wanted >= room)
                    {
                        var where = Decoder.WorksheetOf(zone) ?? "?";
                        Log.Note("stale_dictionary", ("worksheet", name), ("zone", where), ("field", column.FieldCaption), ("index", index),
                                 ("room", room), ("dictionary", DictionarySize()), ("segments", Segments.Count));
                        throw new StaleDictionaryException($"{name}: {where}.{column.FieldCaption} wants entry {index} of {room} - the dictionary we hold does not match this response");
                    }
                }
            }
        }
    }

    /// <summary>This response's own copy of a worksheet. Empty when the zone carried no pane data.</summary>
    public Table Worksheet(string name)
    {
        if (!Refreshed.Contains(name))
            throw new NotRefreshedException($"{name} not in this response (refreshed: [{string.Join(", ", Refreshed.OrderBy(r => r, StringComparer.Ordinal).Select(r => $"'{r}'"))}])");
        CheckDictionary(name);
        if (_tables is null)
        {
            var dataFull = Decoder.DataFull(Ordered());
            _tables = new Dictionary<string, Table?>();
            foreach (var zone in Zones.Values)
            {
                var worksheet = Decoder.WorksheetOf(zone);
                if (worksheet is null || _tables.ContainsKey(worksheet)) continue;
                _tables[worksheet] = Decoder.Decode(zone, dataFull);
            }
        }
        return _tables.GetValueOrDefault(name) ?? new Table();
    }

    /// <summary>One quick filter from this response, preferring a populated copy.</summary>
    private FilterEntry Filter(string level)
    {
        var label = Config.FilterLabels[level];
        FilterEntry? empty = null;
        foreach (var entries in Filters.Values)
            foreach (var one in entries)
            {
                if (one.Column != label) continue;
                if (one.Values.Count > 0) return one;
                empty = one;
            }
        return empty ?? throw new NotRefreshedException($"{label} filter not in this response");
    }

    /// <summary>The values a dropdown offers. Empty is an answer; absent is not.</summary>
    public List<string> Options(string level) => new(Filter(level).Values);

    /// <summary>
    /// Checks the server's own account of what it applied. A filter is sent
    /// only when its domain changes, so <paramref name="absentOk"/> is for
    /// callers with other evidence, such as a refreshed worksheet.
    /// </summary>
    public void Confirm(string level, string value, bool absentOk = false)
    {
        FilterEntry reported;
        try { reported = Filter(level); }
        catch (NotRefreshedException) { if (absentOk) return; throw; }
        var chosen = reported.Selection.Where(v => v != "all").ToHashSet();
        if (!(chosen.Count == 1 && chosen.Contains(value)))
        {
            var shown = chosen.Count <= 5 ? "[" + string.Join(", ", chosen.Select(c => $"'{c}'")) + "]" : $"{chosen.Count} values";
            throw new NotRefreshedException($"{Config.FilterLabels[level]} reads {shown} after selecting '{value}'");
        }
    }
}
