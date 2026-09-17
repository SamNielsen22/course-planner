using System.Text.Json;

namespace Ingest.Gpa;

/// <summary>
/// One JSON object per line: what was asked for, what came back, and the state
/// of the interning dictionary when each row was read - the only trace a
/// drifted dictionary leaves. Rotates at 250 MB. Never throws.
/// </summary>
public static class Log
{
    [ThreadStatic] private static string? _worker;
    public static string Worker { get => _worker ?? "main"; set => _worker = value; }

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Start() =>
        Note("start", ("output", Config.OutputFile), ("workers", Config.Workers), ("subjects_per_session", Config.SubjectsPerSession));

    public static void Note(string kind, params (string Key, object? Value)[] fields)
    {
        try
        {
            var record = new Dictionary<string, object?>();
            foreach (var (key, value) in fields) record[key] = Plain(value);
            record["at"] = DateTime.Now.ToString("HH:mm:ss");
            record["kind"] = kind;
            record["worker"] = Worker;
            var line = JsonSerializer.Serialize(record, Options);
            lock (Gate)
            {
                Rotate();
                File.AppendAllText(Config.LogFile, line + "\n");
            }
        }
        catch
        {
            // never
        }
    }

    /// <summary>Anything the serializer would choke on goes in as its text, as Python's default=str did.</summary>
    private static object? Plain(object? value) => value switch
    {
        null => null,
        string or bool or int or long or double or float => value,
        Exception e => e.GetType().Name + "(" + e.Message + ")",
        IDictionary<string, int> d => d,
        IDictionary<string, string> d => d,
        IDictionary<string, object?> d => d.ToDictionary(p => p.Key, p => Plain(p.Value)),
        IEnumerable<string> list => list.ToList(),
        IEnumerable<object?> list => list.Select(Plain).ToList(),
        _ => value.ToString(),
    };

    private static void Rotate()
    {
        var info = new FileInfo(Config.LogFile);
        if (!info.Exists || info.Length < Config.LogMaxBytes) return;
        for (var n = Config.LogBackups; n >= 1; n--)
        {
            var older = $"{Config.LogFile}.{n}";
            var newer = n == 1 ? Config.LogFile : $"{Config.LogFile}.{n - 1}";
            if (File.Exists(older)) File.Delete(older);
            if (File.Exists(newer)) File.Move(newer, older);
        }
    }
}
