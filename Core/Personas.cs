using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Thokolosi.Core;

/// <summary>
/// A head you can choose for Thokolosi. The picture is a 900x1050 transparent PNG in
/// Assets\personas, rendered by tools\render-personas.ps1.
/// </summary>
public record Persona(string Id, string Name, string Blurb);

public static class Personas
{
    public const string DefaultId = "thokolosi";

    public static readonly Persona[] All =
    {
        new("thokolosi", "Thokolosi",
            "The original: dark swept hair, gold hoop, warm studio light."),
        new("jasmine", "Jasmine",
            "Long dark hair, gold browband with a turquoise jewel, hoop earrings."),
        new("genie", "Genie",
            "Blue skin, glowing eyes, blue headwrap with a gold band and a full beard."),
        new("sultan", "Sultan",
            "White headwrap and white beard, gold and ruby browband, calm and regal."),
        new("sorcerer", "Sorcerer",
            "Purple hood and robe, amber glowing eyes, pale skin."),
        new("merchant", "Merchant",
            "Darker skin, dark beard and moustache, deep red cap with a brass badge."),
        new("storyteller", "Storyteller",
            "Crimson headwrap with a gold band, violet jewel, hoop earring."),
    };

    public static Persona Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return All[0];
        foreach (var p in All)
            if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
        return All[0];
    }

    public static string ImagePath(Persona p) =>
        Path.Combine(AppContext.BaseDirectory, "personas", p.Id + ".png");

    /// <summary>A fresh ImageSource for a persona. Cached per id so switching back is instant.</summary>
    public static ImageSource Image(string? id)
    {
        var p = Get(id);
        if (Cache.TryGetValue(p.Id, out var cached)) return cached;

        ImageSource src;
        var disk = ImagePath(p);
        try
        {
            if (File.Exists(disk))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(disk, UriKind.Absolute);
                bi.EndInit();
                bi.Freeze();
                src = bi;
            }
            else
            {
                src = new BitmapImage(new Uri($"pack://application:,,,/Assets/personas/{p.Id}.png", UriKind.Absolute));
                ((BitmapImage)src).Freeze();
            }
        }
        catch
        {
            src = new BitmapImage(new Uri("pack://application:,,,/Assets/personas/thokolosi.png", UriKind.Absolute));
            ((BitmapImage)src).Freeze();
        }

        Cache[p.Id] = src;
        return src;
    }

    static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
}
