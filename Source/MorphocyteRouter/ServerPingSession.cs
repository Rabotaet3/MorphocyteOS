using System.IO;
using System.Text.Json.Nodes;

namespace MorphocyteRouter;

internal sealed class ServerPingSession(ClashConfigDocument document,CoreProcessManager? core=null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime=new();
    private readonly CoreProcessManager _core=core??new();
    private readonly SemaphoreSlim _cleanupGate=new(1,1);
    private bool _disposed;
    private CoreDiagnostics? _api;
    private Task<CoreDiagnostics>? _ready;
    private string? _directory;

    internal async Task<int> CheckAsync(string name,CancellationToken token)
    {
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token,_lifetime.Token);
        CoreDiagnostics api;
        try { api=await (_ready??=StartAsync()).WaitAsync(cancellation.Token); }
        catch(OperationCanceledException) { throw; }
        catch { throw new ServerPingSetupException(); }
        return await api.CheckProxyAsync(name,cancellation.Token);
    }

    private async Task<CoreDiagnostics> StartAsync()
    {
        var token=_lifetime.Token;
        _directory=Path.Combine(Path.GetTempPath(),"MorphocyteOS-server-ping-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var isolated=ClashConfigDocument.Parse(Path.Combine(_directory,"profile.yaml"),document.BuildText(document.Rules));
        var engine=await CoreBackend.EnsureSingBoxAsync(token);
        _api=new CoreDiagnostics();
        XrayBridgePlan? bridge=XrayBridgePlan.Create(isolated);
        try
        {
        var json=JsonNode.Parse(SingBoxConfig.Build(isolated,_api.Port,_api.Secret,false,includeTun:false,xrayEndpoints:bridge.Endpoints))!;
        var inbounds=json["inbounds"]!.AsArray();
        foreach(var inbound in inbounds.Where(item=>item?["type"]?.ToString()=="mixed").ToArray()) inbounds.Remove(inbound);
        if(inbounds.Any(item=>item?["type"]?.ToString()=="tun")) throw new InvalidDataException();
        json["log"]!["level"]="error";
        var runtime=Path.Combine(_directory,"runtime.json"); await File.WriteAllTextAsync(runtime,json.ToJsonString(),token);
        var owned=bridge; bridge=null;
        await _core.StartWithBridgeAsync(CoreProcessManager.CreateStartInfo(engine,runtime),owned,token);
        using var ready=CancellationTokenSource.CreateLinkedTokenSource(token); ready.CancelAfter(TimeSpan.FromSeconds(8));
        while(true)
        {
            try { await _api.ReadVersionAsync(ready.Token); return _api; }
            catch(System.Net.Http.HttpRequestException) { await Task.Delay(100,ready.Token); }
        }
        }
        finally { bridge?.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _cleanupGate.WaitAsync();
        try
        {
            if(_disposed) return;
            _lifetime.Cancel();
            if(_ready is not null) { try { await _ready; } catch { } }
            if(!await _core.StopAsync())
                throw new IOException("Остановка проверки серверов ещё не подтверждена. Повтори остановку перед запуском VPN.");
            _api?.Dispose(); _lifetime.Dispose();
            _disposed=true;
            // Only the exact temporary directory created by this session is removed.
            if(_directory is not null)
                try { Directory.Delete(_directory,true); } catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
        finally { _cleanupGate.Release(); }
    }
}

internal sealed class ServerPingSetupException : Exception;
