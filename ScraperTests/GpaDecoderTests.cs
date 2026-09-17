using System.Globalization;
using System.Text.Json;
using Ingest.Gpa;
using Ingest.Gpa.Vizql;

namespace ScraperTests;

/// <summary>
/// The C# reading of the Tableau protocol against the Python's, over six
/// captured responses. The fixtures hold each raw response, what the Python
/// library decoded, and the rows it would have written - which match gpa3.csv.
/// </summary>
public class GpaDecoderTests
{
    private static readonly string Samples = Path.Combine(AppContext.BaseDirectory, "gpa-samples");
    private static readonly string[] Steps = ["01_prime", "02_term", "03_subject", "04_course", "05_section", "06_reread"];

    private static JsonElement Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Samples, name))).RootElement;

    /// <summary>
    /// Every step replayed in order on one shared dictionary and checked as it
    /// arrives. The server re-sends slot 0 with new contents partway, so a
    /// reply decoded later would be the stale read the scraper refuses.
    /// </summary>
    private static void ForEachStep(Action<string, Reply, JsonElement> check)
    {
        var segments = new Dictionary<string, JsonElement>();
        foreach (var step in Steps)
        {
            var reply = new Reply(segments, JsonDocument.Parse(File.ReadAllText(Path.Combine(Samples, step + ".response.json"))));
            check(step, reply, Load(step + ".decoded.json"));
        }
    }

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => Decoder.Text(e)).ToList();

    [Fact]
    public void RefreshedTiledAndAbsorbedSegmentsMatchTheLibrary()
    {
        ForEachStep((step, reply, decoded) =>
        {
            Assert.Equal(Strings(decoded.GetProperty("refreshed")), reply.Refreshed.OrderBy(r => r, StringComparer.Ordinal).ToList());
            Assert.Equal(Strings(decoded.GetProperty("tiled")), reply.Tiled);
            var absorbed = decoded.GetProperty("absorbed").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
            Assert.Equal(absorbed, reply.Absorbed);
            // The reread fixture is only the LAST of that path's four commands; the
            // two clears and the section pin before it rewrote the dictionary and
            // were not saved, so its dictionary cannot be replayed. Structure only.
            if (step == "06_reread") return;
            var dictionary = decoded.GetProperty("dictionary").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
            var mine = reply.DictionarySize();
            Assert.True(dictionary.Count == mine.Count && dictionary.All(d => mine.TryGetValue(d.Key, out var n) && n == d.Value),
                $"{step}: dictionary expected {{{string.Join(", ", dictionary.Select(d => d.Key + "=" + d.Value))}}} got {{{string.Join(", ", mine.Select(d => d.Key + "=" + d.Value))}}}; segments held {string.Join(",", reply.Ordered().Select(o => o.Key))}");
            Assert.Equal(Strings(decoded.GetProperty("segments_held")), reply.Ordered().Select(s => s.Key).ToList());
        });
    }

    [Fact]
    public void OptionsAndFiltersMatchTheLibrary()
    {
        ForEachStep((step, reply, decoded) =>
        {
            foreach (var level in Config.FilterOrder)
            {
                var expected = decoded.GetProperty("options").GetProperty(level);
                if (expected.ValueKind == JsonValueKind.Object)   // the library raised NotRefreshed
                    Assert.Throws<NotRefreshedException>(() => reply.Options(level));
                else
                    Assert.Equal(Strings(expected), reply.Options(level));
            }
            var filters = decoded.GetProperty("filters");
            Assert.Equal(filters.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal), reply.Filters.Keys.OrderBy(n => n, StringComparer.Ordinal));
            foreach (var worksheet in filters.EnumerateObject())
            {
                var mine = reply.Filters[worksheet.Name];
                var theirs = worksheet.Value.EnumerateArray().ToList();
                Assert.Equal(theirs.Count, mine.Count);
                for (var i = 0; i < theirs.Count; i++)
                {
                    Assert.Equal(Decoder.Text(theirs[i].GetProperty("column")), mine[i].Column);
                    Assert.Equal(Strings(theirs[i].GetProperty("values")), mine[i].Values);
                    Assert.Equal(Strings(theirs[i].GetProperty("selection")), mine[i].Selection);
                }
            }
        });
    }

    private static string Norm(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n.ToString("R", CultureInfo.InvariantCulture) : text;

    [Fact]
    public void WorksheetsDecodeToTheSameCells()
    {
        string[] read = ["Catnbr-alias", "Measure Names-alias", "Measure Values-alias", "CNT(Emplid Proxy)-alias", "Grade Group-alias"];
        var checkedCells = 0;
        ForEachStep((step, reply, decoded) =>
        {
            if (step == "06_reread") return;   // see RefreshedTiledAndAbsorbedSegmentsMatchTheLibrary
            foreach (var sheet in new[] { Config.GpaSheet, Config.HeadcountSheet })
            {
                var expected = decoded.GetProperty("tables").GetProperty(sheet);
                if (expected.ValueKind == JsonValueKind.Null) { Assert.DoesNotContain(sheet, reply.Refreshed); continue; }
                Assert.Equal(JsonValueKind.Array, expected.ValueKind);
                var table = reply.Worksheet(sheet);
                var rows = expected.EnumerateArray().ToList();
                Assert.True(rows.Count == table.Rows, $"{step} {sheet}: library has {rows.Count} rows, port {table.Rows}");
                for (var r = 0; r < rows.Count; r++)
                    foreach (var column in read)
                        if (rows[r].TryGetProperty(column, out var cell))
                        {
                            Assert.True(Norm(Decoder.Text(cell)) == Norm(table.Text(r, column)), $"{step} {sheet} row {r} {column}: library '{Decoder.Text(cell)}' vs port '{table.Text(r, column)}' (columns: {string.Join(" | ", table.Columns.Keys)})");
                            checkedCells++;
                        }
            }
        });
        Assert.True(checkedCells > 500, $"only {checkedCells} cells compared");
    }

    [Fact]
    public void TheRowsThePythonWouldWriteComeOutIdentical()
    {
        var expected = Load("expected_rows.json");
        var checkedRows = 0;
        ForEachStep((step, reply, _) =>
        {
            var name = step switch { "04_course" => "course", "05_section" => "section", _ => null };
            if (name is null) return;
            var want = expected.GetProperty(name);
            var counts = Sweep.GradeCounts(reply, "2420");
            var graded = Config.GpaBearing.Sum(c => counts.GetValueOrDefault(c));
            var stats = Sweep.GpaStats(reply, "2420", graded);
            Assert.Equal(want.GetProperty("stats").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!), stats);
            Assert.Equal(want.GetProperty("counts").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()), counts);
            checkedRows++;
        });
        Assert.Equal(2, checkedRows);
    }

    [Fact]
    public void StaleDictionaryIsRefusedBeforeAnyNumberIsBelieved()
    {
        // The section response decoded on an EMPTY dictionary: every index past the end is a refusal, not a shrug.
        var fresh = new Dictionary<string, JsonElement>();
        var response = JsonDocument.Parse(File.ReadAllText(Path.Combine(Samples, "05_section.response.json")));
        var reply = new Reply(fresh, response);
        // Its own segment came along, so it holds slot 2 but not slots 0 and 1 the response also indexes into.
        Assert.Throws<StaleDictionaryException>(() => reply.Worksheet(Config.GpaSheet));
    }

    [Fact]
    public void BootstrapFramesSplitByTheirOwnBraces()
    {
        var text = "12;{\"a\":\"x;y\"}7;{\"b\":2}trailing";
        var frames = Frames.Split(text);
        Assert.Equal(["{\"a\":\"x;y\"}", "{\"b\":2}"], frames);
        Assert.Empty(Frames.Split("not a frame"));
    }

    [Fact]
    public void CsvFieldsAreQuotedOnlyWhenTheyMustBe()
    {
        Assert.Equal("2.70", Sweep.CsvField("2.70"));
        Assert.Equal("\"a,b\"", Sweep.CsvField("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", Sweep.CsvField("say \"hi\""));
        Assert.Equal(("Fall 2025", "CS"), Sweep.Shorten("Fall 2025 End of Term", "CS - Computer Science"));
    }
}
