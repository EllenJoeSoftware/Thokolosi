using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Thokolosi.Core.Remote;

public record PairedPhone(string Uid, string Name, DateTime PairedAt, string? Code = null);

/// <summary>
/// The PC side of the phone link (see docs/PROTOCOL.md). Signs in to Firebase anonymously, publishes presence,
/// mirrors the chat to the outbox, and turns inbox items from paired phones into events for the app.
/// </summary>
public sealed class PhoneLink : IAsyncDisposable
{
    readonly AppConfig _cfg;
    readonly FirebaseClient _fb;
    readonly CancellationTokenSource _cts = new();
    readonly Channel<JsonObject> _outbox = Channel.CreateUnbounded<JsonObject>();
    readonly Dictionary<string, PairedPhone> _phones = new();
    readonly object _gate = new();
    bool _phonesLoaded;
    readonly HashSet<string> _handled = new();
    System.Threading.Timer? _heartbeat;
    JsonObject _lastInfo = new();

    public string PcUid => _fb.Uid ?? "";
    public bool Connected { get; private set; }
    public string? LastError { get; private set; }

    // ---- events (raised on background threads) ----
    public event Action<string, bool, string>? MessageReceived;   // text, voice, phone name
    public event Action<string, bool, string>? ApprovalAnswered;  // approvalId, allow, by
    public event Action? StopRequested;
    public event Action<bool?, bool?>? ModeChangeRequested;       // auto, silent
    public event Action? NewChatRequested;
    public event Action<string>? PlaybookRequested;
    public event Action<string?>? UndoRequested;
    public event Action<PairedPhone>? PhonePaired;
    public event Action? PhonesChanged;
    public event Action<string>? JobChangedRemotely;
    public event Action<JsonObject>? PlaybooksChangedRemotely;

    public PhoneLink(AppConfig cfg)
    {
        _cfg = cfg;
        var p = cfg.Phone;
        _fb = new FirebaseClient(p.ApiKey, p.DatabaseUrl, p.PcUid, p.RefreshToken);
    }

    public IReadOnlyList<PairedPhone> Phones { get { lock (_gate) return _phones.Values.OrderBy(p => p.PairedAt).ToList(); } }

    string Pc => $"pcs/{_fb.Uid}";

