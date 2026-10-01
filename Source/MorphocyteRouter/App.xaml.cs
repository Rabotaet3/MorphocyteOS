using System.Windows;

namespace MorphocyteRouter;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "Local\\MorphocyteOS.Router.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            ThemeManager.Apply("Тёмная морфоцитная");
            UtilityDialogs.ShowNotice(null, "MorphocyteOS", "Приложение уже запущено. Переключись на существующее окно.");
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
