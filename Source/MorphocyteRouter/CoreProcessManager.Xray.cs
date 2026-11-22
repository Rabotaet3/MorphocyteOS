using System.Diagnostics;
using System.IO;

namespace MorphocyteRouter;

public sealed partial class CoreProcessManager
{
    private CoreProcessManager[] _bridges=[];
    private XrayBridgePlan? _bridgePlan;
    private int _stoppingBridge;
    private volatile bool _bridgeFailed;
    internal int? BridgeProcessId => _bridges.FirstOrDefault()?.TrackedProcessId;
    internal IReadOnlyList<int> BridgeProcessIds => _bridges.Select(bridge=>bridge.TrackedProcessId).OfType<int>().ToArray();
    private static bool IsXray(string executable) => Path.GetFileNameWithoutExtension(executable).Equals("xray", StringComparison.OrdinalIgnoreCase);

    internal async Task StartWithBridgeAsync(ProcessStartInfo info, XrayBridgePlan plan, CancellationToken token,
        Func<CancellationToken, Task>? waitUntilReady = null, bool bridgeValidated = false)
    {
        if (!plan.HasConnections) { plan.Dispose(); await StartAsync(info, token, waitUntilReady); return; }
        if (HasTrackedProcess) { plan.Dispose(); throw new IOException("Предыдущие процессы VPN ещё не завершены."); }
        _bridgePlan = plan;
        var plans=plan.ProcessPlans;
        _bridges=plans.Select(_=>new CoreProcessManager()).ToArray(); _bridgeFailed = false;
        foreach(var bridge in _bridges)
        {
        bridge.Exited += () =>
        {
            if (Volatile.Read(ref _stoppingBridge) == 0 && _bridges.Contains(bridge))
            {
                _bridgeFailed = true;
                _ = BridgeFailedAsync(bridge);
            }
        };
        }
        try
        {
            token.ThrowIfCancellationRequested();
            if (!CoreBackend.IsSingBox(info.FileName)) throw new InvalidDataException("Мост Xray требует основного ядра sing-box.");
            if (!bridgeValidated)
            {
                plan.ConfigureDnsFromRuntime(await File.ReadAllTextAsync(info.ArgumentList[^1], token));
                var check = await plan.ValidateAsync(token);
                if (!check.Success) throw new InvalidDataException(check.Message);
            }
            plan.ReleasePorts();
            await Parallel.ForEachAsync(Enumerable.Range(0, plans.Count),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                async (index, childToken) => await _bridges[index].StartAsync(
                    CreateStartInfo(XrayBridgePlan.EnginePath, plans[index].ConfigPath!), childToken));
            await plan.VerifyReadyAsync(token);
            if (_bridgeFailed || _bridges.Any(bridge=>!bridge.IsRunning)) throw new IOException("Вспомогательное ядро Xray завершилось при запуске.");
            await StartAsync(info, token, waitUntilReady);
            if (_bridgeFailed || _bridges.Any(bridge=>!bridge.IsRunning)) throw new IOException("Вспомогательное ядро Xray завершилось при запуске.");
        }
        catch { await StopAsync(); throw; }
    }

    private async Task BridgeFailedAsync(CoreProcessManager bridge)
    {
        // The owning gate prevents a late callback from stopping a new session.
        await _gate.WaitAsync();
        try
        {
            if (!_bridges.Contains(bridge)) return;
            if (_process is { } process)
            {
                _pendingStop ??= _terminate(process);
                try { await _pendingStop.WaitAsync(_stopTimeout); ReapExited(); }
                catch { /* Retain ownership; the UI offers another stop attempt. */ }
            }
            await StopBridgeAsync();
            StartupFailed?.Invoke("Xray неожиданно завершился. VPN остановлен или требует остановки; прямой маршрут вместо VPN не включался.");
        }
        finally { _gate.Release(); }
    }

    private async Task StopBridgeAfterMainExitAsync(CoreProcessManager bridge)
    {
        await _gate.WaitAsync();
        try { if (_bridges.Contains(bridge)) await StopBridgeAsync(); }
        finally { _gate.Release(); }
    }

    private async Task<bool> StopBridgeAsync()
    {
        if (_bridges.Length==0) return true;
        Interlocked.Increment(ref _stoppingBridge);
        try
        {
            var remaining=new List<CoreProcessManager>();
            foreach(var bridge in _bridges) if(!await bridge.StopAsync()) remaining.Add(bridge);
            _bridges=remaining.ToArray();
            if(_bridges.Length>0) return false;
            _bridgePlan?.Dispose(); _bridgePlan = null;
            return true;
        }
        finally { Interlocked.Decrement(ref _stoppingBridge); }
    }
}
