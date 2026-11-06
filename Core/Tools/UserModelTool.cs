using System.Text.Json.Nodes;
using Thokolosi.Core.Profile;

namespace Thokolosi.Core.Tools;

/// <summary>Lets the agent read the user model (what Thokolosi has learned about the user), and lets the user reset it.</summary>
public class UserModelTool : ITool
{
    public string Name => "user_model";

    public JsonObject Definition { get; } = Json.Def("user_model",
        "Thokolosi's growing model of the user: who he is, his work and projects, tools and sites, routines, preferences and better-way suggestions. " +
        "Actions: read (the full model), forget (reset it to the start; the user must ask for this and approve it), status (when it was last updated and whether building is on).",
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["read", "forget", "status"] }
          },
          "required": ["action"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => input.Str("action") == "forget";

    public string Describe(JsonObject input) => input.Str("action") switch
    {
        "forget" => "Resetting the user model",
        "status" => "Checking the user model",
        _ => "Reading the user model",
    };

    public Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            switch (input.Str("action"))
            {
                case "read":
                    return Task.FromResult(ToolResult.Text(UserModel.Read()));
                case "forget":
                    return Task.FromResult(ToolResult.Text(UserModel.Forget()
                        ? "The user model was reset. It will start learning again from the next update."
                        : "There was no saved model to reset."));
                case "status":
                    return Task.FromResult(ToolResult.Text(
                        $"Building: {(ctx.Config.Profile.Enabled ? "on" : "off")}, every {ctx.Config.Profile.IntervalMinutes} min. " +
                        $"Last updated: {UserModel.LastUpdated() ?? "never"}. Location: {UserModel.Path_}"));
                default:
                    return Task.FromResult(ToolResult.Error("Unknown action."));
            }
        }
        catch (Exception ex) { return Task.FromResult(ToolResult.Error(ex.Message)); }
    }
}
