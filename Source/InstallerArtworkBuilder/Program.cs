using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

internal static class Program
{
    private static readonly Color Canvas = Color.FromArgb(11, 18, 17);
    private static readonly Color Green = Color.FromArgb(23, 59, 49);
    private static readonly Color Accent = Color.FromArgb(115, 255, 191);
    private static readonly Color LightAccent = Color.FromArgb(24, 105, 74);

    private static int Main(string[] arguments)
    {
        if (arguments.Length != 4 || arguments[0] != "--input" || arguments[2] != "--outputdir")
        {
            Console.Error.WriteLine("Usage: InstallerArtworkBuilder --input <logo.png> --outputdir <directory>");
            return 2;
        }

        try
        {
            var input = Path.GetFullPath(arguments[1]);
            var output = Path.GetFullPath(arguments[3]);
            using var original = new Bitmap(input);
            var bounds = VisibleBounds(original);
            Directory.CreateDirectory(output);
            CreateBanner(original, bounds, Path.Combine(output, "wizard-banner.png"));
            CreateMark(original, bounds, Path.Combine(output, "wizard-mark.png"));
            CreateBackground(Path.Combine(output, "wizard-background.png"));
            CreateBanner(original, bounds, Path.Combine(output, "wizard-banner-light.png"), light: true);
            CreateBackground(Path.Combine(output, "wizard-background-light.png"), light: true);
            Console.WriteLine($"Installer artwork generated in {output}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static Rectangle VisibleBounds(Bitmap source)
    {
        var left = source.Width;
        var top = source.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            if (source.GetPixel(x, y).A < 20) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }
        if (right < left || bottom < top) throw new InvalidDataException("The logo contains no visible pixels.");
        return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static void Configure(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    private static void CreateBanner(Bitmap logo, Rectangle sourceBounds, string output, bool light = false)
    {
        // Four source pixels per logical wizard pixel keep the panel sharp on
        // high-DPI displays without inventing another version of the app logo.
        using var bitmap = new Bitmap(656, 1256, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        Configure(graphics);
        FillBackground(graphics, bitmap.Width, bitmap.Height, light);

        using (var accentLine = new Pen(Color.FromArgb(92, light ? LightAccent : Accent), 2f))
            graphics.DrawLine(accentLine, 654, 0, 654, bitmap.Height);

        DrawOrbit(graphics, new RectangleF(-245, 100, 950, 950), 45, light);
        DrawOrbit(graphics, new RectangleF(-140, 205, 740, 740), 38, light);
        DrawOrbit(graphics, new RectangleF(-35, 310, 530, 530), 30, light);
        DrawOrbit(graphics, new RectangleF(400, -170, 440, 440), 20, light);
        DrawOrbit(graphics, new RectangleF(140, 1020, 620, 620), 20, light);

        DrawGlow(graphics, new RectangleF(10, 292, 636, 666), light);
        DrawLogo(graphics, logo, sourceBounds, new RectangleF(66, 366, 524, 524));

        using var brandFont = new Font("Segoe UI", 58, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(light ? Color.FromArgb(24, 52, 43) : Color.FromArgb(234, 247, 242));
        using var subtitleFont = new Font("Segoe UI", 33, FontStyle.Regular, GraphicsUnit.Pixel);
        using var subtitleBrush = new SolidBrush(light ? Color.FromArgb(75, 113, 95) : Color.FromArgb(142, 178, 163));
        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("MorphocyteOS", brandFont, textBrush, new RectangleF(38, 99, 580, 84), centered);
        graphics.DrawString("VPN МАРШРУТИЗАЦИЯ", subtitleFont, subtitleBrush, new RectangleF(38, 184, 580, 54), centered);

        using var shortLine = new Pen(Color.FromArgb(116, light ? LightAccent : Accent), 3f);
        graphics.DrawLine(shortLine, 282, 1007, 374, 1007);
        graphics.FillEllipse(textBrush, 321, 1037, 14, 14);
        bitmap.Save(output, ImageFormat.Png);
    }

    private static void CreateMark(Bitmap logo, Rectangle sourceBounds, string output)
    {
        using var bitmap = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        Configure(graphics);
        graphics.Clear(Color.Transparent);
        DrawLogo(graphics, logo, sourceBounds, new RectangleF(8, 8, 112, 112));
        bitmap.Save(output, ImageFormat.Png);
    }

    private static void CreateBackground(string output, bool light = false)
    {
        // Inno Setup's back-image canvas is 497:360. Keeping that exact
        // proportion avoids stretching the decorative orbits on the wizard.
        using var bitmap = new Bitmap(1988, 1440, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        Configure(graphics);
        FillBackground(graphics, bitmap.Width, bitmap.Height, light);
        DrawOrbit(graphics, new RectangleF(1370, -450, 950, 950), 20, light);
        DrawOrbit(graphics, new RectangleF(1487, -333, 716, 716), 18, light);
        DrawOrbit(graphics, new RectangleF(1604, -216, 482, 482), 15, light);
        using var node = new SolidBrush(Color.FromArgb(40, light ? LightAccent : Accent));
        graphics.FillEllipse(node, 1604, 269, 22, 22);
        bitmap.Save(output, ImageFormat.Png);
    }

    private static void FillBackground(Graphics graphics, int width, int height, bool light)
    {
        using var gradient = new LinearGradientBrush(new Rectangle(0, 0, width, height), Green, Canvas, 70f);
        gradient.InterpolationColors = new ColorBlend
        {
            Colors = light
                ? new[] { Color.FromArgb(221, 241, 229), Color.FromArgb(232, 245, 236),
                    Color.FromArgb(244, 250, 246), Color.FromArgb(248, 252, 249) }
                : new[] { Green, Color.FromArgb(15, 32, 27), Canvas, Color.FromArgb(8, 13, 12) },
            Positions = new[] { 0f, 0.38f, 0.78f, 1f }
        };
        graphics.FillRectangle(gradient, 0, 0, width, height);
    }

    private static void DrawOrbit(Graphics graphics, RectangleF rectangle, int alpha, bool light)
    {
        using var pen = new Pen(Color.FromArgb(alpha, light ? LightAccent : Accent), 1.5f);
        graphics.DrawEllipse(pen, rectangle);
    }

    private static void DrawGlow(Graphics graphics, RectangleF rectangle, bool light)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(rectangle);
        using var glow = new PathGradientBrush(path)
        {
            CenterColor = light ? Color.FromArgb(64, 46, 130, 93) : Color.FromArgb(56, 54, 187, 127),
            SurroundColors = new[] { light ? Color.FromArgb(0, 46, 130, 93) : Color.FromArgb(0, 54, 187, 127) },
            FocusScales = new PointF(0.25f, 0.25f)
        };
        graphics.FillEllipse(glow, rectangle);
    }

    private static void DrawLogo(Graphics graphics, Bitmap logo, Rectangle sourceBounds, RectangleF available)
    {
        var scale = Math.Min(available.Width / sourceBounds.Width, available.Height / sourceBounds.Height);
        var width = sourceBounds.Width * scale;
        var height = sourceBounds.Height * scale;
        var destination = new RectangleF(available.X + (available.Width - width) / 2,
            available.Y + (available.Height - height) / 2, width, height);
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        graphics.DrawImage(logo, Rectangle.Round(destination), sourceBounds.X, sourceBounds.Y,
            sourceBounds.Width, sourceBounds.Height, GraphicsUnit.Pixel, attributes);
    }
}
