using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MQTTnet;
using MQTTnet.Client;

namespace Thokolosi.Core.Monitors;

public record MonitorReading(string Name, string Raw, double? Number, string Unit, DateTime Time);
public record RuleStatus(string When, bool Active, DateTime? LastAlert, string? Error);
public record MonitorStatus(string Name, string Kind, bool Enabled, bool Connected, DateTime? LastData, string? LastError,
    IReadOnlyList<MonitorReading> Values, IReadOnlyList<RuleStatus> Rules);

/// <summary>
/// Background monitors (MQTT, HTTP JSON, PowerShell command) with threshold rules, cooldowns, recovery messages
/// and per-day CSV logs in %APPDATA%\Thokolosi\monitors\{name}\yyyy-MM-dd.csv (at most one row per minute).
/// Starts all enabled monitors on construction; call Reload() after settings change.
/// </summary>
public sealed class MonitorService : IDisposable
{
    readonly Func<List<MonitorSettings>> _settings;
    readonly Action<string, string> _alert;
    readonly object _gate = new();
    List<MonitorRunner> _runners = new();

    public static string RootDir => Path.Combine(AppConfig.Dir, "monitors");

    public MonitorService(Func<List<MonitorSettings>> settings, Action<string, string> alert)
    {
        _settings = settings;
        _alert = alert;
        Reload();
    }

