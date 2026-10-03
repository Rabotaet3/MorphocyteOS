using System.Windows;

namespace MorphocyteRouter;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--apply-update")
        {
            StartupUri = null;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code;
            try { code = await UpdateInstaller.RunAsync(e.Args[1]); }
            catch { code = 2; }
            Shutdown(code);
            return;
        }
        _singleInstance = new Mutex(true, "Local\\MorphocyteOS.Router.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            if (!e.Args.Contains("--startup")) MorphocyteRouter.MainWindow.RequestExistingWindow();
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            UtilityDialogs.ShowNotice(MainWindow, "MorphocyteOS — ошибка", args.Exception.Message);
            args.Handled = true;
        };
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
