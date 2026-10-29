using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Thokolosi;

/// <summary>
/// Full-screen snipping overlay: freezes the screen, lets you drag a rectangle, and returns the region as a PNG file.
/// </summary>
public sealed class SnipWindow : Window
{
    readonly Drawing.Bitmap _shot;
    readonly Drawing.Rectangle _area;   // virtual screen in physical pixels
    readonly Canvas _canvas = new();
    readonly Rectangle _sel = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x7B, 0x5C, 0xFF)), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(30, 0x7B, 0x5C, 0xFF)) };
    Point _start;
    bool _dragging;
    readonly TaskCompletionSource<string?> _result = new();

    SnipWindow(Drawing.Bitmap shot, Drawing.Rectangle area)
    {
        _shot = shot;
        _area = area;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        AllowsTransparency = false;
        Cursor = Cursors.Cross;
        Background = Brushes.Black;

        var img = new Image { Source = ToBitmapSource(shot), Stretch = Stretch.Fill, Opacity = 0.55 };
        _canvas.Children.Add(img);
        _canvas.Children.Add(_sel);
        _canvas.Children.Add(new TextBlock
        {
            Text = "Drag to snip an area for Thokolosi  -  Esc to cancel",
            Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(180, 0x15, 0x12, 0x1D)),
            Padding = new Thickness(10, 6, 10, 6), FontSize = 14, Margin = new Thickness(20)
        });
        Content = _canvas;
        _sel.Visibility = Visibility.Collapsed;

        Loaded += (_, _) =>
        {
            // Cover the whole virtual screen (convert physical pixels to DIPs).
            var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var tl = m.Transform(new Point(_area.Left, _area.Top));
            var br = m.Transform(new Point(_area.Right, _area.Bottom));
            Left = tl.X; Top = tl.Y; Width = br.X - tl.X; Height = br.Y - tl.Y;
            img.Width = Width; img.Height = Height;
            Activate();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(_canvas); _dragging = true; CaptureMouse(); Update(_start); _sel.Visibility = Visibility.Visible; };
        MouseMove += (_, e) => { if (_dragging) Update(e.GetPosition(_canvas)); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            Finish(Crop(_start, e.GetPosition(_canvas)));
        };
        MouseRightButtonUp += (_, _) => Finish(null);
    }

    /// <summary>Capture the screen, let the user pick a region, and return a PNG path (or null if cancelled).</summary>
    public static Task<string?> SnipAsync()
    {
        var area = WinForms.SystemInformation.VirtualScreen;
        var shot = new Drawing.Bitmap(area.Width, area.Height);
        using (var g = Drawing.Graphics.FromImage(shot))
            g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
        var w = new SnipWindow(shot, area);
        w.Show();
        return w._result.Task;
    }

    void Update(Point p)
    {
        Canvas.SetLeft(_sel, Math.Min(p.X, _start.X));
        Canvas.SetTop(_sel, Math.Min(p.Y, _start.Y));
        _sel.Width = Math.Abs(p.X - _start.X);
        _sel.Height = Math.Abs(p.Y - _start.Y);
    }

    string? Crop(Point a, Point b)
    {
        // DIPs on the canvas -> pixels in the captured bitmap
        double sx = _shot.Width / ActualWidth, sy = _shot.Height / ActualHeight;
        int x = (int)(Math.Min(a.X, b.X) * sx), y = (int)(Math.Min(a.Y, b.Y) * sy);
        int w = (int)(Math.Abs(a.X - b.X) * sx), h = (int)(Math.Abs(a.Y - b.Y) * sy);
        if (w < 8 || h < 8) return null;
        x = Math.Clamp(x, 0, _shot.Width - 1); y = Math.Clamp(y, 0, _shot.Height - 1);
        w = Math.Min(w, _shot.Width - x); h = Math.Min(h, _shot.Height - y);
        using var crop = _shot.Clone(new Drawing.Rectangle(x, y, w, h), _shot.PixelFormat);
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "thokolosi");
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"snip-{DateTime.Now:HHmmss}.png");
        crop.Save(path, Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    bool _finished;

    void Finish(string? path)
    {
        if (_finished) return;
        _finished = true;
        _result.TrySetResult(path);
        Close();
        _shot.Dispose();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_finished) { _finished = true; _result.TrySetResult(null); _shot.Dispose(); }
        base.OnClosed(e);
    }

    static BitmapSource ToBitmapSource(Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, Drawing.Imaging.ImageFormat.Bmp);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
