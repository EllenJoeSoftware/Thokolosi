using System.Text.Json.Nodes;

namespace Thokolosi.Core.Tools;

/// <summary>Access to the saved-passwords vault (Settings > Passwords). Passwords are encrypted at rest; get and delete follow the normal Ask/Auto approval mode.</summary>
public class SecretsTool : ITool
{
    public string Name => "secrets";

    public JsonObject Definition { get; } = Json.Def("secrets",
        "Saved logins (passwords, app passwords, API keys) stored encrypted for this Windows user; the user manages them in Settings > Passwords. " +
        "Actions: list (names, usernames and notes only, never passwords), get (returns the username and password for one entry; asks the user first when Auto mode is off; " +
        "use it only for the task at hand, never print, repeat, log or store it anywhere else, and prefer passing it straight into a script), " +
        "save (add or update an entry the user has just told you to store), delete (asks the user first when Auto mode is off).",
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["list", "get", "save", "delete"] },
            "name": { "type": "string", "description": "Entry name, e.g. 'gmail-mohapiw'" },
            "username": { "type": "string", "description": "save: login or email" },
            "password": { "type": "string", "description": "save: the password or key to store" },
            "notes": { "type": "string", "description": "save: what it is for" }
          },
          "required": ["action"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => input.Str("action") is "get" or "delete";

    public string Describe(JsonObject input) => input.Str("action") switch
    {
        "list" => "Listing saved password names",
        "get" => $"Reading saved password '{input.Str("name")}'",
        "save" => $"Saving password '{input.Str("name")}'",
        "delete" => $"Deleting saved password '{input.Str("name")}'",
        _ => "Saved passwords",
    };

    public Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            var name = input.Str("name");
            switch (input.Str("action"))
            {
                case "list":
                    var all = Vault.Load();
                    return Task.FromResult(ToolResult.Text(all.Count == 0
                        ? "No saved passwords yet. Add them in Settings > Passwords."
                        : string.Join("\n", all.Select(e => $"- {e.Name} | user: {(e.Username.Length == 0 ? "(none)" : e.Username)} | {e.Notes} | updated {e.Updated:yyyy-MM-dd}"))));
                case "get":
                {
                    var e = Vault.Find(name);
                    if (e == null) return Task.FromResult(ToolResult.Error($"No saved password named '{name}'. Use action list."));
                    var pw = Vault.GetPassword(name);
                    if (string.IsNullOrEmpty(pw)) return Task.FromResult(ToolResult.Error("The entry exists but could not be decrypted (it may have been saved by another Windows user)."));
                    return Task.FromResult(ToolResult.Text($"user: {e.Username}\npassword: {pw}\n(Do not repeat or store this.)"));
                }
                case "save":
                    if (string.IsNullOrWhiteSpace(name)) return Task.FromResult(ToolResult.Error("name is required."));
                    Vault.Set(name, input.Str("username"), input.Str("password"), input.Str("notes"));
                    return Task.FromResult(ToolResult.Text($"Saved '{name}' (encrypted)."));
                case "delete":
                    return Task.FromResult(Vault.Remove(name) ? ToolResult.Text($"Deleted '{name}'.") : ToolResult.Error($"No saved password named '{name}'."));
                default:
                    return Task.FromResult(ToolResult.Error("Unknown action."));
            }
        }
        catch (Exception ex) { return Task.FromResult(ToolResult.Error(ex.Message)); }
    }
}

