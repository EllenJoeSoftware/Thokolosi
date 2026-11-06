using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Thokolosi.Core;

namespace Thokolosi;

/// <summary>
/// Shows chat text with clickable links. Recognises web addresses (https://...) and Markdown links [label](https://...).
/// Clicking a link opens it in the user's default browser. Use as an attached property on a TextBlock:
/// local:LinkText.Source="{Binding Text}".
/// </summary>
public static class LinkText
{
    static readonly Regex Pattern = new(
        @"\[(?<label>[^\]]+)\]\((?<md>https?://[^\s)]+)\)|(?<url>https?://[^\s<>()\[\]""']*[^\s<>()\[\]""'.,;:!?])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0xC4, 0xFF));

    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(LinkText), new PropertyMetadata(null, OnSourceChanged));

    public static string? GetSource(DependencyObject d) => (string?)d.GetValue(SourceProperty);
    public static void SetSource(DependencyObject d, string? value) => d.SetValue(SourceProperty, value);

    static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var text = e.NewValue as string ?? "";
        int pos = 0;
        foreach (Match m in Pattern.Matches(text))
        {
            if (m.Index > pos) tb.Inlines.Add(new Run(text[pos..m.Index]));
            var url = m.Groups["md"].Success ? m.Groups["md"].Value : m.Groups["url"].Value;
            var label = m.Groups["label"].Success ? m.Groups["label"].Value : url;
            var link = new Hyperlink(new Run(label))
            {
                NavigateUri = new Uri(url),
                ToolTip = url,
                Foreground = LinkBrush,
                TextDecorations = TextDecorations.Underline,
            };
            link.RequestNavigate += (_, args) => { Open(url); args.Handled = true; };
            tb.Inlines.Add(link);
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) tb.Inlines.Add(new Run(text[pos..]));
    }

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { ActivityLog.Write("LINK", "Could not open " + url + ": " + ex.Message); }
    }
}
