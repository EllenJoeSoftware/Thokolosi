using System.Security.Cryptography;
using System.Text;

namespace Thokolosi.Core;

/// <summary>Encrypts secrets with Windows DPAPI: only this Windows user on this PC can decrypt them.</summary>
public static class Secret
{
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain.Trim()), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }
}
