using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private Window? _coreLogWindow;
    private ContentControl? _floatingCoreLogHost;
    private double _coreLogDragX, _coreLogDragY;
    private bool IsCoreLogVisible => _coreLogWindow?.IsVisible == true || CoreLogHost.IsVisible;

    private void InitializeCoreLogPanel()
    {
        CoreLogToggle.IsChecked = _settings.ShowCoreLog;
        UpdateCoreLogLayout();
        ThemeManager.Changed += RefreshCoreLogWindowIcon;
        Closed += (_, _) => { ThemeManager.Changed -= RefreshCoreLogWindowIcon; _coreLogWindow?.Close(); };
    }

    private void RefreshCoreLogWindowIcon()
    {
        if (_coreLogWindow is { } window && Application.Current.Resources["ThemeLogo"] is System.Windows.Media.ImageSource icon)
            window.Icon = icon;
    }

    private async void CoreLogToggle_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveAsync(async () =>
        {
            _settings.ShowCoreLog = CoreLogToggle.IsChecked == true;
            if (!_settings.ShowCoreLog) _coreLogWindow?.Close();
            UpdateCoreLogLayout();
            await SaveSettingsAsync();
        });
        CoreLogToggle.IsChecked = _settings.ShowCoreLog;
    }

    private void UpdateCoreLogLayout()
    {
        var inline = _settings.ShowCoreLog && _coreLogWindow is null;
        CoreLogHost.Visibility = inline ? Visibility.Visible : Visibility.Collapsed;
        CoreLogRow.Height = inline ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        ConnectionTableRow.Height = new GridLength(inline ? 3 : 1, GridUnitType.Star);
        if (inline || _coreLogWindow is not null) RefreshCoreLogText();
    }

    private void RefreshCoreLogText()
    {
        var text = string.Join(Environment.NewLine, _logLines);
        if (LogText.Text == text) return;
        var offset = LogText.VerticalOffset;
        var following = LogText.SelectionLength == 0 && LogText.ExtentHeight - LogText.ViewportHeight - offset < 28;
        var start = LogText.SelectionStart;
        var length = LogText.SelectionLength;
        LogText.Text = text;
        LogText.Select(Math.Min(start, text.Length), Math.Min(length, Math.Max(0, text.Length - start)));
        if (following) LogText.ScrollToEnd();
        else LogText.ScrollToVerticalOffset(offset);
    }

    private void CoreLogGrip_DragStarted(object sender, DragStartedEventArgs e)
    { _coreLogDragX = _coreLogDragY = 0; }

    private void CoreLogGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    { _coreLogDragX = _coreLogDragY = 0; }

    private void CoreLogGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _coreLogDragX += e.HorizontalChange; _coreLogDragY += e.VerticalChange;
        if (Math.Abs(_coreLogDragX) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(_coreLogDragY) < SystemParameters.MinimumVerticalDragDistance) return;
        // The log's TextBox never initiates this action: only its four edge grips do.
        if (sender is Thumb thumb) thumb.CancelDrag();
        var window = DetachCoreLog();
        if (window is null) return;
        // Screen pixels, not primary-monitor DIP coordinates. Negative positions
        // and mixed-DPI displays are valid and must not be clamped to monitor 1.
        if (ReadCoreLogPointer(out var pointer))
            PositionCoreLogWindow(new WindowInteropHelper(window).Handle, IntPtr.Zero,
                pointer.X - 24, pointer.Y - 24, 0, 0, 0x0001 | 0x0004 | 0x0010);
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            try { window.DragMove(); }
            catch (InvalidOperationException) { /* A released button ends the gesture. */ }
        }
        e.Handled = true;
    }

    private Window? DetachCoreLog()
    {
        if (_closing || !_settings.ShowCoreLog) return null;
        if (_coreLogWindow is not null) return _coreLogWindow;
        var window = DialogChrome.CreateWindow(this, "ЖУРНАЛ СОБЫТИЙ", 800, 460, 550, 280, ResizeMode.CanResizeWithGrip);
        window.ShowInTaskbar = true;
        window.Icon = Icon;
        _floatingCoreLogHost = new ContentControl {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch, Margin = new Thickness(12, 8, 12, 12)
        };
        var copy = DialogChrome.MakeButton("КОПИРОВАТЬ ЖУРНАЛ", false);
        copy.MinWidth = 180;
        copy.Click += CopyLog_Click;
        var dock = DialogChrome.MakeButton("ВЕРНУТЬ В ОСНОВНОЕ ОКНО", false);
        dock.MinWidth = 220;
        dock.Margin = new Thickness(8, 0, 0, 0);
        dock.Click += (_, _) => window.Close();
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(copy); footer.Children.Add(dock);
        window.Content = DialogChrome.BuildFrame(window, "ЖУРНАЛ СОБЫТИЙ", _floatingCoreLogHost, footer);
        System.Windows.Shell.WindowChrome.SetWindowChrome(window, new() {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(18), UseAeroCaptionButtons = false
        });
        // Move the same control, retaining its selection, history and scroll offset.
        CoreLogHost.Content = null;
        _floatingCoreLogHost.Content = CoreLogSurface;
        _coreLogWindow = window;
        window.Closed += (_, _) =>
        {
            _floatingCoreLogHost.Content = null;
            CoreLogHost.Content = CoreLogSurface;
            _floatingCoreLogHost = null;
            _coreLogWindow = null;
            if (!_closing) UpdateCoreLogLayout();
        };
        UpdateCoreLogLayout();
        window.Show();
        return window;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PositionCoreLogWindow(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct CoreLogScreenPoint { public int X, Y; }

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadCoreLogPointer(out CoreLogScreenPoint point);
}
