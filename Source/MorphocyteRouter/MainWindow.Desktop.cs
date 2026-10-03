using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private readonly bool _desktopIntegrationEnabled;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayPower;
    private bool _exitRequested, _startupMode, _desktopInitialized;
    private HwndSource? _windowSource;
    internal static readonly uint RestoreWindowMessage = RegisterWindowMessage("MorphocyteOS.RestoreWindow.1");
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    internal static void RequestExistingWindow() => PostMessage(new IntPtr(0xffff), RestoreWindowMessage, IntPtr.Zero, IntPtr.Zero);

    private void InitializeDesktopIntegration()
    {
        if (_desktopInitialized) return;
        _desktopInitialized = true;
        try
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Открыть MorphocyteOS", null, (_, _) => RestoreFromTray());
            _trayPower = new Forms.ToolStripMenuItem("Запустить VPN");
            _trayPower.Click += async (_, _) => await RunExclusiveAsync(async () =>
            {
                if (_core.HasTrackedProcess || _recoveryLifetime is not null) { CancelConnectionIntent(); await StopCoreAsync(); } else await StartCoreAsync();
            });
            menu.Items.Add(_trayPower);
            menu.Items.Add("Журнал", null, (_, _) => { RestoreFromTray(); ChangeMainPage(true); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (_, _) => RequestExit());
            using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/MorphocyteOS;component/Assets/morphocyte.ico"))?.Stream
                ?? throw new IOException("Не найден значок приложения.");
            using var icon = new Drawing.Icon(iconStream);
            _tray = new Forms.NotifyIcon { Icon = (Drawing.Icon)icon.Clone(), Text = "MorphocyteOS", ContextMenuStrip = menu, Visible = true };
            _tray.DoubleClick += (_, _) => RestoreFromTray();
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(HandleDesktopMessage);
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
        _trayPower.Text = _core.HasTrackedProcess ? "Остановить VPN" : _recoveryLifetime is not null ? "Отменить восстановление" : "Запустить VPN";
        _trayPower.Enabled = !_busy && !_closing && (_core.HasTrackedProcess || _document is { HasVpnConnection: true });
    }

    private void DisposeDesktopIntegration()
    {
        _diagnosticTimer?.Stop();
        _windowSource?.RemoveHook(HandleDesktopMessage);
        _windowSource = null;
        if (_tray is null) return;
        _tray.Visible = false;
        var icon = _tray.Icon;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        icon?.Dispose();
        _tray = null;
    }
}
