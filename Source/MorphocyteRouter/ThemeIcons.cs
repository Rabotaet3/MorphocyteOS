using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace MorphocyteRouter;

internal static class ThemeIcons
{
    private sealed record Artwork(BitmapSource Logo, byte[] Icon);
    private static readonly Dictionary<string, Artwork> Cache = new(StringComparer.Ordinal);
    private static readonly BitmapSource Original = Load();

    private static BitmapSource Load()
    {
        var image = new BitmapImage(); image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/MorphocyteOS;component/Assets/morphocyte-icon.png");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit(); image.Freeze(); return IconArtwork.TrimPadding(image);
    }

    private static Artwork Get(string theme, Color accent, bool light)
    {
        if (Cache.TryGetValue(theme, out var cached)) return cached;
        var artwork = IconArtwork.Tint(Original, accent, light);
        // Resample each shell icon directly from the original-size artwork; do not
        // resize an already downsampled logo a second time.
        var result = new Artwork(IconArtwork.Scale(artwork, 512), IconArtwork.EncodeIcon(artwork)); Cache.Add(theme, result); return result;
    }

    internal static BitmapSource Logo(string theme, Color accent, bool light) => Get(theme, accent, light).Logo;

    internal static Drawing.Icon CreateIcon(int size)
    {
        var accent = ((SolidColorBrush)ThemeManager.GetBrush("ThemeAccent")).Color;
        var artwork = Get(ThemeManager.CurrentTheme, accent, ThemeManager.IsLightTheme);
        using var stream = new MemoryStream(artwork.Icon);
        using var icon = new Drawing.Icon(stream, size, size);
        return (Drawing.Icon)icon.Clone();
    }
}
