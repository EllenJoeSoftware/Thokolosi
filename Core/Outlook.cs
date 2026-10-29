using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Thokolosi.Core;

public record CalendarEvent(string Id, string Subject, DateTime Start, DateTime End, string Location, string Organizer, string? TeamsUrl, string Body);

/// <summary>
/// Talks to classic desktop Outlook through COM. This works for work (Microsoft 365) accounts without any admin
/// consent, because it uses the Outlook app you're already signed in to. (The "new Outlook" app has no COM support.)
/// </summary>
public static class Outlook
{
    const int OlFolderInbox = 6, OlFolderSentMail = 5, OlFolderCalendar = 9, OlMailItem = 0, OlMail = 43;

    static readonly Regex TeamsLink = new(@"https://teams\.(microsoft|live)\.com/(l/meetup-join|meet)/[^\s""<>)\]]+", RegexOptions.IgnoreCase);

    public static dynamic App()
    {
        var type = Type.GetTypeFromProgID("Outlook.Application")
            ?? throw new InvalidOperationException("Classic Outlook is not installed on this PC (the 'new Outlook' app has no automation support). Use the email tool or Outlook on the web in the browser instead.");
        try { return Activator.CreateInstance(type)!; }
        catch (COMException ex) { throw new InvalidOperationException("Could not start Outlook: " + ex.Message); }
    }

    public static dynamic Session() => App().GetNamespace("MAPI");

    public static dynamic Folder(string name)
    {
        var ns = Session();
        return name.ToLowerInvariant() switch
        {
            "sent" or "sent items" => ns.GetDefaultFolder(OlFolderSentMail),
            "calendar" => ns.GetDefaultFolder(OlFolderCalendar),
            _ => ns.GetDefaultFolder(OlFolderInbox)
        };
    }

    public record MailSummary(string Id, DateTime Received, string From, string Subject, bool Unread, string Preview);

    public static List<MailSummary> ListMail(string folder, int limit, bool unreadOnly, string? query)
    {
        var f = Folder(folder);
        dynamic items = f.Items;
        items.Sort("[ReceivedTime]", true);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Replace("'", "''");
            items = items.Restrict($"@SQL=(\"urn:schemas:httpmail:subject\" like '%{q}%' OR \"urn:schemas:httpmail:textdescription\" like '%{q}%' OR \"urn:schemas:httpmail:fromname\" like '%{q}%')");
            items.Sort("[ReceivedTime]", true);
        }
        if (unreadOnly) { items = items.Restrict("[UnRead] = True"); items.Sort("[ReceivedTime]", true); }

        var list = new List<MailSummary>();
        int count = items.Count;
        for (int i = 1; i <= count && list.Count < limit; i++)
        {
            dynamic it = items.Item(i);
            if ((int)it.Class != OlMail) continue;
            string body = it.Body ?? "";
            list.Add(new MailSummary((string)it.EntryID, (DateTime)it.ReceivedTime, (string)(it.SenderName ?? ""), (string)(it.Subject ?? ""), (bool)it.UnRead,
                body.Length > 160 ? body[..160].Replace("\r", " ").Replace("\n", " ") : body.Replace("\r", " ").Replace("\n", " ")));
        }
        return list;
    }

    public static string ReadMail(string id, bool markRead)
    {
        dynamic it = Session().GetItemFromID(id);
        var atts = new List<string>();
        int n = it.Attachments.Count;
        for (int i = 1; i <= n; i++) atts.Add((string)it.Attachments.Item(i).FileName);
        if (markRead) it.UnRead = false;
        return $"From: {it.SenderName} <{SenderAddress(it)}>\nTo: {it.To}\nCc: {it.CC}\nDate: {(DateTime)it.ReceivedTime:yyyy-MM-dd HH:mm}\nSubject: {it.Subject}\n" +
               (atts.Count > 0 ? "Attachments: " + string.Join(", ", atts) + "\n" : "") + "\n" + (string)(it.Body ?? "");
    }

    static string SenderAddress(dynamic it)
    {
        try
        {
            if ((string)it.SenderEmailType == "EX") return (string)it.Sender.GetExchangeUser().PrimarySmtpAddress;
            return (string)it.SenderEmailAddress;
        }
        catch { return ""; }
    }

    public static string Send(string to, string cc, string subject, string body, IEnumerable<string> attachments, bool draftOnly, string? replyToId)
    {
        dynamic mail;
        if (!string.IsNullOrEmpty(replyToId))
        {
            dynamic orig = Session().GetItemFromID(replyToId);
            mail = orig.Reply();
            mail.Body = body + "\r\n\r\n" + (string)mail.Body;
            if (!string.IsNullOrWhiteSpace(cc)) mail.CC = cc;
        }
        else
        {
            mail = App().CreateItem(OlMailItem);
            mail.To = to;
            mail.CC = cc;
            mail.Subject = subject;
            mail.Body = body;
        }
        foreach (var a in attachments) mail.Attachments.Add(a);
        if (draftOnly) { mail.Save(); return "Saved to Drafts."; }
        mail.Send();
        return "Sent.";
    }

    public static List<CalendarEvent> Events(DateTime from, DateTime to)
    {
        dynamic items = Folder("calendar").Items;
        items.IncludeRecurrences = true;
        items.Sort("[Start]");
        // Outlook parses these dates using the PC's regional format, so use the current culture's "g" pattern.
        dynamic found = items.Restrict($"[Start] < '{to:g}' AND [End] > '{from:g}'");
        var list = new List<CalendarEvent>();
        // GetFirst/GetNext is the reliable way to walk a restricted, recurrence-expanded Items collection.
        for (dynamic? a = found.GetFirst(); a != null; a = found.GetNext())
        {
            string body = "";
            try { body = a.Body ?? ""; } catch { }
            string location = a.Location ?? "";
            var m = TeamsLink.Match(body + " " + location);
            DateTime start = a.Start, end = a.End;
            list.Add(new CalendarEvent(
                $"{(string)a.EntryID}|{start:yyyyMMddHHmm}",
                (string)(a.Subject ?? ""), start, end, location,
                (string)(a.Organizer ?? ""), m.Success ? m.Value : null, body));
            if (list.Count >= 200) break;
        }
        return list;
    }
}
