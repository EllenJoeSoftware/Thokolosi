using System.Text.Json.Nodes;

namespace Thokolosi.Core;

/// <summary>One-shot text transformations for the clipboard hotkey (result goes back on the clipboard).</summary>
public static class ClipboardActions
{
    public record Action(string Label, string Instruction);

    public static readonly Action[] Text =
    {
        new("Summarise", "Summarise this in a few short bullet points."),
        new("Rewrite clearly", "Rewrite this to be clearer and more concise. Keep the meaning and the language."),
        new("Make it professional", "Rewrite this in a polite, professional tone suitable for a client or employer."),
        new("Fix spelling && grammar", "Fix spelling, grammar and punctuation only. Keep the wording otherwise. Use South African English."),
        new("Draft a reply", "This is a message I received. Draft a short, friendly reply I can send. Leave [placeholders] for anything you don't know."),
        new("Explain", "Explain this simply in a few sentences."),
        new("Fix this code", "Fix bugs in this code. Return only the corrected code, no explanation, no markdown fences."),
        new("Translate to English", "Translate this to English."),
        new("Translate to Afrikaans", "Translate this to Afrikaans."),
        new("Translate to isiZulu", "Translate this to isiZulu."),
        new("Translate to Sesotho", "Translate this to Sesotho."),
    };

    public static async Task<string> RunAsync(AppConfig cfg, Action action, string text, CancellationToken ct)
    {
        var client = new ClaudeClient();
        var system = "You transform text the user copied. Output ONLY the resulting text - no preamble, no quotes, no explanations - " +
                     "so it can be pasted directly. Preserve formatting style (plain text stays plain).";
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = $"{action.Instruction}\n\n---\n{text}" } };
        var result = await client.CompleteAsync(cfg.ApiKey ?? throw new InvalidOperationException("No API key"),
            cfg.Model, system, content, 4000, ct, "clipboard");
        return result.Trim();
    }
}
