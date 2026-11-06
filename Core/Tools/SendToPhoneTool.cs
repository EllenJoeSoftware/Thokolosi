using System.IO;
using System.Text.Json.Nodes;
using Thokolosi.Core.Remote;

namespace Thokolosi.Core.Tools;

/// <summary>Send a file from this PC to the user's paired phone(s).</summary>
public class SendToPhoneTool(Func<PhoneLink?> phone) : ITool
{
    public string Name => "send_to_phone";

    public JsonObject Definition { get; } = Json.Def("send_to_phone",
        "Send a file from this PC to the user's paired iPhone (it appears in the Thokolosi app, where they can open, save or share it). " +
        $"Up to {PhoneLink.MaxFileBytes / 1048576} MB. Use it when the user asks to send/get a file on their phone, or when a result " +
        "(report, export, photo, PDF) is more useful on the phone. Files the user sends FROM the phone arrive in %USERPROFILE%\\Downloads\\Thokolosi.",
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Full path of the file to send" },
            "message": { "type": "string", "description": "Optional short note shown with the file" }
          },
          "required": ["path"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => false; // only goes to the user's own paired phones

    public string Describe(JsonObject input) => $"Sending {Path.GetFileName(input.Str("path"))} to your phone";

    public async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var link = phone();
        if (link == null || !link.Connected)
            return ToolResult.Error("The phone link isn't connected (Settings > Phone). No file was sent.");
        var path = Environment.ExpandEnvironmentVariables(input.Str("path").Trim().Trim('"'));
        try
        {
            await link.SendFileAsync(path, input.Str("message"), null, ct);
            return ToolResult.Text($"Sent {Path.GetFileName(path)} ({new FileInfo(path).Length / 1024.0:0.#} KB) to the phone.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ToolResult.Error("Couldn't send: " + ex.Message); }
    }
}
