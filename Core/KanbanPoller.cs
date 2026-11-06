using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Thokolosi.Core;

/// <summary>
/// Watches the Easy Kanban board. Every 5 seconds it makes one cheap HTTP GET (no AI involved). Only when a new card
/// that Wycliffe himself created is waiting in a To Do column does it start a background agent run to work that card.
/// Connection details come from the "easy-kanban" entry in mcp.json (so no secret is copied anywhere else).
/// Safety: only cards created by a human user are worked; cards labelled getting-started/test are ignored; each card
/// is attempted at most twice per app run; one card at a time. Disable by setting "disabled": true on the easy-kanban
/// server in mcp.json (or removing it), then restarting Thokolosi.
/// </summary>
public sealed class KanbanPoller : IDisposable
{
    const int PollSeconds = 5;
    static readonly string[] IgnoreLabels = { "getting-started", "test" };

    readonly Func<string, string, Task> _run;
    readonly System.Threading.Timer _timer;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    readonly Dictionary<string, int> _attempts = new();
    readonly HashSet<string> _todoColumns = new();
    string? _url, _key, _myId;
    DateTime _columnsAt = DateTime.MinValue, _heartbeatAt = DateTime.MinValue;
    bool _wasDown;
    int _busy, _working;

    public KanbanPoller(Func<string, string, Task> runBackgroundTask)
    {
        _run = runBackgroundTask;
        if (!LoadSettings()) { ActivityLog.Write("KANBAN", "No easy-kanban server in mcp.json; board polling is off."); _timer = new System.Threading.Timer(_ => { }); return; }
        ActivityLog.Write("KANBAN", $"Polling {_url} every {PollSeconds}s.");
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(PollSeconds));
    }

    bool LoadSettings()
    {
        try
        {
            if (!File.Exists(AppConfig.McpPath)) return false;
            var servers = JsonNode.Parse(File.ReadAllText(AppConfig.McpPath))?["mcpServers"]?.AsObject();
            var s = servers?["easy-kanban"];
            if (s == null || s["disabled"]?.GetValue<bool>() == true) return false;
            _url = s["env"]?["EASY_KANBAN_URL"]?.GetValue<string>()?.TrimEnd('/');
            _key = s["env"]?["EASY_KANBAN_API_KEY"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(_url) || string.IsNullOrWhiteSpace(_key)) return false;
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            return true;
        }
        catch (Exception ex) { ActivityLog.Write("KANBAN", "Could not read mcp.json: " + ex.Message); return false; }
    }

    async Task<JsonNode?> GetAsync(string path)
    {
        using var r = await _http.GetAsync(_url + path);
        r.EnsureSuccessStatusCode();
        return JsonNode.Parse(await r.Content.ReadAsStringAsync());
    }

    async Task HeartbeatAsync()
    {
        _heartbeatAt = DateTime.Now;
        var body = new JsonObject { ["status"] = _working == 1 ? "working" : "idle", ["note"] = _working == 1 ? "Working a board card" : "Watching the board" };
        using var r = await _http.PostAsync(_url + "/api/agent/heartbeat", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        if (r.IsSuccessStatusCode)
            _myId = JsonNode.Parse(await r.Content.ReadAsStringAsync())?["id"]?.GetValue<string>() ?? _myId;
    }

    async Task RefreshColumnsAsync()
    {
        _columnsAt = DateTime.Now;
        var boards = (await GetAsync("/api/boards"))?.AsArray();
        if (boards == null) return;
        var ids = new HashSet<string>();
        foreach (var b in boards)
        {
            var id = b?["id"]?.GetValue<string>();
            if (id == null || b?["archived"]?.GetValue<bool>() == true) continue;
            var cols = (await GetAsync("/api/boards/" + id))?["columns"]?.AsArray();
            if (cols == null) continue;
            foreach (var c in cols)
                if (c?["kind"]?.GetValue<string>() == "todo" && c["id"]?.GetValue<string>() is { } cid) ids.Add(cid);
        }
        _todoColumns.Clear();
        foreach (var i in ids) _todoColumns.Add(i);
    }

    async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (_myId == null || DateTime.Now - _heartbeatAt > TimeSpan.FromSeconds(60)) await HeartbeatAsync();
            if (_todoColumns.Count == 0 || DateTime.Now - _columnsAt > TimeSpan.FromSeconds(60)) await RefreshColumnsAsync();
            if (_wasDown) { _wasDown = false; ActivityLog.Write("KANBAN", "Board is reachable again."); }

            if (Volatile.Read(ref _working) == 1) return;
            var cards = (await GetAsync("/api/agent/cards"))?.AsArray();
            if (cards == null) return;
            foreach (var c in cards)
            {
                if (c == null) continue;
                var id = c["id"]?.GetValue<string>();
                if (id == null || !_todoColumns.Contains(c["columnId"]?.GetValue<string>() ?? "")) continue;
                if (c["blocked"]?.GetValue<bool>() == true || c["archived"]?.GetValue<bool>() == true) continue;
                if (c["createdBy"]?["type"]?.GetValue<string>() != "user") continue;           // only cards a human wrote
                var assignee = c["assigneeId"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(assignee) && assignee != _myId) continue;               // someone else's card
                var labels = c["labels"]?.AsArray().Select(l => l?.GetValue<string>() ?? "").ToList() ?? new();
                if (labels.Any(l => IgnoreLabels.Contains(l, StringComparer.OrdinalIgnoreCase))) continue;
                if (_attempts.GetValueOrDefault(id) >= 2) continue;

                _attempts[id] = _attempts.GetValueOrDefault(id) + 1;
                Start(id, c["number"]?.ToString() ?? "?", c["title"]?.GetValue<string>() ?? "(untitled)");
                break;                                                                            // one card at a time
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!_wasDown) { _wasDown = true; ActivityLog.Write("KANBAN", "Board not reachable: " + ex.Message); }
        }
        catch (Exception ex) { ActivityLog.Write("KANBAN", "Poll error: " + ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    void Start(string id, string number, string title)
    {
        Volatile.Write(ref _working, 1);
        ActivityLog.Write("KANBAN", $"Card #{number} '{title}' is waiting; starting it.");
        var prompt =
            $"A card is waiting for you on the Easy Kanban board 'Agent Operations': card #{number}, id {id}, title: {title}. " +
            "It was created by Wycliffe on his own board. Steps: (1) get_card to read the full instructions, checklist and comments. " +
            "(2) move_card it to the In Progress column (use list_boards for the column id; do NOT use claim_next_card, it may pick another card), and set_status working. " +
            "(3) Do the work, using log_progress as you go and check_item for checklist items. " +
            "(4) Finish with complete_card (moveTo review) with a Markdown result, then set_status idle. " +
            "If anything is unclear, or needs his decision or approval (sending, posting, paying, deleting), use ask_question and stop. If you cannot do it, use fail_card. " +
            "All your usual rules and hard limits still apply. Keep your final summary to 2 or 3 lines.";
        _ = Task.Run(async () =>
        {
            try { await _run("Kanban #" + number + ": " + title, prompt); }
            catch (Exception ex) { ActivityLog.Write("KANBAN", "Run failed: " + ex.Message); }
            finally { Volatile.Write(ref _working, 0); _heartbeatAt = DateTime.MinValue; }
        });
    }

    public void Dispose() { _timer.Dispose(); _http.Dispose(); }
}
