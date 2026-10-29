using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using Thokolosi.Core.Remote;

namespace Thokolosi;

/// <summary>Shows a one-time QR code the phone app scans to pair with this PC.</summary>
public partial class PairWindow : Window
{
    readonly PhoneLink _link;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime _expires;

    public PairWindow(PhoneLink link)
    {
        InitializeComponent();
        _link = link;
        _link.PhonePaired += OnPaired;
        _timer.Tick += (_, _) => Tick();
        Loaded += async (_, _) => await NewCodeAsync();
    }

    async Task NewCodeAsync()
    {
        try
        {
            StatusText.Text = "Waiting for your phone...";
            var (qr, expires) = await _link.CreatePairingAsync();
            _expires = expires;
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(qr, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(10);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(png);
            img.EndInit();
            QrImage.Source = img;
            LoadingText.Visibility = Visibility.Collapsed;
            _timer.Start();
            Tick();
        }
        catch (Exception ex)
        {
            LoadingText.Text = "Couldn't create a code";
            StatusText.Text = ex.Message;
        }
    }

    void Tick()
    {
        var left = _expires - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            _timer.Stop();
            ExpiryText.Text = "Code expired - making a new one...";
            _ = NewCodeAsync();
            return;
        }
        ExpiryText.Text = $"Code expires in {left:m\\:ss}. It works once.";
    }

    void OnPaired(PairedPhone phone) => Dispatcher.BeginInvoke(async () =>
    {
        _timer.Stop();
        QrImage.Opacity = 0.15;
        StatusText.Text = $"✓ Paired with {phone.Name}";
        ExpiryText.Text = "You can now chat with Thokolosi and approve actions from your phone.";
        await Task.Delay(2500);
        Close();
    });

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _link.PhonePaired -= OnPaired;
        base.OnClosed(e);
    }
}
