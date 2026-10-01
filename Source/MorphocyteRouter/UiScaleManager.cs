using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace MorphocyteRouter;

internal static class UiScaleManager
{
    private sealed class WindowMetrics
    {
        internal required double MinWidth { get; init; }
        internal required double MinHeight { get; init; }
        internal double AppliedScale { get; set; } = 1;
    }

    private static readonly ConditionalWeakTable<Window, WindowMetrics> Metrics = new();
    private static double _currentScale = 1;

    internal static double CurrentScale
    {
        get => _currentScale;
        set => _currentScale = Normalize(value);
    }

    internal static double Normalize(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, .8, 1.5) : 1;

    // WPF work-area dimensions are device-independent, just like Window.Width.
    // The content is remeasured by LayoutTransform, keeping text vector-rendered.
    internal static void Apply(Window window, FrameworkElement content, double scale)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(content);
        window.Dispatcher.VerifyAccess();
        scale = Normalize(scale);
        var metrics = Metrics.GetValue(window, candidate => new WindowMetrics
        {
            MinWidth = candidate.MinWidth,
            MinHeight = candidate.MinHeight
        });
        var width = double.IsFinite(window.Width) ? window.Width : window.ActualWidth;
        var height = double.IsFinite(window.Height) ? window.Height : window.ActualHeight;
        var ratio = scale / metrics.AppliedScale;
        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(1, workArea.Width - 24);
        var maxHeight = Math.Max(1, workArea.Height - 24);
        var minWidth = Math.Min(metrics.MinWidth * scale, maxWidth);
        var minHeight = Math.Min(metrics.MinHeight * scale, maxHeight);

        // Reset minima first so reducing the scale never leaves stale oversized
        // constraints. Keep a user's manual window resize on subsequent calls.
        window.MinWidth = 0;
        window.MinHeight = 0;
        window.MaxWidth = maxWidth;
        window.MaxHeight = maxHeight;
        window.MinWidth = minWidth;
        window.MinHeight = minHeight;
        if (width > 0) window.Width = Math.Clamp(width * ratio, minWidth, maxWidth);
        if (height > 0) window.Height = Math.Clamp(height * ratio, minHeight, maxHeight);

        var transform = new ScaleTransform(scale, scale);
        transform.Freeze();
        content.LayoutTransform = transform;
        content.UseLayoutRounding = true;
        content.SnapsToDevicePixels = true;
        metrics.AppliedScale = scale;
    }
}
