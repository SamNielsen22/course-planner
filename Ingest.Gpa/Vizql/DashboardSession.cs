using System.Net;
using System.Text.Json;

namespace Ingest.Gpa.Vizql;

/// <summary>
/// One live vizql session over plain HTTP. Bootstrap responses no longer carry
/// data, so filters are applied through the dashboard-categorical-filter
/// command, whose responses still do. The session is the id in the URL path.
/// </summary>
public sealed class DashboardSession
{
    private readonly HttpClient _http;
    public string? SessionId { get; set; }
    /// <summary>level -> value currently pinned.</summary>
    public Dictionary<string, string> Selected { get; } = new();
    /// <summary>Select every term rather than one. Set by the all-terms pass and re-applied on every session rebuild.</summary>
    public bool AllTerms { get; set; }
    /// <summary>The interning table, per session.</summary>
    public Dictionary<string, JsonElement> Segments { get; private set; } = new();
    private int _emptyStreak;

    public DashboardSession()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(50),
        };
        handler.CookieContainer.Add(new Uri(Config.Host), new Cookie("tableau_locale", "en"));
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(Config.UserAgent);
        _http.DefaultRequestHeaders.Referrer = new Uri(Config.DashboardUrl + "?:embed=y");
        _http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
    }

    private sealed record Answer(int Status, string Text);

    /// <summary>A POST with four backing-off retries. Every call here is idempotent, so re-sending one is safe.</summary>
    private Answer Post(string url, Dictionary<string, string> form, TimeSpan timeout)
    {
        var backoff = 0.6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                using var response = _http.PostAsync(url, new FormUrlEncodedContent(form), cts.Token).GetAwaiter().GetResult();
                var status = (int)response.StatusCode;
                var text = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
                if (status is 502 or 503 or 504 && attempt <= 4)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(backoff));
                    backoff = Math.Min(backoff * 2, 10);
                    continue;
                }
                return new Answer(status, text);
            }
            catch (Exception error) when (Transport.IsTransportError(error) && attempt <= 4)
            {
                Thread.Sleep(TimeSpan.FromSeconds(backoff));
                backoff = Math.Min(backoff * 2, 10);
            }
        }
    }

    /// <summary>Starts a session if there is none. Its priming response is the only place the term list can be read.</summary>
    public Reply? Connect() => SessionId is null ? StartSession() : null;

    /// <summary>Opens a session, waiting out an outage. Retrying the real call is the health check.</summary>
    private Reply StartSession()
    {
        var delay = Config.OutageFirstWait;
        while (true)
        {
            string reason;
            try { return OpenSession(); }
            catch (Exception error) when (Transport.IsTransportError(error)) { reason = error.GetType().Name; }
            catch (RefusedException error) { reason = error.Message; }
            SessionId = null;
            Log.Note("outage", ("reason", reason), ("retry_in", delay));
            Console.WriteLine($"cannot open a session ({reason}); retrying in {delay}s");
            Thread.Sleep(TimeSpan.FromSeconds(delay));
            delay = Math.Min(delay * 2, Config.OutageMaxWait);
        }
    }

    private Reply OpenSession()
    {
        Segments = new Dictionary<string, JsonElement>();   // segment keys belong to the session that sent them
        _emptyStreak = 0;                                   // a fresh session starts the count over

        var started = Post(Config.Host + Config.VizqlRootPath + "/startSession/viewing?%3Aembed=y&%3Aredirect=auth", new(), TimeSpan.FromSeconds(60));
        if (started.Status != 200) throw new RefusedException($"startSession -> HTTP {started.Status}");
        using var info = JsonDocument.Parse(started.Text);
        var root = info.RootElement;
        string Field(string name) => root.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText()) : "";

        var size = "{\"w\": 1200, \"h\": 800}";
        var booted = Post(Config.Host + Config.VizqlRootPath + $"/bootstrapSession/sessions/{Field("sessionid")}", new()
        {
            ["worksheetPortSize"] = size, ["dashboardPortSize"] = size, ["clientDimension"] = size,
            ["renderMapsClientSide"] = "true", ["isBrowserRendering"] = "true", ["browserRenderingThreshold"] = "100",
            ["formatDataValueLocally"] = "false", ["clientNum"] = "", ["navType"] = "Reload", ["navSrc"] = "Top",
            ["devicePixelRatio"] = "1", ["clientRenderPixelLimit"] = "25000000", ["allowAutogenWorksheetPhoneLayouts"] = "true",
            ["sheet_id"] = Field("sheetId"), ["showParams"] = Field("showParams"),
            ["stickySessionKey"] = root.TryGetProperty("stickySessionKey", out var sticky) ? sticky.GetRawText() : "{}",
            ["filterTileSize"] = "200", ["locale"] = "en_US", ["language"] = "en", ["verboseMode"] = "false",
            [":session_feature_flags"] = "{}", ["keychain_version"] = "1",
        }, TimeSpan.FromSeconds(120));
        if (booted.Status != 200) throw new RefusedException($"bootstrapSession -> HTTP {booted.Status}");
        var frames = Frames.Split(booted.Text);
        if (frames.Count == 0) throw new RefusedException("dashboard never loaded");
        using var first = JsonDocument.Parse(frames[0]);
        SessionId = first.RootElement.GetProperty("newSessionId").GetString();
        Log.Note("session_open", ("session", SessionId), ("replaying", new Dictionary<string, string>(Selected)));

        var primed = Prime();

        // Selecting every term is a filter-all, not a value, so it leaves
        // nothing in Selected for the loop below to replay.
        if (AllTerms) Command(Config.FilterLabels["term"], [], "filter-all");

        foreach (var level in Config.FilterOrder)
        {
            if (level == "term" && AllTerms) continue;   // replayed above as a filter-all; as a value it would pin the literal "(all)"
            if (Selected.TryGetValue(level, out var value)) Command(Config.FilterLabels[level], [value]);
        }
        return primed;
    }

    /// <summary>Select no subject at all, on a session that has just booted: the one response that carries every dropdown's full option list.</summary>
    public Reply Prime() => Command(Config.FilterLabels["subject"], []);

    private Reply Command(string label, IReadOnlyList<string> values, string updateType = "filter-replace")
    {
        Thread.Sleep(Config.SecondsBetweenLoads);
        var started = DateTime.UtcNow;
        var reply = Post(Config.Host + Config.VizqlRootPath + $"/sessions/{SessionId}/commands/tabdoc/dashboard-categorical-filter", new()
        {
            ["dashboard"] = Config.DashboardName,
            ["qualifiedFieldCaption"] = label,
            ["exclude"] = "false",
            ["filterUpdateType"] = updateType,
            ["filterValues"] = JsonSerializer.Serialize(values),
        }, TimeSpan.FromSeconds(180));
        var seconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);
        if (reply.Status != 200)
        {
            Log.Note("command", ("filter", label), ("values", values), ("how", updateType), ("seconds", seconds), ("status", reply.Status));
            throw new RefusedException($"filter {label}=[{string.Join(", ", values)}] -> HTTP {reply.Status}: {reply.Text[..Math.Min(120, reply.Text.Length)]}");
        }
        var answer = new Reply(Segments, JsonDocument.Parse(reply.Text));
        Log.Note("command", ("filter", label), ("values", values), ("how", updateType), ("seconds", seconds),
                 ("refreshed", answer.Refreshed.OrderBy(r => r, StringComparer.Ordinal).ToList()), ("tiled", answer.Tiled),
                 ("segments_in", answer.Absorbed), ("dictionary", answer.DictionarySize()), ("selected", new Dictionary<string, string>(Selected)));

        if (answer.Refreshed.Count > 0) _emptyStreak = 0;
        else if (++_emptyStreak >= Config.WedgedAfterEmpty)
        {
            Log.Note("session_wedged", ("streak", _emptyStreak), ("filter", label), ("values", values), ("selected", new Dictionary<string, string>(Selected)));
            throw new SessionWedgedException($"{_emptyStreak} commands in a row came back with no worksheet - the session has stopped answering, and every course after this one would be skipped for nothing");
        }
        return answer;
    }

    /// <summary>Pins one filter and returns the response proving it applied. Only a refusal costs the session.</summary>
    public Reply Select(string level, string value)
    {
        Exception? failure = null;
        for (var attempt = 1; attempt <= Config.LoadAttempts; attempt++)
        {
            try
            {
                if (SessionId is null) StartSession();
                var reply = SelectOnce(level, value);
                if (level != "term" && (reply.Refreshed.Contains(Config.GpaSheet) || reply.Refreshed.Contains(Config.HeadcountSheet)))
                    reply.Confirm(level, value, absentOk: true);   // the sheets came back fresh, so the selection landed
                else if (level != "term")
                    reply.Confirm(level, value);                   // a term pin re-renders nothing; sweep_term confirms it from the next response
                return reply;
            }
            catch (Exception error) when (Transport.IsTransportError(error)) { failure = error; }   // keep the session; it outlives these
            catch (NotRefreshedException error) { failure = error; }                                 // the session is fine, the view is not
            catch (Exception error) { SessionId = null; failure = error; }                            // refused - the session may be gone
            if (attempt == Config.LoadAttempts) throw failure!;
            Thread.Sleep(TimeSpan.FromSeconds(Config.SecondsBetweenRetries * attempt));
        }
        throw failure!;
    }

    /// <summary>Clears the narrower filters, widens this one if it is already pinned, then pins. Recorded before it is issued, since a failed command may still apply.</summary>
    private Reply SelectOnce(string level, string value)
    {
        var at = Array.IndexOf(Config.FilterOrder, level);
        foreach (var narrower in Config.FilterOrder.Skip(at + 1).Reverse())
            if (Selected.ContainsKey(narrower))
            {
                Command(Config.FilterLabels[narrower], [], "filter-all");
                Selected.Remove(narrower);
            }

        if (Selected.TryGetValue(level, out var current) && current == value)
        {
            Command(Config.FilterLabels[level], [], "filter-all");
            Selected.Remove(level);
        }

        if (level == "term" && value == Config.AllTerms)
        {
            AllTerms = true;
            Selected[level] = value;
            return Command(Config.FilterLabels["term"], [], "filter-all");
        }

        if (level == "term") AllTerms = false;   // a real term after the all-terms pass: the flag must go with it

        Selected[level] = value;
        return Command(Config.FilterLabels[level], [value]);
    }

    /// <summary>Reads a section again, section first then course, so the last command is a change the server redraws.</summary>
    public Reply RereadSection(string catnbr, string section)
    {
        foreach (var level in new[] { "section", "course" })
            if (Selected.ContainsKey(level))
            {
                Command(Config.FilterLabels[level], [], "filter-all");
                Selected.Remove(level);
            }

        Selected["section"] = section;
        var widened = Command(Config.FilterLabels["section"], [section]);
        widened.Confirm("section", section, absentOk: widened.Refreshed.Contains(Config.GpaSheet) || widened.Refreshed.Contains(Config.HeadcountSheet));

        Selected["course"] = catnbr;
        var narrowed = Command(Config.FilterLabels["course"], [catnbr]);
        bool Both(Reply r) => r.Refreshed.Contains(Config.GpaSheet) && r.Refreshed.Contains(Config.HeadcountSheet);
        if (Both(narrowed)) return narrowed;
        if (Both(widened)) return widened;
        return narrowed.Refreshed.Count > 0 ? narrowed : widened;
    }

    /// <summary>Drop the vizql session, keeping the HTTP connection and the term pin.</summary>
    public void CloseSession()
    {
        Log.Note("session_retire", ("session", SessionId), ("segments", Segments.Count), ("dictionary", Decoder.DictionarySize(Segments)));
        SessionId = null;
        Segments = new Dictionary<string, JsonElement>();
        var term = Selected.TryGetValue("term", out var t) ? t : null;
        Selected.Clear();
        if (term is not null) Selected["term"] = term;
    }

    public void Close()
    {
        try { _http.Dispose(); } catch { }
    }
}
