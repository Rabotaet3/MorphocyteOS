using System.IO;
using System.Windows;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private string RememberedVpnServer()
        => _document is { } document ? _settings.ProfileSelections.GetValueOrDefault(document.Path)
            ?? _settings.ProfileSubscriptions.GetValueOrDefault(document.Path)?.SelectedServer ?? "" : "";

    internal async Task<bool> SelectVpnServerAsync(string selection)
    {
        var success=false;
        await RunExclusiveAsync(async()=>
        {
            if(_document is not { } document || !VpnServerCatalog.Choices(document).Contains(selection,StringComparer.Ordinal))
                throw new InvalidDataException("Этот сервер отсутствует в доступном списке текущего профиля.");
            if(_core.IsRunning && _diagnostics is null)
                throw new IOException("Локальный API ядра пока недоступен. Выбор сервера не изменён.");
            var savedSelection=_settings.ProfileSelections.GetValueOrDefault(document.Path);
            var previousRevision=_settings.ServerSelectionRevision;
            var source=_settings.ProfileSubscriptions.GetValueOrDefault(document.Path);
            var savedSourceSelection=source?.SelectedServer;
            var diagnostics=_core.IsRunning?_diagnostics:null;
            string? previousLive=null;
            var liveAttempted=false;
            _speedTestLifetime?.Cancel(); _diagnosticLifetime?.Cancel();
            try
            {
                if(diagnostics is not null && document.VpnRoute is { } group)
                {
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
                    previousLive=await diagnostics.ReadSelectionAsync(group,deadline.Token);
                    if(!VpnServerCatalog.Choices(document).Contains(previousLive,StringComparer.Ordinal))
                        previousLive=await diagnostics.ReadImmediateSelectionAsync(group,deadline.Token);
                    liveAttempted=true;
                    if(!await diagnostics.RestoreSelectionAsync(group,selection,deadline.Token))
                        throw new IOException("Ядро не разрешило выбрать этот сервер. Подписка и правила не изменены.");
                    foreach(var expected in VpnServerCatalog.SelectionPath(document,selection))
                        if(await diagnostics.ReadImmediateSelectionAsync(expected.Key,deadline.Token)!=expected.Value)
                            throw new IOException("Ядро не подтвердило переключение сервера. Прежний выбор восстанавливается.");
                }
                _settings.ProfileSelections[document.Path]=selection;
                _settings.ServerSelectionRevision=checked(previousRevision+1);
                if(source is not null) source.SelectedServer=selection;
                await SaveSettingsAsync();
                _selectedConnection=selection;
                _downloadCapacity=null; _capacityServer=""; _health.Clear(); _nextHealthCheck=DateTime.UtcNow.AddSeconds(2);
                RefreshHealthReadout(); RefreshConnectionCard();
                ConnectionStateText.Text="Проверка не выполнялась";
                if (diagnostics is not null) SetStatus("ПРОВЕРЯЮ VPN", ModeLabel(_settings.Mode), "#FFD166");
                SetLog(diagnostics is null?"VPN-сервер выбран. Он будет использован при запуске.":"VPN-сервер переключён. Выбор сохранён.");
                success=true;
            }
            catch
            {
                if(savedSelection is null) _settings.ProfileSelections.Remove(document.Path); else _settings.ProfileSelections[document.Path]=savedSelection;
                _settings.ServerSelectionRevision=previousRevision;
                if(source is not null) source.SelectedServer=savedSourceSelection??"";
                if(liveAttempted && previousLive is not null && diagnostics is not null && document.VpnRoute is { } group)
                {
                    try { using var rollback=new CancellationTokenSource(TimeSpan.FromSeconds(5)); if(!await diagnostics.RestoreSelectionAsync(group,previousLive,rollback.Token)) throw new IOException(); }
                    catch { SetLog("Не удалось подтвердить прежний сервер после ошибки выбора. Проверь подключение."); }
                }
                throw;
            }
            finally
            {
                if(!_closing && (_diagnosticLifetime is null || _diagnosticLifetime.IsCancellationRequested))
                {
                    _diagnosticLifetime?.Dispose(); _diagnosticLifetime=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    _checkingConnection = false;
                    if(_core.IsRunning)
                    {
                        StartTrafficMonitor();
                        if (success) _ = CheckConnectionAsync(manual: false);
                    }
                }
            }
        });
        return success;
    }
}
