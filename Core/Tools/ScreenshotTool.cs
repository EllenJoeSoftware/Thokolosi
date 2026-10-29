using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json.Nodes;
using WinForms = System.Windows.Forms;

namespace Thokolosi.Core.Tools;

/// <summary>Captures the user's screen so Claude can see what's on it.</summary>
public class ScreenshotTool : ITool
{
    public string Name => "screenshot_screen";

    public JsonObject Definition { get; } = Json.Def("screenshot_screen",
        "Take a screenshot of the user's whole desktop (all monitors) to see what is currently on screen.",
        """{ "type": "object", "properties": {} }""");

    public bool NeedsApproval(JsonObject input) => false;
    public string Describe(JsonObject input) => "Looking at the screen";

    public Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var bytes = Capture(1568);
        var r = ToolResult.Text("Screenshot of the desktop:");
        r.AddImage(bytes, "image/jpeg");
        return Task.FromResult(r);
    }

    public static byte[] Capture(int maxEdge)
    {
        var area = WinForms.SystemInformation.VirtualScreen;
        using var full = new Bitmap(area.Width, area.Height);
        using (var g = Graphics.FromImage(full))
            g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);

        double scale = Math.Min(1.0, (double)maxEdge / Math.Max(area.Width, area.Height));
        int w = (int)(area.Width * scale), h = (int)(area.Height * scale);
        using var small = new Bitmap(w, h);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, w, h);
        }

        using var ms = new MemoryStream();
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
        small.Save(ms, jpeg, ep);
        return ms.ToArray();
    }
}
