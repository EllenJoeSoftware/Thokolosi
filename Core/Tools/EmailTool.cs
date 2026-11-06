using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Thokolosi.Core.Tools;

/// <summary>Any IMAP/SMTP mailbox (Gmail with an app password, Outlook.com, cPanel/company mail, ...).</summary>
public class EmailTool : ITool
{
    public string Name => "email";

    public JsonObject Definition { get; } = Json.Def("email",
        "Read and send email through the user's configured IMAP/SMTP accounts. " +
        "Actions: accounts, folders, list (recent or unread), search, read (by uid), send (new or reply), mark_read. " +
        "Always show the user a draft and get confirmation before 'send' unless their job description says otherwise.",
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["accounts", "folders", "list", "search", "read", "send", "mark_read"] },
            "account": { "type": "string", "description": "Account name or address. Default: first account." },
            "folder": { "type": "string", "description": "Folder name, default INBOX." },
            "unread_only": { "type": "boolean" },
            "limit": { "type": "integer", "description": "Max messages for list/search (default 20)." },
            "query": { "type": "string", "description": "search: text to find in subject, body or sender." },
            "since_days": { "type": "integer", "description": "search: only messages from the last N days." },
            "uid": { "type": "integer", "description": "read/mark_read: message uid from list/search. send: uid of message to reply to (optional)." },
            "to": { "type": "string", "description": "send: comma-separated recipients (not needed for replies)." },
            "cc": { "type": "string" },
            "subject": { "type": "string" },
            "body": { "type": "string", "description": "send: plain-text body." },
            "attachments": { "type": "array", "items": { "type": "string" }, "description": "send: file paths to attach." }
          },
          "required": ["action"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => input.Str("action") is "send" or "mark_read";

    public string Describe(JsonObject input) => input.Str("action") switch
    {
        "send" => $"Email: sending '{input.Str("subject")}' to {input.Str("to", "(reply)")}\n\n{Json.Truncate(input.Str("body"), 800)}",
        "search" => $"Email: searching for '{input.Str("query")}'",
        "read" => $"Email: reading message {input.Str("uid")}",
        var a => $"Email: {a}"
    };

    public async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var accounts = ctx.Config.EmailAccounts;
        var action = input.Str("action");
        if (action == "accounts")
            return ToolResult.Text(accounts.Count == 0
                ? "No email accounts configured. The user can add one in Settings > Integrations."
                : string.Join("\n", accounts.Select(a => $"{a.Name}: {a.Address} ({a.ImapHost})")));
        if (accounts.Count == 0) return ToolResult.Error("No IMAP email account is set up. Try the outlook tool or MCP email tools, or ask the user to add an account in Settings > Integrations.");

        var want = input.Str("account");
        var acct = accounts.FirstOrDefault(a => a.Name.Equals(want, StringComparison.OrdinalIgnoreCase) || a.Address.Equals(want, StringComparison.OrdinalIgnoreCase)) ?? accounts[0];
        if (string.IsNullOrEmpty(acct.Password)) return ToolResult.Error($"No password saved for {acct.Address}.");

        if (action == "send") return await SendAsync(acct, input, ct);

        using var imap = new ImapClient();
        await imap.ConnectAsync(acct.ImapHost, acct.ImapPort, SecureSocketOptions.Auto, ct);
        var loginPassword = acct.RequirePassword();
            await imap.AuthenticateAsync(string.IsNullOrEmpty(acct.Username) ? acct.Address : acct.Username, loginPassword, ct);
        try
        {
            if (action == "folders")
            {
                var ns = imap.PersonalNamespaces.FirstOrDefault();
                var all = ns == null ? new List<IMailFolder>() : (await imap.GetFoldersAsync(ns, cancellationToken: ct)).ToList();
                return ToolResult.Text(string.Join("\n", all.Select(f => f.FullName)));
            }

            var folderName = input.Str("folder", "INBOX");
            var folder = string.IsNullOrEmpty(folderName) || folderName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
                ? imap.Inbox : await imap.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(action == "mark_read" ? FolderAccess.ReadWrite : FolderAccess.ReadOnly, ct);
            int limit = Math.Clamp(input.Int("limit", 20), 1, 100);

            switch (action)
            {
                case "list":
                {
                    var uids = await folder.SearchAsync(input.Bool("unread_only") ? SearchQuery.NotSeen : SearchQuery.All, ct);
                    return ToolResult.Text(await Summaries(folder, uids.Reverse().Take(limit).ToList(), ct, $"{acct.Address} / {folder.FullName}"));
                }
                case "search":
                {
                    var q = input.Str("query");
                    SearchQuery sq = string.IsNullOrWhiteSpace(q)
                        ? SearchQuery.All
                        : SearchQuery.SubjectContains(q).Or(SearchQuery.BodyContains(q)).Or(SearchQuery.FromContains(q));
                    var days = input.Int("since_days", 0);
                    if (days > 0) sq = sq.And(SearchQuery.DeliveredAfter(DateTime.Today.AddDays(-days)));
                    if (input.Bool("unread_only")) sq = sq.And(SearchQuery.NotSeen);
                    var uids = await folder.SearchAsync(sq, ct);
                    return ToolResult.Text(await Summaries(folder, uids.Reverse().Take(limit).ToList(), ct, $"Search '{q}' in {folder.FullName}"));
                }
                case "read":
                {
                    var msg = await folder.GetMessageAsync(new UniqueId((uint)input.Int("uid", 0)), ct);
                    return ToolResult.Text(Format(msg));
                }
                case "mark_read":
                    await folder.AddFlagsAsync(new UniqueId((uint)input.Int("uid", 0)), MessageFlags.Seen, true, ct);
                    return ToolResult.Text("Marked as read.");
                default:
                    return ToolResult.Error($"Unknown action '{action}'.");
            }
        }
        finally { await imap.DisconnectAsync(true, CancellationToken.None); }
    }

    static async Task<string> Summaries(IMailFolder folder, IList<UniqueId> uids, CancellationToken ct, string header)
    {
        if (uids.Count == 0) return header + "\n(no messages)";
        var items = await folder.FetchAsync(uids, MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.UniqueId | MessageSummaryItems.PreviewText, ct);
        var sb = new StringBuilder(header + "\n");
        foreach (var m in items.OrderByDescending(m => m.Date))
        {
            var unread = m.Flags.HasValue && !m.Flags.Value.HasFlag(MessageFlags.Seen) ? " [UNREAD]" : "";
            var from = m.Envelope?.From?.ToString() ?? "(unknown sender)";        // the envelope is always requested, but be safe
            var subject = m.Envelope?.Subject ?? "(no subject)";
            sb.AppendLine($"uid {m.UniqueId.Id} | {m.Date.LocalDateTime:yyyy-MM-dd HH:mm} | {from} | {subject}{unread}");
            if (!string.IsNullOrWhiteSpace(m.PreviewText)) sb.AppendLine("    " + Json.Truncate(m.PreviewText.Trim(), 160));
        }
        return sb.ToString();
    }

    static string Format(MimeMessage msg)
    {
        var body = msg.TextBody ?? HtmlToText(msg.HtmlBody ?? "");
        var sb = new StringBuilder();
        sb.AppendLine($"From: {msg.From}");
        sb.AppendLine($"To: {msg.To}");
        if (msg.Cc.Count > 0) sb.AppendLine($"Cc: {msg.Cc}");
        sb.AppendLine($"Date: {msg.Date.LocalDateTime:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Subject: {msg.Subject}");
        var att = msg.Attachments.Select(a => a.ContentDisposition?.FileName ?? a.ContentType.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (att.Count > 0) sb.AppendLine("Attachments: " + string.Join(", ", att));
        sb.AppendLine();
        sb.Append(Json.Truncate(body.Trim(), 30000));
        return sb.ToString();
    }

    public static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>|</p>|</div>|</tr>|</li>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        return Regex.Replace(s, @"\n\s*\n\s*\n+", "\n\n");
    }

    static async Task<ToolResult> SendAsync(EmailAccount acct, JsonObject input, CancellationToken ct)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(acct.Name, acct.Address));
        var subject = input.Str("subject");
        var replyUid = input.Int("uid", 0);

        if (replyUid > 0)
        {
            using var imap = new ImapClient();
            await imap.ConnectAsync(acct.ImapHost, acct.ImapPort, SecureSocketOptions.Auto, ct);
            var loginPassword = acct.RequirePassword();
            await imap.AuthenticateAsync(string.IsNullOrEmpty(acct.Username) ? acct.Address : acct.Username, loginPassword, ct);
            var folderName = input.Str("folder", "INBOX");
            var folder = folderName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? imap.Inbox : await imap.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            var orig = await folder.GetMessageAsync(new UniqueId((uint)replyUid), ct);
            await imap.DisconnectAsync(true, ct);

            msg.InReplyTo = orig.MessageId;
            foreach (var r in orig.References) msg.References.Add(r);
            if (!string.IsNullOrEmpty(orig.MessageId)) msg.References.Add(orig.MessageId);
            msg.To.AddRange(orig.ReplyTo.Count > 0 ? orig.ReplyTo : orig.From);
            if (string.IsNullOrWhiteSpace(subject))
                subject = orig.Subject?.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) == true ? orig.Subject : "Re: " + orig.Subject;
        }

        var to = input.Str("to");
        if (!string.IsNullOrWhiteSpace(to)) msg.To.AddRange(InternetAddressList.Parse(to));
        var cc = input.Str("cc");
        if (!string.IsNullOrWhiteSpace(cc)) msg.Cc.AddRange(InternetAddressList.Parse(cc));
        if (msg.To.Count == 0) return ToolResult.Error("No recipient. Give 'to' or a 'uid' to reply to.");
        msg.Subject = subject;

        var builder = new BodyBuilder { TextBody = input.Str("body") };
        foreach (var a in input["attachments"] as JsonArray ?? new JsonArray())
        {
            var path = Environment.ExpandEnvironmentVariables(a!.GetValue<string>());
            if (!File.Exists(path)) return ToolResult.Error($"Attachment not found: {path}");
            await builder.Attachments.AddAsync(path, ct);
        }
        msg.Body = builder.ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(acct.SmtpHost, acct.SmtpPort, SecureSocketOptions.Auto, ct);
        var sendPassword = acct.RequirePassword();
        await smtp.AuthenticateAsync(string.IsNullOrEmpty(acct.Username) ? acct.Address : acct.Username, sendPassword, ct);
        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(true, ct);
        return ToolResult.Text($"Sent '{msg.Subject}' to {msg.To}.");
    }
}

