using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Thokolosi.Core.Tools;

/// <summary>Settings for the Facebook Page tool. The page access token is DPAPI-encrypted on disk.</summary>
public class FacebookSettings
{
    /// <summary>Numeric Facebook Page id.</summary>
    public string PageId { get; set; } = "";
    public string? PageAccessTokenProtected { get; set; }

    /// <summary>Long-lived Page access token (needs pages_manage_posts, pages_read_engagement, pages_manage_engagement).</summary>
    [JsonIgnore]
    public string? PageAccessToken
    {
        get => Secret.Unprotect(PageAccessTokenProtected);
        set => PageAccessTokenProtected = Secret.Protect(value);
    }
}

/// <summary>Manage a Facebook Page through the Graph API: read posts/comments, publish or schedule posts, reply to comments.</summary>
public class FacebookTool(Func<FacebookSettings> settings) : ITool
{
    public const string GraphVersion = "v21.0";
    static readonly string Base = $"https://graph.facebook.com/{GraphVersion}/";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public string Name => "facebook";

    public JsonObject Definition { get; } = Json.Def("facebook",
        "Manage the user's Facebook Page. Actions: page_info, recent_posts, post (message, optional link, optional local image path, " +
        "optional schedule_at local time 10 min to 30 days ahead), scheduled_posts, comments (post_id), reply_comment (comment_id, message). " +
        "Confirm wording with the user before posting unless told otherwise.",
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["page_info", "recent_posts", "post", "scheduled_posts", "comments", "reply_comment"] },
            "message": { "type": "string", "description": "post/reply_comment: text" },
            "link": { "type": "string", "description": "post: URL to attach (with an image it is appended to the caption)" },
            "image_path": { "type": "string", "description": "post: local image file to upload as a photo post" },
            "schedule_at": { "type": "string", "description": "post: ISO local time to publish, e.g. 2026-11-02T07:30" },
            "post_id": { "type": "string", "description": "comments: post id (from recent_posts)" },
            "comment_id": { "type": "string", "description": "reply_comment: comment id (from comments)" },
            "limit": { "type": "integer", "description": "items to return (default 10)" }
          },
          "required": ["action"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => input.Str("action") is "post" or "reply_comment";

    public string Describe(JsonObject input) => input.Str("action") switch
    {
        "post" => $"Facebook: {(input.Str("schedule_at").Length > 0 ? $"scheduling post for {input.Str("schedule_at")}" : "publishing post")}" +
                  $"{(input.Str("image_path").Length > 0 ? $" with image {Path.GetFileName(input.Str("image_path"))}" : "")}\n\n" +
                  Json.Truncate(input.Str("message"), 800) + (input.Str("link").Length > 0 ? "\n" + input.Str("link") : ""),
        "reply_comment" => $"Facebook: replying to comment {input.Str("comment_id")}\n\n{Json.Truncate(input.Str("message"), 800)}",
        "comments" => $"Facebook: reading comments on {input.Str("post_id")}",
        var a => $"Facebook: {a}"
    };

    public async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var s = settings();
        var token = s.PageAccessToken;
        if (string.IsNullOrWhiteSpace(s.PageId) || string.IsNullOrEmpty(token))
            return ToolResult.Error("Facebook Page id and Page access token are not set. The user can add them in Settings > Integrations.");
        var page = s.PageId.Trim();
        int limit = Math.Clamp(input.Int("limit", 10), 1, 100);

        switch (input.Str("action"))
        {
            case "page_info":
            {
                var r = await Get(token, $"{page}?fields=name,category,fan_count,followers_count,link,about,website", ct);
                return ToolResult.Text($"{r["name"]} ({r["category"]}) - {r["followers_count"] ?? r["fan_count"]} followers, {r["fan_count"]} likes\n" +
                                       $"{r["link"]}\n{r["about"]}".TrimEnd());
            }
            case "recent_posts":
            {
                var r = await Get(token, $"{page}/posts?limit={limit}&fields=id,message,created_time,permalink_url,shares," +
                                         "comments.summary(true).limit(0),reactions.summary(true).limit(0)", ct);
                var sb = new StringBuilder();
                foreach (var p in r["data"] as JsonArray ?? new JsonArray())
                {
                    sb.AppendLine($"[{When(p!["created_time"])}] id {p["id"]} | {p["reactions"]?["summary"]?["total_count"] ?? 0} reactions, " +
                                  $"{p["comments"]?["summary"]?["total_count"] ?? 0} comments, {p["shares"]?["count"] ?? 0} shares");
                    sb.AppendLine("  " + OneLine(p["message"]?.ToString(), 300));
                }
                return ToolResult.Text(sb.Length == 0 ? "No posts." : Json.Truncate(sb.ToString(), 30000));
            }
            case "scheduled_posts":
            {
                var r = await Get(token, $"{page}/scheduled_posts?limit={limit}&fields=id,message,scheduled_publish_time,created_time", ct);
                var sb = new StringBuilder();
                foreach (var p in r["data"] as JsonArray ?? new JsonArray())
                {
                    var at = p!["scheduled_publish_time"] is JsonNode n && long.TryParse(n.ToString(), out var unix)
                        ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("ddd yyyy-MM-dd HH:mm")
                        : When(p["scheduled_publish_time"]);
                    sb.AppendLine($"[{at}] id {p["id"]}: {OneLine(p["message"]?.ToString(), 300)}");
                }
                return ToolResult.Text(sb.Length == 0 ? "No scheduled posts." : Json.Truncate(sb.ToString(), 30000));
            }
            case "comments":
            {
                var post = input.Str("post_id").Trim();
                if (post.Length == 0) return ToolResult.Error("post_id is required.");
                var r = await Get(token, $"{Uri.EscapeDataString(post)}/comments?limit={limit}&order=reverse_chronological&filter=stream" +
                                         "&fields=id,from,message,created_time,like_count,comment_count,parent{id}", ct);
                var sb = new StringBuilder();
                foreach (var c in r["data"] as JsonArray ?? new JsonArray())
                {
                    var reply = c!["parent"] != null ? $" (reply to {c["parent"]?["id"]})" : "";
                    sb.AppendLine($"[{When(c["created_time"])}] {c["from"]?["name"] ?? "(hidden user)"}{reply}: {OneLine(c["message"]?.ToString(), 500)}" +
                                  $"  (comment_id {c["id"]}, {c["like_count"] ?? 0} likes, {c["comment_count"] ?? 0} replies)");
                }
                return ToolResult.Text(sb.Length == 0 ? "No comments." : Json.Truncate(sb.ToString(), 30000));
            }
            case "reply_comment":
            {
                var id = input.Str("comment_id").Trim();
                var msg = input.Str("message");
                if (id.Length == 0 || string.IsNullOrWhiteSpace(msg)) return ToolResult.Error("comment_id and message are required.");
                var r = await Post(token, $"{Uri.EscapeDataString(id)}/comments", new FormUrlEncodedContent(new Dictionary<string, string> { ["message"] = msg }), ct);
                return ToolResult.Text($"Replied (comment id {r["id"]}).");
            }
            case "post":
                return await Publish(token, page, input, ct);
            default:
                return ToolResult.Error("Unknown action.");
        }
    }

    static async Task<ToolResult> Publish(string token, string page, JsonObject input, CancellationToken ct)
    {
        var message = input.Str("message");
        var link = input.Str("link").Trim();
        var image = Environment.ExpandEnvironmentVariables(input.Str("image_path").Trim());
        var scheduleText = input.Str("schedule_at").Trim();
        if (string.IsNullOrWhiteSpace(message) && link.Length == 0 && image.Length == 0)
            return ToolResult.Error("Nothing to post: give a message, link or image_path.");

        long? scheduleUnix = null;
        if (scheduleText.Length > 0)
        {
            if (!DateTimeOffset.TryParse(scheduleText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at))
                return ToolResult.Error($"Could not parse schedule_at '{scheduleText}'. Use ISO local time like 2026-11-02T07:30.");
            var ahead = at - DateTimeOffset.Now;
            if (ahead < TimeSpan.FromMinutes(10) || ahead > TimeSpan.FromDays(30))
                return ToolResult.Error($"schedule_at must be between 10 minutes and 30 days from now (it is {ahead.TotalHours:0.#} hours away).");
            scheduleUnix = at.ToUnixTimeSeconds();
        }

        JsonObject r;
        if (image.Length > 0)
        {
            if (!File.Exists(image)) return ToolResult.Error($"Image not found: {image}");
            var caption = link.Length > 0 ? $"{message}\n\n{link}".Trim() : message;
            using var form = new MultipartFormDataContent();
            var bytes = await File.ReadAllBytesAsync(image, ct);
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(Path.GetExtension(image).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/jpeg"
            });
            form.Add(file, "source", Path.GetFileName(image));
            if (!string.IsNullOrWhiteSpace(caption)) form.Add(new StringContent(caption), "caption");
            if (scheduleUnix is long su)
            {
                form.Add(new StringContent("false"), "published");
                form.Add(new StringContent(su.ToString(CultureInfo.InvariantCulture)), "scheduled_publish_time");
            }
            r = await Post(token, $"{page}/photos", form, ct);
        }
        else
        {
            var p = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(message)) p["message"] = message;
            if (link.Length > 0) p["link"] = link;
            if (scheduleUnix is long su)
            {
                p["published"] = "false";
                p["scheduled_publish_time"] = su.ToString(CultureInfo.InvariantCulture);
            }
            r = await Post(token, $"{page}/feed", new FormUrlEncodedContent(p), ct);
        }

        var id = r["post_id"] ?? r["id"];
        var when = scheduleUnix is long u ? $"Scheduled for {DateTimeOffset.FromUnixTimeSeconds(u).LocalDateTime:ddd yyyy-MM-dd HH:mm}" : "Published";
        return ToolResult.Text($"{when} (id {id}).");
    }

    static async Task<JsonObject> Get(string token, string pathAndQuery, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + pathAndQuery);
        return await Send(req, token, ct);
    }

    static async Task<JsonObject> Post(string token, string path, HttpContent content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + path) { Content = content };
        return await Send(req, token, ct);
    }

    static async Task<JsonObject> Send(HttpRequestMessage req, string token, CancellationToken ct)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonObject? body = null;
        try { body = JsonNode.Parse(text) as JsonObject; } catch { }
        if (body?["error"] is JsonObject err)
        {
            var code = err["code"]?.ToString();
            var hint = code switch
            {
                "190" => " (the Page access token is invalid or expired - generate a new long-lived Page token)",
                "200" or "10" => " (the token is missing a permission such as pages_manage_posts / pages_manage_engagement)",
                "100" => " (check the id or parameters)",
                "368" => " (Facebook temporarily blocked this action - try again later)",
                _ => ""
            };
            throw new InvalidOperationException($"Facebook API error {code}: {err["message"]}{hint}");
        }
        if (!resp.IsSuccessStatusCode || body == null)
            throw new InvalidOperationException($"Facebook API HTTP {(int)resp.StatusCode}: {Json.Truncate(text, 500)}");
        return body;
    }

    static string When(JsonNode? n)
    {
        var s = n?.ToString();
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            : s ?? "?";
    }

    static string OneLine(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(no text)";
        s = s.Replace("\r", "").Replace("\n", " / ");
        return s.Length <= max ? s : s[..max] + "...";
    }
}
