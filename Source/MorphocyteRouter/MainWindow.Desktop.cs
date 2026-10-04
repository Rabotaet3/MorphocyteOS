using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private readonly bool _desktopIntegrationEnabled;
    private Forms.NotifyIcon? _tray;
    private ContextMenu? _trayMenu;
    private MenuItem? _trayPower, _traySettings;
    private Drawing.Icon? _smallWindowIcon, _largeWindowIcon;
    private bool _exitRequested, _startupMode, _desktopInitialized;
    private HwndSource? _windowSource;
    internal static readonly uint RestoreWindowMessage = RegisterWindowMessage("MorphocyteOS.RestoreWindow.1");
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    internal static void RequestExistingWindow() => PostMessage(new IntPtr(0xffff), RestoreWindowMessage, IntPtr.Zero, IntPtr.Zero);

    private void InitializeDesktopIntegration()
    {
        if (_desktopInitialized) return;
        _desktopInitialized = true;
        try
        {
            var menu = new ContextMenu { Placement = PlacementMode.MousePoint, Style = (Style)FindResource("TrayMenuStyle") };
            _trayMenu = menu;
            MenuItem Item(string text, Action action)
            {
                var item = new MenuItem { Header = text, Style = (Style)FindResource("TrayMenuItemStyle") };
                item.Click += (_, _) => action(); menu.Items.Add(item); return item;
            }
            Item("Открыть MorphocyteOS", RestoreFromTray);
            _trayPower = Item("Запустить VPN", () => { });
            _trayPower.Click += async (_, _) => await RunExclusiveAsync(async () =>
            {
                if (_core.HasTrackedProcess || _recoveryLifetime is not null) { CancelConnectionIntent(); await StopCoreAsync(); } else await StartCoreAsync();
            });
            _traySettings = Item("Настройки", OpenSettingsFromTray);
            Item("Журнал", () => { RestoreFromTray(); ChangeMainPage(true); });
            menu.Items.Add(new Separator { Style = (Style)FindResource("TraySeparatorStyle") });
            var exit = Item("Выход", RequestExit);
            exit.SetResourceReference(Control.ForegroundProperty, "ThemeDanger");
            menu.Opened += (_, _) => { RefreshTrayState(); menu.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100))); };
            _tray = new Forms.NotifyIcon { Icon = ThemeIcons.CreateIcon(32), Text = "MorphocyteOS", Visible = true };
            _tray.MouseUp += (_, args) =>
            {
                if (args.Button != Forms.MouseButtons.Right || _closing) return;
                Dispatcher.Invoke(() =>
                {
                    // A foreground owner is required for a notification-area popup
                    // to dismiss correctly on outside click, including while hidden.
                    SetForegroundWindow(new WindowInteropHelper(this).Handle);
                    menu.IsOpen = false; menu.IsOpen = true;
                });
            };
            _tray.DoubleClick += (_, _) => RestoreFromTray();
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(HandleDesktopMessage);
            RefreshThemeIcons();
            RefreshTrayState();
            _startupMode = Environment.GetCommandLineArgs().Contains("--startup");
            if (_startupMode) HideToTray();
        }
        catch (Exception ex)
        {
            DisposeDesktopIntegration();
            SetLog("Трей недоступен; окно остаётся открытым. " + ex.Message);
        }
    }

    private void OpenSettingsFromTray()
    {
        RestoreFromTray();
        var existing = OwnedWindows.OfType<Window>().FirstOrDefault(dialog => dialog.IsVisible);
        if (existing is not null) { existing.Activate(); return; }
        SettingsButton_Click(this, new RoutedEventArgs());
    }

    private void RefreshThemeIcons()
    {
        var logo = (ImageSource)Application.Current.Resources["ThemeLogo"];
        Icon = logo;
        var window = new WindowInteropHelper(this).Handle;
        if (window == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var small = ThemeIcons.CreateIcon((int)Math.Round(SystemParameters.SmallIconWidth * dpi));
        var large = ThemeIcons.CreateIcon((int)Math.Round(SystemParameters.IconWidth * dpi));
        SendMessage(window, 0x80, IntPtr.Zero, small.Handle); // WM_SETICON / ICON_SMALL
        SendMessage(window, 0x80, new IntPtr(1), large.Handle);
        _smallWindowIcon?.Dispose(); _largeWindowIcon?.Dispose();
        _smallWindowIcon = small; _largeWindowIcon = large;
        if (_tray is not null)
        {
            var old = _tray.Icon; _tray.Icon = ThemeIcons.CreateIcon((int)Math.Round(SystemParameters.SmallIconWidth * dpi)); old?.Dispose();
        }
    }

    private IntPtr HandleDesktopMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == RestoreWindowMessage) { RestoreFromTray(); handled = true; }
        return IntPtr.Zero;
    }

    internal void HideToTray()
    {
        if (_tray is null) { Close(); return; }
        foreach (Window dialog in OwnedWindows) if (dialog.IsVisible) return;
        Hide();
    }

    internal void RestoreFromTray()
    {
        if (_closing) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    internal void RequestExit()
    {
        CancelConnectionIntent();
        _exitRequested = true;
        Close();
    }

    private void RefreshTrayState()
    {
        if (_tray is null || _trayPower is null) return;
        _tray.Text = "MorphocyteOS · " + (_core.IsRunning ? "ядро работает" : "VPN выключен");
        _trayPower.Header = _core.HasTrackedProcess ? "Остановить VPN" : _recoveryLifetime is not null ? "Отменить восстановление" : "Запустить VPN";
        _trayPower.IsEnabled = !_busy && !_closing && (_core.HasTrackedProcess || _recoveryLifetime is not null || _document is { HasVpnConnection: true });
        if (_traySettings is not null) _traySettings.IsEnabled = !_busy && !_closing;
    }

    private void DisposeDesktopIntegration()
    {
        _diagnosticTimer?.Stop();
        _windowSource?.RemoveHook(HandleDesktopMessage);
        _windowSource = null;
        ThemeManager.Changed -= RefreshThemeIcons;
        _trayMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _trayMenu = null;
        _smallWindowIcon?.Dispose(); _largeWindowIcon?.Dispose();
        _smallWindowIcon = null; _largeWindowIcon = null;
        if (_tray is null) return;
        _tray.Visible = false;
        var icon = _tray.Icon;
        _tray.Dispose();
        icon?.Dispose();
        _tray = null;
    }
}