    /// <summary>Connects, retrying with backoff (the PC often starts before Wi-Fi is up). Returns once connected or disposed.</summary>
    public async Task StartAsync()
    {
        var ct = _cts.Token;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await _fb.TokenAsync(ct);
                SaveIdentity();
                Connected = true;
                LastError = null;
                ActivityLog.Write("PHONE", $"Phone link connected (pc {_fb.Uid})");
                break;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                if (attempt < 3 || attempt % 10 == 0) ActivityLog.Write("PHONE", "Phone link not connected yet: " + ex.Message);
                // Configuration problems won't fix themselves; network problems will.
                if (ex is InvalidOperationException && attempt >= 2) return;
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(300, 15 * (attempt + 1))), ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        _ = Task.Run(() => SendLoop(ct));
        await UpdateInfoAsync(new JsonObject { ["online"] = true });
        _heartbeat = new System.Threading.Timer(_ => _ = UpdateInfoAsync(new JsonObject()), null, 60_000, 60_000);
        _ = Task.Run(() => PruneOutboxAsync(ct));
        _ = Task.Run(() => _fb.ListenAsync($"{Pc}/phones", OnPhones, ct));
        _ = Task.Run(() => _fb.ListenAsync($"{Pc}/inbox", OnInbox, ct));
        _ = Task.Run(() => _fb.ListenAsync($"{Pc}/config", OnConfig, ct));
    }

    void SaveIdentity()
    {
        if (_cfg.Phone.PcUid == _fb.Uid && _cfg.Phone.RefreshToken == _fb.RefreshToken) return;
        _cfg.Phone.PcUid = _fb.Uid;
        _cfg.Phone.RefreshToken = _fb.RefreshToken;
        _cfg.Save();
    }

    // ---------- pairing ----------

    /// <summary>Creates a one-time pairing code (valid 10 minutes) and returns the QR text.</summary>
    public async Task<(string QrText, DateTime Expires)> CreatePairingAsync()
    {
        if (!Connected) throw new InvalidOperationException(LastError ?? "Phone link isn't connected.");
        var code = Base32(RandomNumberGenerator.GetBytes(20)); // 32 chars, 160 bits
        var expires = DateTime.UtcNow.AddMinutes(10);
        await _fb.PutAsync($"pairings/{code}", new JsonObject
        {
            ["pcUid"] = _fb.Uid,
            ["pcName"] = Environment.MachineName,
            ["expires"] = new DateTimeOffset(expires).ToUnixTimeMilliseconds()
        }, _cts.Token);
        var qr = $"thokolosi://pair?v=1&pc={_fb.Uid}&code={code}&name={Uri.EscapeDataString(Environment.MachineName)}&project={Uri.EscapeDataString(_cfg.Phone.ProjectId)}";
        return (qr, expires.ToLocalTime());
    }

    public Task UnpairAsync(string phoneUid) => _fb.DeleteAsync($"{Pc}/phones/{phoneUid}", _cts.Token);

    async Task OnPhones(string evt, string path, JsonNode? data)
    {
        // A full put at the root (first load, or every reconnect) carries the complete list. Anything else
        // (child put, patch, field change) is applied by re-reading the whole node, which is small.
        JsonObject all;
        if (evt == "put" && path == "/") all = data as JsonObject ?? new JsonObject();
        else all = await _fb.GetAsync($"{Pc}/phones", _cts.Token) as JsonObject ?? new JsonObject();

        var added = new List<PairedPhone>();
        lock (_gate)
        {
            var before = _phones.Keys.ToHashSet();
            _phones.Clear();
            foreach (var (uid, node) in all)
            {
                if (node is not JsonObject o) continue;
                Upsert(uid, o, new List<PairedPhone>());
                if (!before.Contains(uid)) added.Add(_phones[uid]);
            }
        }
        var firstLoad = !_phonesLoaded;
        _phonesLoaded = true;
        PhonesChanged?.Invoke();
        if (firstLoad) return;
        foreach (var p in added)
        {
            ActivityLog.Write("PHONE", $"Paired with {p.Name}");
            PhonePaired?.Invoke(p);
            Post("status", new JsonObject { ["text"] = $"Paired with {p.Name}" });
            if (p.Code != null) { try { await _fb.DeleteAsync($"pairings/{p.Code}", _cts.Token); } catch { } }
        }
    }

    void Upsert(string uid, JsonNode node, List<PairedPhone> added)
    {
        var name = node["name"]?.GetValue<string>() ?? "Phone";
        var at = node["pairedAt"] is JsonValue v && v.TryGetValue<long>(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : DateTime.Now;
        var isNew = !_phones.ContainsKey(uid);
        _phones[uid] = new PairedPhone(uid, name, at, node["code"]?.GetValue<string>());
        if (isNew) added.Add(_phones[uid]);
    }

    string PhoneName(string? uid)
    {
        lock (_gate) return uid != null && _phones.TryGetValue(uid, out var p) ? p.Name : "phone";
    }

    // ---------- inbox ----------

    async Task OnInbox(string evt, string path, JsonNode? data)
    {
        var items = new List<(string Id, JsonObject Item)>();
        if (path == "/")
        {
            // put "/" = whole inbox; patch "/" = {pushId: item, ...}; ignore field-level keys like "id/ts"
            foreach (var (id, node) in data as JsonObject ?? new JsonObject())
                if (node is JsonObject o && !id.Contains('/')) items.Add((id, o));
        }
        else if (data is JsonObject o && !path.Trim('/').Contains('/'))
        {
            items.Add((path.Trim('/'), o));
        }

        var staleBefore = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        foreach (var (id, item) in items.OrderBy(i => i.Item["ts"]?.GetValue<long>() ?? 0))
        {
            lock (_gate) { if (!_handled.Add(id)) continue; }   // never run the same item twice
            try { await _fb.DeleteAsync($"{Pc}/inbox/{id}", _cts.Token); } catch { }
            var ts = item["ts"] is JsonValue tv && tv.TryGetValue<long>(out var t) ? t : 0;
            if (ts > 0 && ts < staleBefore && item["type"]?.GetValue<string>() is "message" or "run_playbook" or "undo" or "approval")
            {
                ActivityLog.Write("PHONE", $"Skipped a phone request older than an hour: {item["text"]}");
                Post("status", new JsonObject { ["text"] = "Skipped an old request sent while this PC was offline: " + (item["text"]?.GetValue<string>() ?? item["type"]?.GetValue<string>()) });
                continue;
            }
            try { Dispatch(item); }
            catch (Exception ex) { ActivityLog.Write("PHONE", "Inbox item failed: " + ex.Message); }
        }
    }

    void Dispatch(JsonObject item)
    {
        var from = item["from"]?.GetValue<string>();
        var name = PhoneName(from);
        switch (item["type"]?.GetValue<string>())
        {
            case "message":
                var text = item["text"]?.GetValue<string>() ?? "";
                if (text.Trim().Length > 0) MessageReceived?.Invoke(text, item["voice"]?.GetValue<bool>() ?? false, name);
                break;
            case "approval":
                ApprovalAnswered?.Invoke(item["approvalId"]?.GetValue<string>() ?? "", item["allow"]?.GetValue<bool>() ?? false, name);
                break;
            case "stop": StopRequested?.Invoke(); break;
            case "set_mode":
                ModeChangeRequested?.Invoke(item["auto"]?.GetValue<bool>(), item["silent"]?.GetValue<bool>());
                break;
            case "new_chat": NewChatRequested?.Invoke(); break;
            case "run_playbook": PlaybookRequested?.Invoke(item["name"]?.GetValue<string>() ?? ""); break;
            case "undo": UndoRequested?.Invoke(item["snapshotId"]?.GetValue<string>()); break;
        }
    }

    // ---------- config (central management) ----------

    long _jobSeen;

    async Task OnConfig(string evt, string path, JsonNode? data)
    {
        JsonNode? job, playbooks;
        var p = path.Trim('/');
        var full = evt == "put" && p == "" ? data : await _fb.GetAsync($"{Pc}/config", _cts.Token);
        job = full?["job"];
        playbooks = full?["playbooks"];
        bool jobTouched = p == "" || p.StartsWith("job");

        if (!jobTouched) { }
        else if (job == null)
        {
            // First run: publish this PC's job so phones can see and edit it.
            await PublishJobAsync(AppConfig.LoadJob());
        }
        else if (job is JsonObject j)
        {
            var updated = j["updatedAt"]?.GetValue<long>() ?? 0;
            var by = j["by"]?.GetValue<string>() ?? "";
            if (updated > _jobSeen && by != "pc" && j["text"]?.GetValue<string>() is { } text)
            {
                _jobSeen = updated;
                var local = AppConfig.LoadJob();
                var localWrite = System.IO.File.Exists(AppConfig.JobPath)
                    ? new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(AppConfig.JobPath)).ToUnixTimeMilliseconds() : 0;
                if (text == local) { }
                else if (localWrite > updated) await PublishJobAsync(local);   // edited on this PC more recently: PC wins
                else JobChangedRemotely?.Invoke(text);
            }
            else _jobSeen = Math.Max(_jobSeen, updated);
        }
        if (p == "" || p.StartsWith("playbooks")) PlaybooksChangedRemotely?.Invoke(playbooks as JsonObject ?? new JsonObject());
    }

    public Task PublishJobAsync(string text) =>
        Safe(() => _fb.PutAsync($"{Pc}/config/job", new JsonObject { ["text"] = text, ["updatedAt"] = FirebaseClient.ServerTimestamp, ["by"] = "pc" }, _cts.Token));

    public Task PublishPlaybookAsync(string name, string description, string steps, long updatedAt) =>
        Safe(() => _fb.PutAsync($"{Pc}/config/playbooks/{SafeKey(name)}", new JsonObject
        {
            ["name"] = name, ["description"] = description, ["steps"] = steps, ["updatedAt"] = updatedAt, ["by"] = "pc"
        }, _cts.Token));

    public Task DeletePlaybookAsync(string name) => Safe(() => _fb.DeleteAsync($"{Pc}/config/playbooks/{SafeKey(name)}", _cts.Token));

    static string SafeKey(string s) => string.Concat(s.Select(c => ".#$[]/".Contains(c) ? '_' : c));

    // ---------- outbox ----------

    /// <summary>Queue an outbox item (fire-and-forget, ordered).</summary>
    public void Post(string type, JsonObject? fields = null)
    {
        if (!Connected) return;
        var item = fields ?? new JsonObject();
        item["type"] = type;
        item["ts"] = FirebaseClient.ServerTimestamp;
        if (item["text"] is JsonValue t && t.TryGetValue<string>(out var s) && s.Length > 20000)
            item["text"] = s[..20000] + "\n... (truncated)";
        _outbox.Writer.TryWrite(item);
    }

    async Task SendLoop(CancellationToken ct)
    {
        await foreach (var item in _outbox.Reader.ReadAllAsync(ct))
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { await _fb.PushAsync($"{Pc}/outbox", item, ct); break; }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    if (attempt == 4) ActivityLog.Write("PHONE", "Outbox send failed: " + ex.Message);
                    else await Task.Delay(2000 * (attempt + 1), ct);
                }
            }
        }
    }

    async Task PruneOutboxAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeMilliseconds();
            var old = await _fb.GetAsync($"{Pc}/outbox", ct, $"&orderBy=%22ts%22&endAt={cutoff}&limitToFirst=500");
            if (old is JsonObject o && o.Count > 0)
            {
                var patch = new JsonObject();
                foreach (var (id, _) in o) patch[id] = null;
                await _fb.PatchAsync($"{Pc}/outbox", patch, ct);
            }
        }
        catch (Exception ex) { ActivityLog.Write("PHONE", "Outbox prune skipped: " + ex.Message); }
    }

    // ---------- presence ----------

    /// <summary>Merge fields into /info (always refreshes lastSeen + online).</summary>
    public async Task UpdateInfoAsync(JsonObject fields)
    {
        if (!Connected) return;
        lock (_gate)
        {
            foreach (var (k, v) in fields) _lastInfo[k] = v?.DeepClone();
        }
        var patch = new JsonObject
        {
            ["name"] = Environment.MachineName,
            ["version"] = typeof(PhoneLink).Assembly.GetName().Version?.ToString(3) ?? "",
            ["online"] = true,
            ["lastSeen"] = FirebaseClient.ServerTimestamp
        };
        lock (_gate) foreach (var (k, v) in _lastInfo) patch[k] = v?.DeepClone();
        await Safe(() => _fb.PatchAsync($"{Pc}/info", patch, _cts.Token));
    }

    static async Task Safe(Func<Task> f)
    {
        try { await f(); }
        catch (Exception ex) { ActivityLog.Write("PHONE", ex.Message); }
    }

    static string Base32(byte[] data)
    {
        const string A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new System.Text.StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(A[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(A[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public async Task<JsonObject> GetPlaybooksAsync() =>
        await _fb.GetAsync($"{Pc}/config/playbooks", _cts.Token) as JsonObject ?? new JsonObject();

    public async ValueTask DisposeAsync()
    {
        _heartbeat?.Dispose();
        // Give queued outbox items (final reply, approval_resolved) a moment to go out.
        for (int i = 0; i < 30 && _outbox.Reader.Count > 0; i++) await Task.Delay(100);
        if (Connected)
        {
            try
            {
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await _fb.PatchAsync($"{Pc}/info", new JsonObject { ["online"] = false, ["busy"] = false, ["lastSeen"] = FirebaseClient.ServerTimestamp }, t.Token);
            }
            catch { }
        }
        _cts.Cancel();
    }
}