    /// <summary>Stops all monitors and starts the enabled ones from current settings.</summary>
    public void Reload()
    {
        List<MonitorRunner> old;
        lock (_gate)
        {
            old = _runners;
            _runners = (_settings() ?? new()).Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => new MonitorRunner(s, SafeAlert)).ToList();
        }
        foreach (var r in old) r.Dispose();
        foreach (var r in Runners()) if (r.Settings.Enabled) r.Start();
    }

    void SafeAlert(string title, string text)
    {
        ActivityLog.Write("MONITOR", $"{title}: {text}");
        try { _alert(title, text); } catch (Exception ex) { ActivityLog.Write("MONITOR", "Alert delivery failed: " + ex.Message); }
    }

    List<MonitorRunner> Runners() { lock (_gate) return _runners.ToList(); }

    MonitorRunner? Find(string name) =>
        Runners().FirstOrDefault(r => r.Settings.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<MonitorStatus> Status() => Runners().Select(r => r.Status()).ToList();

    /// <summary>Polls now (http/command), re-evaluates rules, and returns the fresh status.</summary>
    public async Task<IReadOnlyList<MonitorStatus>> CheckNowAsync(string? name, CancellationToken ct)
    {
        var list = string.IsNullOrWhiteSpace(name) ? Runners().Where(r => r.Settings.Enabled).ToList()
            : Find(name!) is { } one ? new List<MonitorRunner> { one } : throw new ArgumentException($"No monitor named '{name}'.");
        await Task.WhenAll(list.Select(r => r.TickAsync(ct, force: true)));
        return list.Select(r => r.Status()).ToList();
    }

    /// <summary>min/avg/max per value plus a compact series over the last N hours, read from the CSV logs.</summary>
    public string History(string name, double hours, int points = 24)
    {
        var settings = Find(name)?.Settings ?? _settings()?.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No monitor named '{name}'.");
        hours = Math.Clamp(hours, 0.25, 24 * 31);
        var since = DateTime.Now.AddHours(-hours);
        var dir = Path.Combine(RootDir, MonitorRunner.SafeName(settings.Name));
        var rows = new List<(DateTime T, Dictionary<string, double> V)>();
        for (var day = since.Date; day <= DateTime.Today; day = day.AddDays(1))
        {
            var file = Path.Combine(dir, $"{day:yyyy-MM-dd}.csv");
            if (!File.Exists(file)) continue;
            string[] lines;
            try { using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var sr = new StreamReader(fs); lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries); }
            catch { continue; }
            if (lines.Length < 2) continue;
            var header = lines[0].Trim().Split(',');
            foreach (var line in lines.Skip(1))
            {
                var cells = line.Trim().Split(',');
                if (!DateTime.TryParseExact(cells[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) || t < since) continue;
                var v = new Dictionary<string, double>();
                for (int i = 1; i < cells.Length && i < header.Length; i++)
                    if (double.TryParse(cells[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) v[header[i]] = d;
                rows.Add((t, v));
            }
        }
        if (rows.Count == 0) return $"{settings.Name}: no logged data in the last {hours:0.#} h.";

        var units = settings.Values.ToDictionary(v => v.Name, v => v.Unit, StringComparer.OrdinalIgnoreCase);
        var names = rows.SelectMany(r => r.V.Keys).Distinct().ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"{settings.Name}: {rows.Count} readings {rows[0].T:MM-dd HH:mm} to {rows[^1].T:MM-dd HH:mm}");
        var span = (rows[^1].T - rows[0].T).TotalMinutes;
        int buckets = Math.Clamp(points, 4, 96);
        double bucketMin = Math.Max(1, span / buckets);
        sb.AppendLine($"Series: average per {Fmt(bucketMin)} min, oldest first");
        foreach (var n in names)
        {
            var vals = rows.Where(r => r.V.ContainsKey(n)).Select(r => (r.T, Val: r.V[n])).ToList();
            if (vals.Count == 0) continue;
            units.TryGetValue(n, out var unit);
            var min = vals.MinBy(x => x.Val); var max = vals.MaxBy(x => x.Val);
            sb.AppendLine($"\n{n}{(string.IsNullOrEmpty(unit) ? "" : $" ({unit})")}: min {Fmt(min.Val)} at {min.T:MM-dd HH:mm}, " +
                          $"avg {Fmt(vals.Average(x => x.Val))}, max {Fmt(max.Val)} at {max.T:MM-dd HH:mm}, last {Fmt(vals[^1].Val)}");
            var series = vals.GroupBy(x => (int)Math.Min(buckets - 1, (x.T - rows[0].T).TotalMinutes / bucketMin))
                .OrderBy(g => g.Key).Select(g => $"{g.First().T:HH:mm}={Fmt(g.Average(x => x.Val))}");
            sb.AppendLine("  " + string.Join(" ", series));
        }
        return sb.ToString();
    }

    internal static string Fmt(double d) => Math.Abs(d) >= 100 ? d.ToString("0", CultureInfo.InvariantCulture) : d.ToString("0.##", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        foreach (var r in Runners()) r.Dispose();
        lock (_gate) _runners = new();
    }
}

/// <summary>Parsed form of a rule like "pv_power == 0 between 10:00-15:00 for 15m".</summary>
sealed class ParsedRule
{
    static readonly Regex Rx = new(
        @"^\s*(?<v>[A-Za-z_][\w.\-]*)\s*(?<op><=|>=|==|!=|<|>|=)\s*(?<n>[-+]?\d+(?:\.\d+)?)" +
        @"(?:\s+between\s+(?<a>\d{1,2}:\d{2})\s*(?:-|to|and)\s*(?<b>\d{1,2}:\d{2}))?" +
        @"(?:\s+for\s+(?<f>\d+)\s*(?:m|min|mins|minutes)?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Value = "", Op = "";
    public double Threshold;
    public TimeSpan? From, To;
    public int ForMinutes;

    public static ParsedRule Parse(string text)
    {
        var m = Rx.Match(text ?? "");
        if (!m.Success) throw new FormatException($"Can't parse rule '{text}'. Expected e.g. 'battery_soc < 30', 'temp > 38.5 for 5m', 'pv_power == 0 between 10:00-15:00'.");
        var r = new ParsedRule
        {
            Value = m.Groups["v"].Value,
            Op = m.Groups["op"].Value == "=" ? "==" : m.Groups["op"].Value,
            Threshold = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture),
            ForMinutes = m.Groups["f"].Success ? int.Parse(m.Groups["f"].Value, CultureInfo.InvariantCulture) : 0
        };
        if (m.Groups["a"].Success)
        {
            r.From = TimeSpan.ParseExact(m.Groups["a"].Value, @"h\:mm", CultureInfo.InvariantCulture);
            r.To = TimeSpan.ParseExact(m.Groups["b"].Value, @"h\:mm", CultureInfo.InvariantCulture);
        }
        return r;
    }

    public bool InWindow(DateTime now)
    {
        if (From is not TimeSpan a || To is not TimeSpan b) return true;
        var t = now.TimeOfDay;
        return a <= b ? t >= a && t < b : t >= a || t < b; // window may cross midnight
    }

    public bool Test(double v) => Op switch
    {
        "<" => v < Threshold,
        "<=" => v <= Threshold,
        ">" => v > Threshold,
        ">=" => v >= Threshold,
        "==" => Math.Abs(v - Threshold) < 1e-9,
        "!=" => Math.Abs(v - Threshold) >= 1e-9,
        _ => false
    };
}

sealed class MonitorRunner : IDisposable
{
    sealed class RuleState
    {
        public required MonitorRule Rule;
        public ParsedRule? Parsed;
        public string? Error;
        public DateTime? TrueSince;  // condition continuously true since
        public bool Alerted;        // an alert was sent for the current episode
        public DateTime? LastAlert;
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public MonitorSettings Settings { get; }
    readonly Action<string, string> _alert;
    readonly Dictionary<string, MonitorReading> _values = new(StringComparer.OrdinalIgnoreCase);
    readonly List<RuleState> _rules;
    readonly object _gate = new();
    readonly SemaphoreSlim _tick = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    readonly DateTime _started = DateTime.Now;
    IMqttClient? _mqtt;
    DateTime _nextMqttAttempt = DateTime.MinValue;
    Task? _loop;
    string? _lastError;
    DateTime? _lastData;
    bool _offlineAlerted;
    DateTime _lastLogMinute = DateTime.MinValue;
    string? _csvDate;
    string[]? _csvColumns;

    public MonitorRunner(MonitorSettings settings, Action<string, string> alert)
    {
        Settings = settings;
        _alert = alert;
        _rules = settings.Rules.Where(r => !string.IsNullOrWhiteSpace(r.When)).Select(r =>
        {
            var s = new RuleState { Rule = r };
            try { s.Parsed = ParsedRule.Parse(r.When); } catch (Exception ex) { s.Error = ex.Message; }
            return s;
        }).ToList();
    }

    public static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return s.Length == 0 ? "monitor" : s;
    }

    int Interval => Math.Clamp(Settings.IntervalSeconds, 5, 86400);
    string Kind => (Settings.Kind ?? "").Trim().ToLowerInvariant();

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    async Task LoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { lock (_gate) _lastError = ex.Message; }
            try { await Task.Delay(TimeSpan.FromSeconds(Interval), ct); } catch { return; }
        }
    }

    public async Task TickAsync(CancellationToken ct, bool force = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await _tick.WaitAsync(linked.Token);
        try
        {
            try
            {
                switch (Kind)
                {
                    case "mqtt": await EnsureMqttAsync(linked.Token, force); break;
                    case "http_json": await PollHttpAsync(linked.Token); break;
                    case "command": await PollCommandAsync(linked.Token); break;
                    default: throw new InvalidOperationException($"Unknown monitor kind '{Settings.Kind}' (use mqtt, http_json or command).");
                }
                lock (_gate) if (Kind != "mqtt") _lastError = null;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (Exception ex) { lock (_gate) _lastError = ex.Message; }

            Evaluate(DateTime.Now);
            LogCsv(DateTime.Now);
        }
        finally { _tick.Release(); }
    }

    // ---------------- sources ----------------

    async Task EnsureMqttAsync(CancellationToken ct, bool force)
    {
        if (_mqtt?.IsConnected == true) return;
        if (!force && DateTime.Now < _nextMqttAttempt) return;
        _nextMqttAttempt = DateTime.Now.AddSeconds(Math.Max(30, Interval));
        if (string.IsNullOrWhiteSpace(Settings.Host)) throw new InvalidOperationException("MQTT host is not set.");

        var factory = new MqttFactory();
        if (_mqtt == null)
        {
            _mqtt = factory.CreateMqttClient();
            _mqtt.ApplicationMessageReceivedAsync += e =>
            {
                try { OnMqttMessage(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString() ?? ""); } catch { }
                return Task.CompletedTask;
            };
        }
        var b = new MqttClientOptionsBuilder()
            .WithTcpServer(Settings.Host.Trim(), Settings.Port <= 0 ? 1883 : Settings.Port)
            .WithClientId($"thokolosi-{Environment.MachineName}-{SafeName(Settings.Name)}-{Guid.NewGuid().ToString("N")[..6]}")
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
            .WithTimeout(TimeSpan.FromSeconds(15));
        if (!string.IsNullOrWhiteSpace(Settings.Username)) b = b.WithCredentials(Settings.Username, Settings.Password ?? "");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await _mqtt.ConnectAsync(b.Build(), timeout.Token);

        var topics = Settings.Values.Select(v => v.Source.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        if (topics.Count == 0) throw new InvalidOperationException("No MQTT topics configured in Values.");
        var sub = factory.CreateSubscribeOptionsBuilder();
        foreach (var t in topics) sub = sub.WithTopicFilter(f => f.WithTopic(t));
        await _mqtt.SubscribeAsync(sub.Build(), timeout.Token);
        lock (_gate) _lastError = null;
    }

    void OnMqttMessage(string topic, string payload)
    {
        var now = DateTime.Now;
        foreach (var v in Settings.Values)
        {
            if (!TopicMatches(v.Source.Trim(), topic)) continue;
            var raw = payload.Trim();
            if (!string.IsNullOrWhiteSpace(v.Path))
            {
                try { raw = JsonPath(JsonNode.Parse(raw), v.Path)?.ToString() ?? ""; } catch { continue; }
                if (raw.Length == 0) continue;
            }
            Set(v.Name, raw, v.Unit, now);
        }
    }

    static bool TopicMatches(string filter, string topic)
    {
        if (filter == topic) return true;
        var f = filter.Split('/'); var t = topic.Split('/');
        for (int i = 0; i < f.Length; i++)
        {
            if (f[i] == "#") return true;
            if (i >= t.Length) return false;
            if (f[i] != "+" && f[i] != t[i]) return false;
        }
        return f.Length == t.Length;
    }

    async Task PollHttpAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Settings.Url)) throw new InvalidOperationException("URL is not set.");
        using var resp = await Http.GetAsync(Settings.Url.Trim(), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {Tools.Json.Truncate(text, 200)}");
        ApplyJson(JsonNode.Parse(text), DateTime.Now);
    }

    async Task PollCommandAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Settings.Command)) throw new InvalidOperationException("Command is not set.");
        var script = "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8\n" + Settings.Command;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start PowerShell.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(Interval, 10, 120)));
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("Command timed out.");
        }
        var output = (await stdout).Trim();
        var err = (await stderr).Trim();
        if (output.Length == 0) throw new InvalidOperationException("Command printed nothing." + (err.Length > 0 ? " Error: " + Tools.Json.Truncate(err, 300) : ""));

        var now = DateTime.Now;
        if (output.StartsWith('{') || output.StartsWith('['))
        {
            ApplyJson(JsonNode.Parse(output), now);
            return;
        }
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var i = line.IndexOfAny(new[] { '=', ':' });
            if (i > 0) pairs[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        if (Settings.Values.Count == 0)
            foreach (var kv in pairs) Set(kv.Key, kv.Value, "", now);
        else
            foreach (var v in Settings.Values)
                if (pairs.TryGetValue(string.IsNullOrWhiteSpace(v.Source) ? v.Name : v.Source.Trim(), out var raw)) Set(v.Name, raw, v.Unit, now);
    }

    void ApplyJson(JsonNode? root, DateTime now)
    {
        if (root == null) throw new InvalidOperationException("Empty JSON.");
        if (Settings.Values.Count == 0)
        {
            if (root is JsonObject o)
                foreach (var kv in o)
                    if (kv.Value is JsonValue jv) Set(kv.Key, jv.ToString(), "", now);
            return;
        }
        foreach (var v in Settings.Values)
        {
            var node = JsonPath(root, string.IsNullOrWhiteSpace(v.Source) ? v.Name : v.Source);
            if (node is JsonValue val) Set(v.Name, val.ToString(), v.Unit, now);
        }
    }

    /// <summary>Simple dotted path: "battery.soc", "data[0].temp", "data.0.temp".</summary>
    internal static JsonNode? JsonPath(JsonNode? node, string path)
    {
        foreach (Match m in Regex.Matches(path.Trim(), @"[^.\[\]]+|\[(\d+)\]"))
        {
            if (node == null) return null;
            if (m.Groups[1].Success)
            {
                node = node is JsonArray a && int.Parse(m.Groups[1].Value) is var i && i < a.Count ? a[i] : null;
                continue;
            }
            var key = m.Value;
            node = node switch
            {
                JsonObject o => o.TryGetPropertyValue(key, out var c) ? c
                    : o.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value,
                JsonArray a when int.TryParse(key, out var i) && i < a.Count => a[i],
                _ => null
            };
        }
        return node;
    }

    void Set(string name, string raw, string unit, DateTime now)
    {
        raw = raw.Trim().Trim('"');
        lock (_gate)
        {
            _values[name] = new MonitorReading(name, raw, ToNumber(raw), unit, now);
            _lastData = now;
        }
    }

    static double? ToNumber(string raw)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return raw.ToLowerInvariant() switch
        {
            "on" or "true" or "yes" or "online" => 1,
            "off" or "false" or "no" or "offline" => 0,
            _ => null
        };
    }

    // ---------------- rules ----------------

    TimeSpan FreshFor => TimeSpan.FromSeconds(Math.Max(Interval * 3, 300));

    void Evaluate(DateTime now)
    {
        var alerts = new List<(string Title, string Text)>();
        lock (_gate)
        {
            // Offline / no-data alert.
            if (Settings.OfflineAlertMinutes > 0)
            {
                var since = _lastData ?? _started;
                bool offline = now - since > TimeSpan.FromMinutes(Settings.OfflineAlertMinutes);
                if (offline && !_offlineAlerted)
                {
                    _offlineAlerted = true;
                    alerts.Add(($"{Settings.Name}: no data", $"No readings for {(int)(now - since).TotalMinutes} min." + (_lastError != null ? $" Last error: {_lastError}" : "")));
                }
                else if (!offline && _offlineAlerted && _lastData != null)
                {
                    _offlineAlerted = false;
                    alerts.Add(($"{Settings.Name}: back online", "Readings are arriving again."));
                }
            }

            foreach (var s in _rules)
            {
                if (s.Parsed is not ParsedRule p) continue;
                if (!_values.TryGetValue(p.Value, out var reading) || reading.Number is not double v || now - reading.Time > FreshFor)
                    continue; // missing or stale value: leave state as is

                if (!p.InWindow(now))
                {
                    // Outside the time window the rule doesn't apply; reset quietly (no misleading "back to normal").
                    s.TrueSince = null; s.Alerted = false;
                    continue;
                }

                if (p.Test(v))
                {
                    s.TrueSince ??= now;
                    if (now - s.TrueSince.Value < TimeSpan.FromMinutes(p.ForMinutes)) continue;
                    var cooldown = TimeSpan.FromMinutes(Math.Max(1, s.Rule.CooldownMinutes));
                    if (s.LastAlert != null && now - s.LastAlert.Value < cooldown) continue; // in cooldown: no alert (and no recovery for a suppressed episode)
                    var repeat = s.Alerted ? " (still)" : "";
                    s.Alerted = true;
                    s.LastAlert = now;
                    var title = $"{(s.Rule.Urgent ? "URGENT - " : "")}{Settings.Name}: {p.Value} {MonitorService.Fmt(v)}{reading.Unit}{repeat}";
                    var text = (string.IsNullOrWhiteSpace(s.Rule.Message) ? "" : s.Rule.Message.Trim() + " ") + $"(rule: {s.Rule.When})";
                    alerts.Add((title, text));
                }
                else
                {
                    if (s.Alerted && s.Rule.NotifyRecovery)
                        alerts.Add(($"{Settings.Name}: {p.Value} back to normal", $"{p.Value} is {MonitorService.Fmt(v)}{reading.Unit} (rule was: {s.Rule.When})."));
                    s.TrueSince = null; s.Alerted = false;
                }
            }
        }
        foreach (var (title, text) in alerts) _alert(title, text);
    }

    // ---------------- logging ----------------

    void LogCsv(DateTime now)
    {
        try
        {
            var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
            Dictionary<string, MonitorReading> snap;
            lock (_gate)
            {
                if (minute <= _lastLogMinute) return;
                snap = _values.Where(kv => now - kv.Value.Time <= FreshFor).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                if (snap.Count == 0) return;
                _lastLogMinute = minute;
            }
            var dir = Path.Combine(MonitorService.RootDir, SafeName(Settings.Name));
            Directory.CreateDirectory(dir);
            var date = now.ToString("yyyy-MM-dd");
            var file = Path.Combine(dir, date + ".csv");
            if (_csvDate != date || _csvColumns == null)
            {
                _csvDate = date;
                if (File.Exists(file))
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs);
                    _csvColumns = (sr.ReadLine() ?? "time").Split(',').Skip(1).ToArray();
                }
                else
                {
                    _csvColumns = Settings.Values.Count > 0 ? Settings.Values.Select(v => Clean(v.Name)).ToArray()
                        : snap.Keys.Select(Clean).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
                    File.WriteAllText(file, "time," + string.Join(",", _csvColumns) + "\n");
                }
            }
            var sb = new StringBuilder(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            foreach (var c in _csvColumns)
            {
                sb.Append(',');
                if (snap.TryGetValue(c, out var r)) sb.Append(r.Number is double d ? d.ToString(CultureInfo.InvariantCulture) : Clean(r.Raw));
            }
            File.AppendAllText(file, sb.Append('\n').ToString());
        }
        catch { /* logging must never stop monitoring */ }
    }

    static string Clean(string s) => s.Replace(",", " ").Replace("\n", " ").Replace("\r", "");

    // ---------------- status ----------------

    public MonitorStatus Status()
    {
        lock (_gate)
        {
            var values = Settings.Values.Count > 0
                ? Settings.Values.Select(v => _values.TryGetValue(v.Name, out var r) ? r : new MonitorReading(v.Name, "", null, v.Unit, DateTime.MinValue)).ToList()
                : _values.Values.OrderBy(v => v.Name).ToList();
            bool connected = Kind == "mqtt" ? _mqtt?.IsConnected == true : _lastError == null && _lastData != null;
            return new MonitorStatus(Settings.Name, Settings.Kind, Settings.Enabled, connected, _lastData, _lastError, values,
                _rules.Select(r => new RuleStatus(r.Rule.When, r.Alerted, r.LastAlert, r.Error)).ToList());
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        var c = _mqtt;
        _mqtt = null;
        if (c != null)
        {
            _ = Task.Run(async () =>
            {
                try { if (c.IsConnected) await c.DisconnectAsync(); } catch { }
                c.Dispose();
            });
        }
    }
}
