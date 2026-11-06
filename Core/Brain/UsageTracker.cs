using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Thokolosi.Core.Brain;

/// <summary>Price per million tokens (USD). Edit in config.json to match your Anthropic console.</summary>
public class ModelPrice
{
    public double Input { get; set; }
    public double Output { get; set; }
    public double CacheWrite { get; set; }
    public double CacheRead { get; set; }
}

public class BudgetSettings
{
    /// <summary>Monthly spending cap in USD (0 = no cap).</summary>
    public double MonthlyUsd { get; set; } = 0;

    /// <summary>What to do at the cap: "warn", "cheaper" (switch to the cheapest model), "local" (offline model), or "pause".</summary>
    public string AtCap { get; set; } = "cheaper";

    /// <summary>Estimated prices; keys match the start of the model name. Check console.anthropic.com for your actual rates.</summary>
    public Dictionary<string, ModelPrice> Prices { get; set; } = new()
    {
        ["claude-opus"] = new() { Input = 15, Output = 75, CacheWrite = 18.75, CacheRead = 1.5 },
        ["claude-sonnet"] = new() { Input = 3, Output = 15, CacheWrite = 3.75, CacheRead = 0.3 },
        ["claude-haiku"] = new() { Input = 1, Output = 5, CacheWrite = 1.25, CacheRead = 0.1 },
    };

    /// <summary>USD per web search.</summary>
    public double WebSearchUsd { get; set; } = 0.01;
}

/// <summary>Records token usage of every Claude call and estimates spend per day/month.</summary>
public static class UsageTracker
{
    static readonly object Gate = new();
    static string _month = "";
    static double _monthUsd, _todayUsd;
    static DateTime _today;
    static bool _warned80, _warnedCap;

    public static AppConfig? Config { get; set; }
    public static event Action<string, string>? Alert; // title, text
    public static event Action? Changed;

    static string Dir => Path.Combine(AppConfig.Dir, "usage");

    public static double MonthUsd { get { lock (Gate) { Load(); return _monthUsd; } } }
    public static double TodayUsd { get { lock (Gate) { Load(); return DateTime.Today == _today ? _todayUsd : 0; } } }

    public static void Record(string model, JsonNode? usage, string source)
    {
        if (usage == null || Config == null) return;
        long Get(string k) => usage[k] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
        long input = Get("input_tokens"), output = Get("output_tokens"),
             cacheWrite = Get("cache_creation_input_tokens"), cacheRead = Get("cache_read_input_tokens");
        long searches = usage["server_tool_use"]?["web_search_requests"] is JsonValue s && s.TryGetValue<long>(out var w) ? w : 0;

        var b = Config.Budget;
        var price = b.Prices.Where(kv => model.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).FirstOrDefault()
                    ?? b.Prices.Values.FirstOrDefault() ?? new ModelPrice();
        if (OpenCodeGo.Handles(model)) price = new ModelPrice(); // flat OpenCode Go subscription: no per-token cost
        double usd = (input * price.Input + output * price.Output + cacheWrite * price.CacheWrite + cacheRead * price.CacheRead) / 1_000_000
                     + searches * b.WebSearchUsd;

        lock (Gate)
        {
            Load();
            _monthUsd += usd;
            if (_today != DateTime.Today) { _today = DateTime.Today; _todayUsd = 0; }
            _todayUsd += usd;
            try
            {
                Directory.CreateDirectory(Dir);
                var line = JsonSerializer.Serialize(new { t = DateTime.Now, model, source, input, output, cacheWrite, cacheRead, searches, usd = Math.Round(usd, 5) });
                File.AppendAllText(Path.Combine(Dir, $"{DateTime.Now:yyyy-MM}.jsonl"), line + "\n");
            }
            catch { }
        }
        Changed?.Invoke();
        CheckWarnings();
    }

    static void Load()
    {
        var month = DateTime.Now.ToString("yyyy-MM");
        if (_month == month) return;
        _month = month;
        _monthUsd = 0; _todayUsd = 0; _today = DateTime.Today;
        _warned80 = _warnedCap = false;
        var path = Path.Combine(Dir, month + ".jsonl");
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                var n = JsonNode.Parse(line);
                var usd = n?["usd"]?.GetValue<double>() ?? 0;
                _monthUsd += usd;
                if (n?["t"]?.GetValue<DateTime>().Date == DateTime.Today) _todayUsd += usd;
            }
            catch { }
        }
    }

    static void CheckWarnings()
    {
        var cap = Config?.Budget.MonthlyUsd ?? 0;
        if (cap <= 0) return;
        var spent = MonthUsd;
        if (!_warned80 && spent >= cap * 0.8 && spent < cap)
        {
            _warned80 = true;
            Alert?.Invoke("Thokolosi budget", $"80% of this month's budget used (${spent:0.00} of ${cap:0.00}).");
        }
        if (!_warnedCap && spent >= cap)
        {
            _warnedCap = true;
            var what = Config!.Budget.AtCap switch
            {
                "pause" => "Thokolosi is paused until next month (raise the budget in Settings to continue).",
                "local" => "Switching to the offline model.",
                "warn" => "Continuing (warn-only mode).",
                _ => "Switching to the cheapest model."
            };
            Alert?.Invoke("Thokolosi budget reached", $"${spent:0.00} of ${cap:0.00} this month. {what}");
        }
    }

    /// <summary>Over the monthly cap?</summary>
    public static bool OverCap => (Config?.Budget.MonthlyUsd ?? 0) > 0 && MonthUsd >= Config!.Budget.MonthlyUsd;

    public static string Summary()
    {
        var cap = Config?.Budget.MonthlyUsd ?? 0;
        return $"Today ${TodayUsd:0.00} · this month ${MonthUsd:0.00}" + (cap > 0 ? $" of ${cap:0.00}" : "") + " (estimate)";
    }
}
