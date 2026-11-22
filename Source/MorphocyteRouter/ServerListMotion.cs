using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MorphocyteRouter;

internal sealed class ServerListMotion : IDisposable
{
    private readonly ScrollViewer _scroll;
    private readonly Func<double> _rowHeight;
    private readonly Action _repaint;
    private readonly DispatcherTimer _frames;
    private readonly Stopwatch _clock = new();
    private double _target, _activeRowHeight, _lastFrame, _lastInput;
    internal bool IsAnimating => _frames.IsEnabled;
    internal Func<bool> AnimationsEnabled { get; set; } = () => SystemParameters.ClientAreaAnimation;

    internal ServerListMotion(ScrollViewer scroll, Func<double> rowHeight, Action repaint)
    {
        _scroll = scroll;
        _rowHeight = rowHeight;
        _repaint = repaint;
        _frames = new DispatcherTimer(DispatcherPriority.Render, scroll.Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _frames.Tick += DrawFrame;
        scroll.PreviewMouseWheel += Wheel;
        scroll.PreviewMouseDown += PointerDown;
        scroll.PreviewKeyDown += KeyDown;
        scroll.Unloaded += Unloaded;
    }

    private void Wheel(object sender, MouseWheelEventArgs e)
    {
        if (_scroll.ScrollableHeight <= 0 || e.Delta == 0) return;
        ScrollByRows(-Math.Sign(e.Delta) * Math.Clamp(2 * Math.Abs(e.Delta) / 120d, 1, 2));
        e.Handled = true;
    }

    internal void ScrollByRows(double delta)
    {
        if (!_scroll.IsLoaded || delta == 0) return;
        var offset = _scroll.VerticalOffset;
        var continuing = IsAnimating;
        if (!continuing) _activeRowHeight = _rowHeight();
        var origin = continuing && Math.Sign(_target - offset) == Math.Sign(delta) ? _target : offset;
        var target = Math.Clamp(origin + delta * _activeRowHeight, 0, _scroll.ScrollableHeight);
        if (Math.Abs(target - offset) < .5) { Cancel(); return; }
        if (!AnimationsEnabled())
        {
            Cancel();
            _scroll.ScrollToVerticalOffset(target);
            return;
        }
        _target = target;
        if (!continuing)
        {
            _lastFrame = 0;
            _clock.Restart();
            _frames.Start();
            _repaint();
        }
        _lastInput = _clock.Elapsed.TotalMilliseconds;
    }

    private void DrawFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var offset = _scroll.VerticalOffset;
        var target = Math.Min(_target, _scroll.ScrollableHeight);
        var remaining = target - offset;
        if (now - _lastInput >= 210 || Math.Abs(remaining) < .5)
        {
            _scroll.ScrollToVerticalOffset(target);
            Cancel();
            return;
        }

        // Feed the scroll viewer one position per tick. Wheel events change
        // the target without resetting time or repainting the list on each event.
        var fraction = 1 - Math.Exp(-Math.Min(80, now - _lastFrame) / 56);
        _lastFrame = now;
        _scroll.ScrollToVerticalOffset(offset + remaining * fraction);
    }

    internal void Cancel()
    {
        if (!IsAnimating) return;
        _frames.Stop();
        _clock.Stop();
        _repaint();
    }

    private void PointerDown(object sender, MouseButtonEventArgs e) => Cancel();
    private void KeyDown(object sender, KeyEventArgs e) => Cancel();
    private void Unloaded(object sender, RoutedEventArgs e) => Cancel();

    public void Dispose()
    {
        Cancel();
        _frames.Tick -= DrawFrame;
        _scroll.PreviewMouseWheel -= Wheel;
        _scroll.PreviewMouseDown -= PointerDown;
        _scroll.PreviewKeyDown -= KeyDown;
        _scroll.Unloaded -= Unloaded;
    }
}
