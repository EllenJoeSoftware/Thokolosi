using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Thokolosi.Core.Tools;

/// <summary>
/// A real web browser driven by Playwright. Uses Microsoft Edge (always installed on Windows) with its own
/// Thokolosi profile, so sites you sign into once stay signed in. Headless in Silent mode, visible otherwise.
/// </summary>
public class BrowserTool : ITool, IAsyncDisposable
{
    IPlaywright? _pw;
    IBrowserContext? _context;
    IPage? _page;
    bool _headless;
    readonly SemaphoreSlim _gate = new(1, 1);

    public string Name => "browser";

    public JsonObject Definition { get; } = Json.Def("browser",
        "Control a real web browser (Microsoft Edge) to visit sites, read pages, click, type, and take screenshots. " +
        "The browser keeps its own profile, so logins persist between tasks. " +
        "Typical flow: navigate -> read_page (gives page text plus a list of clickable elements) -> click / fill -> read_page again. " +
        "Selectors use Playwright syntax: CSS ('#search', 'input[name=q]'), text ('text=Sign in'), or role ('role=button[name=\"Search\"]').",
        """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["navigate", "read_page", "click", "fill", "press", "scroll", "screenshot", "back", "new_tab", "close"],
              "description": "navigate: open url. read_page: page text and interactive elements. click: click selector. fill: type text into selector. press: keyboard key such as Enter (selector optional). scroll: scroll down (or up with text='up'). screenshot: image of the page. back: go back. new_tab: open url in a new tab. close: close the browser."
            },
            "url": { "type": "string" },
            "selector": { "type": "string" },
            "text": { "type": "string", "description": "Text for fill, key name for press, or 'up' for scroll." }
          },
          "required": ["action"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => input.Str("action") is "click" or "fill" or "press";

    public string Describe(JsonObject input)
    {
        var a = input.Str("action");
        return a switch
        {
            "navigate" or "new_tab" => $"Browser: opening {input.Str("url")}",
            "click" => $"Browser: clicking {input.Str("selector")}",
            "fill" => $"Browser: typing into {input.Str("selector")}",
            "press" => $"Browser: pressing {input.Str("text")}",
            _ => $"Browser: {a}"
        };
    }

    public async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var action = input.Str("action");
            if (action == "close") { await CloseAsync(); return ToolResult.Text("Browser closed."); }

            var page = await EnsurePageAsync(ctx.Silent);
            var selector = input.Str("selector");
            var text = input.Str("text");
            const float t = 15000;

            switch (action)
            {
                case "navigate":
                    await page.GotoAsync(Normalize(input.Str("url")), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
                    await Settle(page);
                    return await ReadPage(page);

                case "new_tab":
                    _page = await _context!.NewPageAsync();
                    await _page.GotoAsync(Normalize(input.Str("url")), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
                    await Settle(_page);
                    return await ReadPage(_page);

                case "read_page":
                    return await ReadPage(page);

                case "click":
                    await page.Locator(selector).First.ClickAsync(new() { Timeout = t });
                    await Settle(page);
                    return ToolResult.Text($"Clicked. Now on: {await page.TitleAsync()} ({page.Url})");

                case "fill":
                    await page.Locator(selector).First.FillAsync(text, new() { Timeout = t });
                    return ToolResult.Text("Filled.");

                case "press":
                    if (string.IsNullOrEmpty(selector)) await page.Keyboard.PressAsync(text);
                    else await page.Locator(selector).First.PressAsync(text, new() { Timeout = t });
                    await Settle(page);
                    return ToolResult.Text($"Pressed {text}. Now on: {await page.TitleAsync()} ({page.Url})");

                case "scroll":
                    await page.Mouse.WheelAsync(0, text == "up" ? -900 : 900);
                    await page.WaitForTimeoutAsync(400);
                    return ToolResult.Text("Scrolled.");

                case "back":
                    await page.GoBackAsync();
                    await Settle(page);
                    return await ReadPage(page);

                case "screenshot":
                    var png = await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = 75 });
                    return ToolResult.Text($"Screenshot of {page.Url}").AddImage(png, "image/jpeg");

                default:
                    return ToolResult.Error($"Unknown action '{action}'.");
            }
        }
        catch (PlaywrightException ex)
        {
            return ToolResult.Error("Browser error: " + ex.Message.Split('\n')[0]);
        }
        catch (TimeoutException ex)
        {
            return ToolResult.Error("Browser timeout: " + ex.Message.Split('\n')[0]);
        }
        finally { _gate.Release(); }
    }

    static string Normalize(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("file:")
            ? url : "https://" + url;

    static async Task Settle(IPage page)
    {
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 }); } catch { }
    }

    const string ElementsJs = """
        () => {
          const els = [...document.querySelectorAll('a,button,input,textarea,select,[role=button],[role=link],[role=tab],[role=menuitem]')];
          const out = [];
          for (const e of els) {
            const r = e.getBoundingClientRect();
            if (r.width === 0 || r.height === 0) continue;
            const label = (e.innerText || e.value || e.placeholder || e.getAttribute('aria-label') || e.title || e.name || e.id || '')
              .trim().replace(/\s+/g, ' ').slice(0, 80);
            if (!label) continue;
            let hint = e.tagName.toLowerCase();
            if (e.id) hint += '#' + e.id;
            else if (e.name) hint += '[name="' + e.name + '"]';
            const type = e.getAttribute('type');
            out.push(hint + (type ? ' (' + type + ')' : '') + ': ' + label);
            if (out.length >= 80) break;
          }
          return out.join('\n');
        }
        """;

    static async Task<ToolResult> ReadPage(IPage page)
    {
        var title = await page.TitleAsync();
        string body, elements;
        try { body = await page.InnerTextAsync("body", new() { Timeout = 5000 }); } catch { body = "(could not read page text)"; }
        try { elements = await page.EvaluateAsync<string>(ElementsJs); } catch { elements = ""; }
        var sb = new StringBuilder();
        sb.AppendLine($"Title: {title}");
        sb.AppendLine($"URL: {page.Url}");
        sb.AppendLine("--- Page text ---");
        sb.AppendLine(Json.Truncate(body.Trim(), 15000));
        sb.AppendLine("--- Interactive elements ---");
        sb.AppendLine(elements);
        return ToolResult.Text(sb.ToString());
    }

    async Task<IPage> EnsurePageAsync(bool headless)
    {
        if (_context != null && _headless != headless) await CloseAsync(); // mode switched: relaunch
        if (_context == null)
        {
            _pw ??= await Playwright.CreateAsync();
            _headless = headless;
            var opts = new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = headless,
                Channel = "msedge",
                ViewportSize = new ViewportSize { Width = 1280, Height = 860 },
            };
            try
            {
                _context = await _pw.Chromium.LaunchPersistentContextAsync(AppConfig.BrowserProfileDir, opts);
            }
            catch (PlaywrightException)
            {
                // Edge not found: download Playwright's own Chromium once, then use that.
                await Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
                opts.Channel = null;
                _context = await _pw.Chromium.LaunchPersistentContextAsync(AppConfig.BrowserProfileDir, opts);
            }
            _context.Page += (_, p) => _page = p; // follow popups / new tabs
            _context.Close += (_, _) => { _context = null; _page = null; };
            _page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
        }
        if (_page == null || _page.IsClosed)
            _page = _context.Pages.LastOrDefault(p => !p.IsClosed) ?? await _context.NewPageAsync();
        return _page;
    }

    public async Task CloseAsync()
    {
        var c = _context;
        _context = null; _page = null;
        if (c != null) { try { await c.CloseAsync(); } catch { } }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _pw?.Dispose();
    }
}
