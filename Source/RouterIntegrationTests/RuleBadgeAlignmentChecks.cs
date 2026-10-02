using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MorphocyteRouter;

internal partial class Program
{
    private static int RunRuleBadgeAlignmentChecks(MainWindow window, string scratch)
    {
        var template = (DataTemplate)window.FindResource("RuleItemTemplate");
        var count = 0;
        foreach (var kind in new[] { "DOMAIN", "DOMAIN-SUFFIX", "DOMAIN-KEYWORD", "PROCESS-NAME" })
        foreach (var route in new[] { "VPN", "DIRECT", "REJECT" })
        foreach (var scale in new[] { .8, 1, 1.25, 1.5 })
        {
            var rule = new DomainRule { Kind = kind, Value = "example.com", Route = route };
            var row = (FrameworkElement)template.LoadContent();
            row.DataContext = rule;
            var host = new Border { Child = row, LayoutTransform = new ScaleTransform(scale, scale) };
            TextElement.SetFontFamily(host, window.FontFamily);
            host.Measure(new Size(900 * scale, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, 900 * scale, host.DesiredSize.Height));
            host.UpdateLayout();
            foreach (var label in new[] { rule.KindLabel, rule.Value, rule.RouteLabel })
            {
                var text = Descendants<TextBlock>(row).Single(block => block.Text == label);
                var center = text.TranslatePoint(new Point(0, text.ActualHeight / 2), row).Y;
                if (text.VerticalAlignment != VerticalAlignment.Center ||
                    Math.Abs(center - row.ActualHeight / 2) > 1 ||
                    Math.Abs(text.ActualHeight - text.DesiredSize.Height) > 1)
                    throw new Exception($"FAIL: rule text '{label}' is not vertically centered ({kind}, {route}, {scale:P0}).");
                count++;
            }
            if (kind == "DOMAIN" && route == "VPN" && scale == 1)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth),
                    (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(scratch, "rule-badge-alignment.png"));
                encoder.Save(file);
            }
        }
        Console.WriteLine($"PASS: {count} rule text alignment checks across four types, three routes and four scales");
        return count;
    }
}
