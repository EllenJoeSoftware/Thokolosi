using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Thokolosi.Core.Tools;

/// <summary>
/// Runs PowerShell on the PC. Silent mode: hidden process, output captured.
/// Visible mode: a real PowerShell window opens so you can watch, output is mirrored to a file.
/// </summary>
public class PowerShellTool : ITool
{
    public string Name => "run_powershell";

    public JsonObject Definition { get; } = Json.Def("run_powershell",
        "Run a PowerShell (Windows PowerShell 5.1) script on the user's PC and return its combined output. " +
        "Use for anything on the computer: files, apps (Start-Process), git, winget installs, system info, registry, services, networking. " +
        "Commands must be non-interactive (never wait for keyboard input). Long-running servers should be started with Start-Process so they do not block.",
        """
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "PowerShell script to run. May be multiple lines." },
            "working_directory": { "type": "string", "description": "Folder to run in. Defaults to the user's profile folder." },
            "timeout_seconds": { "type": "integer", "description": "Kill the command after this many seconds. Default 120, max 1800." }
          },
          "required": ["command"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => true;

    public string Describe(JsonObject input) => "PowerShell> " + input.Str("command").Trim();

    public async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var command = input.Str("command");
        var timeout = Math.Clamp(input.Int("timeout_seconds", 120), 5, 1800);
        var workDir = input.Str("working_directory");
        if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
            workDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var tmp = Path.Combine(Path.GetTempPath(), "thokolosi");
        Directory.CreateDirectory(tmp);
        var id = Guid.NewGuid().ToString("N")[..10];
        var scriptPath = Path.Combine(tmp, id + ".ps1");
        var outPath = Path.Combine(tmp, id + ".out.txt");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));

        try
        {
            return ctx.Silent
                ? await RunHidden(command, scriptPath, workDir, timeout, timeoutCts.Token, ct)
                : await RunVisible(command, scriptPath, outPath, workDir, timeout, timeoutCts.Token, ct);
        }
        finally
        {
            TryDelete(scriptPath);
            TryDelete(outPath);
        }
    }

    const string Prelude = "$ProgressPreference = 'SilentlyContinue'\r\n[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\r\n";

    async Task<ToolResult> RunHidden(string command, string scriptPath, string workDir, int timeout, CancellationToken token, CancellationToken userCt)
    {
        File.WriteAllText(scriptPath, Prelude + command + "\r\nexit $LASTEXITCODE\r\n", new UTF8Encoding(true));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"")
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Stop build tools leaving server processes behind that hold our output pipes open.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        psi.Environment["UseSharedCompilation"] = "false";

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Exited += (_, _) => exited.TrySetResult();
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        // Wait for the process itself, not for its output pipes: background children (servers, build nodes)
        // can keep the pipes open long after the command has finished.
        if (p.HasExited) exited.TrySetResult();
        try { await exited.Task.WaitAsync(token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (userCt.IsCancellationRequested) throw;
            return ToolResult.Error($"Timed out after {timeout}s and was stopped.\n{Json.Truncate(sb.ToString(), 20000)}");
        }
        p.WaitForExit(2000); // brief drain of buffered output

        string all;
        lock (sb) all = sb.ToString();
        var output = Json.Truncate(all.TrimEnd(), 30000);
        return ToolResult.Text($"Exit code {p.ExitCode}\n{output}", p.ExitCode != 0);
    }

    async Task<ToolResult> RunVisible(string command, string scriptPath, string outPath, string workDir, int timeout, CancellationToken token, CancellationToken userCt)
    {
        var outEsc = outPath.Replace("'", "''");
        var script = new StringBuilder()
            .Append(Prelude)
            .Append("$Host.UI.RawUI.WindowTitle = 'Thokolosi is working'\r\n")
            .Append("Write-Host 'Thokolosi is running:' -ForegroundColor Magenta\r\n")
            .Append("Write-Host @'\r\n").Append(SafeForHereString(command)).Append("\r\n'@ -ForegroundColor DarkGray\r\n")
            .Append("Write-Host ('-' * 60) -ForegroundColor DarkGray\r\n")
            .Append("& {\r\n").Append(command).Append("\r\n} *>&1 | Tee-Object -FilePath '").Append(outEsc).Append("' | Out-Host\r\n")
            .Append("$__code = $LASTEXITCODE\r\n")
            .Append("Write-Host ''\r\nWrite-Host 'Done. Closing in 3 seconds...' -ForegroundColor DarkGray\r\nStart-Sleep -Seconds 3\r\n")
            .Append("exit $__code\r\n");
        File.WriteAllText(scriptPath, script.ToString(), new UTF8Encoding(true));

        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
        {
            WorkingDirectory = workDir,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        };
        using var p = Process.Start(psi)!;
        try { await p.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (userCt.IsCancellationRequested) throw;
            return ToolResult.Error($"Timed out after {timeout}s and was stopped.\n{ReadOut(outPath)}");
        }
        var output = Json.Truncate(ReadOut(outPath).TrimEnd(), 30000);
        return ToolResult.Text($"Exit code {p.ExitCode}\n{output}", p.ExitCode != 0);
    }

    // A line starting with '@ would end the here-string early, so indent such lines for display.
    static string SafeForHereString(string s) =>
        string.Join("\r\n", s.Replace("\r\n", "\n").Split('\n').Select(l => l.StartsWith("'@") ? " " + l : l));

    static string ReadOut(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; } catch { return ""; }
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
