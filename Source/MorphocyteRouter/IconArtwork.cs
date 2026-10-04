using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MorphocyteRouter;

// The same alpha-aware high-quality renderer is used at build time and at runtime.
internal static class IconArtwork
{
    internal static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    internal static BitmapSource TrimPadding(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bitmap.PixelWidth * 4; var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var left = bitmap.PixelWidth; var top = bitmap.PixelHeight; var right = -1; var bottom = -1;
        for (var y = 0; y < bitmap.PixelHeight; y++)
            for (var x = 0; x < bitmap.PixelWidth; x++)
                if (pixels[y * stride + x * 4 + 3] >= 16)
                {
                    left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
        if (right < left) return source;
        // Leave a breathing margin for antialiasing/glow, but remove the large
        // transparent border that made the actual symbol small in every ICO size.
        var side = Math.Min(Math.Min(bitmap.PixelWidth, bitmap.PixelHeight), (int)Math.Ceiling(Math.Max(right - left + 1, bottom - top + 1) * 1.07));
        var originX = Math.Clamp((left + right - side + 1) / 2, 0, bitmap.PixelWidth - side);
        var originY = Math.Clamp((top + bottom - side + 1) / 2, 0, bitmap.PixelHeight - side);
        var result = new CroppedBitmap(source, new Int32Rect(originX, originY, side, side)); result.Freeze(); return result;
    }

    internal static BitmapSource Tint(BitmapSource source, Color accent, bool light)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var targetHue = Hue(accent.R / 255d, accent.G / 255d, accent.B / 255d);
        // Original artwork is mint (164 degrees). Keep its blue highlights and
        // dark texture, rotating the whole palette rather than painting a flat mask.
        var offset = targetHue - 164;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0) continue;
            var r = pixels[i + 2] / 255d; var g = pixels[i + 1] / 255d; var b = pixels[i] / 255d;
            var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
            var saturation = max == 0 ? 0 : (max - min) / max;
            var hue = (Hue(r, g, b) + offset + 360) % 360;
            // The darker variant remains legible on white/light palettes.
            var value = light ? max * .82 : max;
            var chroma = value * saturation;
            var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
            var m = value - chroma;
            (r, g, b) = hue switch
            {
                < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d),
                < 180 => (0d, chroma, x), < 240 => (0d, x, chroma),
                < 300 => (x, 0d, chroma), _ => (chroma, 0d, x)
            };
            pixels[i] = (byte)Math.Round((b + m) * 255);
            pixels[i + 1] = (byte)Math.Round((g + m) * 255);
            pixels[i + 2] = (byte)Math.Round((r + m) * 255);
        }
        var result = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static double Hue(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b)); var delta = max - Math.Min(r, Math.Min(g, b));
        if (delta < .00001) return 164;
        var value = max == r ? (g - b) / delta : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        return (value * 60 + 360) % 360;
    }

    internal static BitmapSource Scale(BitmapSource source, int size)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var context = visual.RenderOpen()) context.DrawImage(source, new Rect(0, 0, size, size));
        var result = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze();
        return result;
    }

    internal static byte[] EncodeIcon(BitmapSource source)
    {
        var frames = Sizes.Select(size =>
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Scale(source, size)));
            using var png = new MemoryStream(); encoder.Save(png); return png.ToArray();
        }).ToArray();
        using var result = new MemoryStream(); using var writer = new BinaryWriter(result);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)Sizes.Length);
        var offset = 6 + 16 * Sizes.Length;
        for (var i = 0; i < Sizes.Length; i++)
        {
            writer.Write((byte)(Sizes[i] == 256 ? 0 : Sizes[i])); writer.Write((byte)(Sizes[i] == 256 ? 0 : Sizes[i]));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
        return result.ToArray();
    }
}
