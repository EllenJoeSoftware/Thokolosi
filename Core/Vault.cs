using System.IO;
using System.Text.Json;

namespace Thokolosi.Core;

/// <summary>One saved login. The password is stored encrypted (Windows DPAPI, current user only), never as plain text.</summary>
public class VaultEntry
{
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string Notes { get; set; } = "";
    public string SecretProtected { get; set; } = "";
    public DateTime Updated { get; set; } = DateTime.Now;
}

/// <summary>
/// Saved passwords. File: %APPDATA%\Thokolosi\secrets\vault.json. Only the password field is secret and it is encrypted with
/// <see cref="Secret"/> (DPAPI, this Windows user on this PC). Edited in Settings > Passwords; the agent can list names and,
/// read one through the "secrets" tool (approval follows Ask/Auto mode).
/// </summary>
public static class Vault
{
    static readonly object Lock = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath => System.IO.Path.Combine(AppConfig.Dir, "secrets", "vault.json");

    public static List<VaultEntry> Load()
    {
        lock (Lock)
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                return JsonSerializer.Deserialize<List<VaultEntry>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch (Exception ex) { ActivityLog.Write("VAULT", "Could not read vault: " + ex.Message); return new(); }
        }
    }

    static void Save(List<VaultEntry> list)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(), Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Adds or updates an entry. An empty password keeps the existing one when updating.</summary>
    public static void Set(string name, string username, string? password, string notes)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A name is required.");
        lock (Lock)
        {
            var list = Load();
            var e = list.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (e == null) { e = new VaultEntry { Name = name }; list.Add(e); }
            if (string.IsNullOrEmpty(password) && string.IsNullOrEmpty(e.SecretProtected))
                throw new ArgumentException("A password is required for a new entry.");
            e.Username = username.Trim();
            e.Notes = notes.Trim();
            if (!string.IsNullOrEmpty(password)) e.SecretProtected = Secret.Protect(password) ?? "";
            e.Updated = DateTime.Now;
            Save(list);
        }
        ActivityLog.Write("VAULT", $"Saved entry '{name}' (value not logged).");
    }

    public static bool Remove(string name)
    {
        lock (Lock)
        {
            var list = Load();
            var n = list.RemoveAll(x => x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (n > 0) Save(list);
            if (n > 0) ActivityLog.Write("VAULT", $"Removed entry '{name}'.");
            return n > 0;
        }
    }

    public static VaultEntry? Find(string name) =>
        Load().FirstOrDefault(x => x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string? GetPassword(string name) => Secret.Unprotect(Find(name)?.SecretProtected);
}

