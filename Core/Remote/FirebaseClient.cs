using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Thokolosi.Core.Remote;

/// <summary>
/// Minimal Firebase client over plain REST: anonymous auth (identitytoolkit + securetoken) and
/// Realtime Database reads/writes/streams (Server-Sent Events). No Google SDK needed.
/// </summary>
public sealed class FirebaseClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly HttpClient StreamHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    readonly string _apiKey;
    readonly string _dbUrl;
    readonly SemaphoreSlim _authGate = new(1, 1);
    string? _idToken;
    DateTime _expires = DateTime.MinValue;

    public string? Uid { get; private set; }
    public string? RefreshToken { get; private set; }

    public FirebaseClient(string apiKey, string databaseUrl, string? uid, string? refreshToken)
    {
        _apiKey = apiKey.Trim();
        _dbUrl = databaseUrl.Trim().TrimEnd('/');
        Uid = uid;
        RefreshToken = refreshToken;
    }

    public static JsonObject ServerTimestamp => new() { [".sv"] = "timestamp" };

    // ---------- auth ----------

    public async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_idToken != null && DateTime.UtcNow < _expires) return _idToken;
        await _authGate.WaitAsync(ct);
        try
        {
            if (_idToken != null && DateTime.UtcNow < _expires) return _idToken;
            if (!string.IsNullOrEmpty(RefreshToken))
            {
                try
                {
                    using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = RefreshToken!
                    });
                    using var r = await Http.PostAsync($"https://securetoken.googleapis.com/v1/token?key={_apiKey}", form, ct);
                    var body = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
                    if (r.IsSuccessStatusCode)
                    {
                        _idToken = body!["id_token"]!.GetValue<string>();
                        RefreshToken = body["refresh_token"]!.GetValue<string>();
                        Uid = body["user_id"]!.GetValue<string>();
                        _expires = DateTime.UtcNow.AddSeconds(int.Parse(body["expires_in"]!.GetValue<string>()) - 120);
                        return _idToken;
                    }
                    // Only a dead identity justifies a new one; outages, quotas and captive portals must not
                    // change this PC's uid (that would orphan every pairing).
                    var err = body?["error"]?["message"]?.GetValue<string>() ?? "";
                    var fatal = new[] { "USER_NOT_FOUND", "INVALID_REFRESH_TOKEN", "TOKEN_EXPIRED", "USER_DISABLED" };
                    if (!fatal.Any(f => err.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                        throw new HttpRequestException($"Firebase token refresh failed ({(int)r.StatusCode} {err}); will retry.");
                    ActivityLog.Write("PHONE", "This PC's Firebase identity is gone (" + err + "); creating a new one - phones must pair again.");
                }
                catch (System.Text.Json.JsonException) { throw new HttpRequestException("Unexpected response refreshing the Firebase token; will retry."); }
            }

            // New anonymous identity (first run, or the old one was deleted).
            using var req = new StringContent("{\"returnSecureToken\":true}", Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync($"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={_apiKey}", req, ct);
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException("Firebase sign-in failed: " + (json?["error"]?["message"]?.GetValue<string>() ?? resp.StatusCode.ToString()) +
                                                    ". Check the API key and that Anonymous sign-in is enabled.");
            _idToken = json!["idToken"]!.GetValue<string>();
            RefreshToken = json["refreshToken"]!.GetValue<string>();
            Uid = json["localId"]!.GetValue<string>();
            _expires = DateTime.UtcNow.AddSeconds(int.Parse(json["expiresIn"]!.GetValue<string>()) - 120);
            return _idToken;
        }
        finally { _authGate.Release(); }
    }

    // ---------- database ----------

    async Task<string> Url(string path, CancellationToken ct, string query = "") =>
        $"{_dbUrl}/{path.Trim('/')}.json?auth={await TokenAsync(ct)}{query}";

    async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct, string query = "")
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, await Url(path, ct, query));
            if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            if ((int)resp.StatusCode == 401 && attempt == 0) { _expires = DateTime.MinValue; continue; } // token expired
            if ((int)resp.StatusCode >= 500 && attempt < 2) { await Task.Delay(1000 * (attempt + 1), ct); continue; }
            throw new InvalidOperationException($"Firebase {method} /{path} failed ({(int)resp.StatusCode}): {text}");
        }
    }

    public Task<JsonNode?> GetAsync(string path, CancellationToken ct, string query = "") => SendAsync(HttpMethod.Get, path, null, ct, query);
    public Task<JsonNode?> PutAsync(string path, JsonNode value, CancellationToken ct) => SendAsync(HttpMethod.Put, path, value, ct);
    public Task<JsonNode?> PatchAsync(string path, JsonObject value, CancellationToken ct) => SendAsync(HttpMethod.Patch, path, value, ct);
    public Task DeleteAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Delete, path, null, ct);

    /// <summary>Push a child with a generated key; returns the key.</summary>
    public async Task<string?> PushAsync(string path, JsonObject value, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Post, path, value, ct))?["name"]?.GetValue<string>();

    /// <summary>
    /// Listen to a path. Calls onEvent(eventType "put"/"patch", relativePath, data) for every change, starting with
    /// the full current value at "/". Reconnects automatically (tokens expire hourly) until ct is cancelled.
    /// </summary>
    public async Task ListenAsync(string path, Func<string, string, JsonNode?, Task> onEvent, CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, await Url(path, ct));
                req.Headers.Accept.ParseAdd("text/event-stream");
                using var resp = await StreamHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)resp.StatusCode == 401) { _expires = DateTime.MinValue; throw new IOException("unauthorised"); }
                resp.EnsureSuccessStatusCode();
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);
                string? evt = null;
                while (!ct.IsCancellationRequested)
                {
                    // Watchdog: Firebase sends keep-alives about every 30 s, so silence for 90 s means a dead
                    // (half-open) connection - typical after sleep/resume or a Wi-Fi change.
                    string? line;
                    using (var lineCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        lineCts.CancelAfter(TimeSpan.FromSeconds(90));
                        try { line = await reader.ReadLineAsync(lineCts.Token); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("stream went silent"); }
                    }
                    if (line == null) break;
                    if (line.StartsWith("event:")) { evt = line[6..].Trim(); continue; }
                    if (!line.StartsWith("data:")) continue;
                    var data = line[5..].Trim();
                    switch (evt)
                    {
                        case "put":
                        case "patch":
                            failures = 0; // only a delivered event proves the stream is healthy
                            var node = JsonNode.Parse(data);
                            await onEvent(evt, node?["path"]?.GetValue<string>() ?? "/", node?["data"]);
                            break;
                        case "auth_revoked":
                        case "cancel":
                            _expires = DateTime.MinValue;
                            throw new IOException("stream " + evt);
                        // keep-alive: ignore
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                failures++;
                if (failures <= 3 || failures % 20 == 0) ActivityLog.Write("PHONE", $"Stream /{path} reconnecting: {ex.Message}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Max(1, failures))), ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
