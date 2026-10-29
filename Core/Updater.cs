using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Thokolosi.Core;

public record UpdateInfo(Version Version, string Tag, string AssetApiUrl, string Notes);

/// <summary>
/// Self-update from GitHub Releases: looks for a newer release with a Thokolosi-win-x64.zip asset, downloads it,
/// and swaps the install folder through a small PowerShell script after Thokolosi exits.
/// </summary>
public static class Updater
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);

    public static async Task<UpdateInfo?> CheckAsync(UpdateSettings s, CancellationToken ct)
    {
        using var req = Request(s, $"https://api.github.com/repos/{s.Repo}/releases/latest", "application/vnd.github+json");
        using var resp = await Http.SendAsync(req, ct);
        if ((int)resp.StatusCode == 404)
            throw new InvalidOperationException(string.IsNullOrEmpty(s.Token)
                ? "No release found (if the repo is private, add a read-only GitHub token in Settings > Updates)."
                : "No release found yet.");
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
        var tag = json["tag_name"]?.GetValue<string>() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        var asset = (json["assets"] as JsonArray ?? new JsonArray())
            .FirstOrDefault(a => a?["name"]?.GetValue<string>()?.Equals("Thokolosi-win-x64.zip", StringComparison.OrdinalIgnoreCase) == true);
        if (asset == null) return null;
        var current = new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));
        if (v <= current) return null;
        return new UpdateInfo(v, tag, asset["url"]!.GetValue<string>(), json["body"]?.GetValue<string>() ?? "");
    }

    /// <summary>Download, unpack, and schedule the swap. Returns when Thokolosi should exit.</summary>
    public static async Task InstallAsync(UpdateSettings s, UpdateInfo info, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "thokolosi-update-" + info.Tag);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, "update.zip");

        using (var req = Request(s, info.AssetApiUrl, "application/octet-stream"))
        using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(zip);
            await resp.Content.CopyToAsync(fs, ct);
        }
        var unpack = Path.Combine(work, "files");
        ZipFile.ExtractToDirectory(zip, unpack);
        // zip layout: Thokolosi\..., install.ps1, uninstall.ps1
        var src = Directory.Exists(Path.Combine(unpack, "Thokolosi")) ? Path.Combine(unpack, "Thokolosi") : unpack;
        if (!File.Exists(Path.Combine(src, "Thokolosi.exe"))) throw new InvalidOperationException("The update package doesn't contain Thokolosi.exe.");

        var installDir = AppContext.BaseDirectory.TrimEnd('\\');
        var exe = Path.Combine(installDir, "Thokolosi.exe");
        var script = Path.Combine(work, "apply.ps1");
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $pid0 = {{Environment.ProcessId}}
            for ($i = 0; $i -lt 60 -and (Get-Process -Id $pid0 -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
            Get-Process Thokolosi -ErrorAction SilentlyContinue | Stop-Process -Force
            robocopy "{{src}}" "{{installDir}}" /MIR /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            Start-Process "{{exe}}" -ArgumentList '--tray','--updated'
            """);
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        ActivityLog.Write("UPDATE", $"Installing {info.Tag}; restarting.");
    }

    static HttpRequestMessage Request(UpdateSettings s, string url, string accept)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Thokolosi-Updater");
        req.Headers.Accept.ParseAdd(accept);
        if (!string.IsNullOrEmpty(s.Token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token);
        return req;
    }
}
