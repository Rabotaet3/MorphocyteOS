using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

internal partial class Program
{
    private static async Task<int> RunServerPickerChecks(string scratch,string fakeExe)
    {
        var count=await RunServerSortingChecks(scratch);
        count+=await RunSubscriptionMetadataChecks(scratch,fakeExe);
        void Check(bool ok,string label) { if(!ok) throw new Exception("FAIL: "+label); count++; Console.WriteLine("PASS: "+label); }
        var names=Enumerable.Range(1,40).Select(i=>$"№{i:00} · Сервер {i:00} — Подписка").ToArray();
        names[^1]+=" — Очень длинное название сервера с дополнительной информацией о стране и варианте подключения";
        var nodes=string.Join("\n",names.Select((name,i)=>$"  - {{name: '{name}', type: socks5, server: 192.0.2.{i+1}, port: 1080}}"));
        var original="mixed-port: 0\nproxies:\n"+nodes+"\nproxy-groups:\n  - {name: VPN, type: select, proxies: ["+string.Join(", ",names.Select(name=>"'"+name+"'"))+"]}\nrules:\n  - DOMAIN-SUFFIX,keep.example,VPN\n  - MATCH,DIRECT\n";
        var path=Path.Combine(scratch,"forty-servers.yaml"); File.WriteAllText(path,original);
        var document=ClashConfigDocument.Load(path);
        Check(VpnServerCatalog.Choices(document).SequenceEqual(names),"all 40 subscription servers are offered in provider order without truncation");
        var selectedPath=VpnServerCatalog.SelectionPath(document,names[^1]);
        Check(selectedPath["VPN"]==names[^1],"the final server is reachable through the main VPN selector");
        using var nativeApi=new CoreDiagnostics();
        var runtime=SingBoxConfig.Build(document,nativeApi.Port,nativeApi.Secret,false,includeTun:false,selectedServer:names[^1],selectionCacheId:"test-selection-0");
        using(var json=JsonDocument.Parse(runtime))
        {
            var group=json.RootElement.GetProperty("outbounds").EnumerateArray().Single(o=>o.GetProperty("tag").GetString()=="VPN");
            Check(group.GetProperty("default").GetString()==names[^1] && group.GetProperty("outbounds").GetArrayLength()==40,"sing-box starts with the remembered server and retains all 40 options");
            Check(group.GetProperty("interrupt_exist_connections").GetBoolean(),"changing the main selector reconnects existing VPN flows through the new server");
        }
        var nestedText="mixed-port: 0\nproxies:\n - {name: First, type: socks5, server: 192.0.2.1, port: 1080}\n - {name: Last, type: socks5, server: 192.0.2.2, port: 1080}\nproxy-groups:\n - {name: VPN, type: select, proxies: [DIRECT, Region]}\n - {name: Region, type: select, proxies: [First, Last]}\nrules:\n - MATCH,VPN\n";
        var nested=ClashConfigDocument.Parse(Path.Combine(scratch,"nested.yaml"),nestedText);
        Check(VpnServerCatalog.Choices(nested).SequenceEqual(new[]{"First","Last"}),"nested selectors expose their servers while DIRECT is excluded");
        var nestedRuntime=SingBoxConfig.Build(nested,nativeApi.Port,nativeApi.Secret,false,includeTun:false,selectedServer:"Last");
        using(var json=JsonDocument.Parse(nestedRuntime))
        {
            var groups=json.RootElement.GetProperty("outbounds").EnumerateArray().Where(o=>o.GetProperty("type").GetString()=="selector").ToDictionary(o=>o.GetProperty("tag").GetString()!);
            Check(groups["VPN"].GetProperty("default").GetString()=="Region" && groups["Region"].GetProperty("default").GetString()=="Last","remembered nested selection is applied before sing-box starts accepting traffic");
        }
        var mihomo=nested.BuildRuntimeText(nativeApi.Port,nativeApi.Secret,selectedServer:"Last");
        var yaml=new YamlStream(); yaml.Load(new StringReader(mihomo));
        var yamlGroups=(YamlSequenceNode)((YamlMappingNode)yaml.Documents[0].RootNode).Children[new YamlScalarNode("proxy-groups")];
        Check(yamlGroups.Children.OfType<YamlMappingNode>().Select(g=>((YamlSequenceNode)g.Children[new YamlScalarNode("proxies")]).Children[0].ToString()).SequenceEqual(new[]{"Region","Last"}),"Mihomo also receives the remembered path without changing the saved profile");
        Check(nested.SourceHash==ClashConfigDocument.Hash(nestedText) && File.ReadAllText(path)==original,"selection only changes runtime configuration and leaves source YAML intact");

        var settingsPath=Path.Combine(scratch,"server-settings.json"); var settings=AppSettings.Load(settingsPath);
        settings.ConfigPath=path; settings.CorePath=fakeExe; settings.AutoCheckUpdates=false; settings.AutoReconnect=false;
        settings.ProfileSubscriptions[path]=SubscriptionSource.Create("https://subscription.example/test"); settings.Save();
        var window=new MainWindow(settings,Path.Combine(scratch,"server-picker.log")) { ShowInTaskbar=false };
        object Field(string name)=>typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
        async Task Idle() { var gate=(SemaphoreSlim)Field("_operations"); await gate.WaitAsync(); gate.Release(); }
        var core=(CoreProcessManager)Field("_core");
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Idle();
            Check(!Descendants<Button>(window).Any(button=>button.Name=="ChooseVpnServerButton"),"connection card is informational and has no duplicate server picker");
            ((Button)Field("SubscriptionTabButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            var serverView=(DependencyObject)((ContentControl)Field("SubscriptionServersHost")).Content;
            var rows=Descendants<StackPanel>(serverView).Single(item=>item.Name=="VpnServerRows");
            var search=Descendants<TextBox>(serverView).Single(item=>item.Name=="VpnServerSearch");
            Check(rows.Children.OfType<Button>().Count()==40,"subscription page contains all forty servers");
            search.Text="Сервер 40"; window.UpdateLayout();
            var result=rows.Children.OfType<Button>().Single();
            Check(result.Tag!.ToString()==names[^1],"page search finds the final server");
            var label=Descendants<TextBlock>(result).Single(item=>item.Name=="VpnServerTitle");
            var labelBounds=label.TransformToAncestor(result).TransformBounds(new Rect(label.RenderSize));
            Check(labelBounds.Top>=0&&labelBounds.Bottom<=result.ActualHeight&&label.ActualHeight>label.FontSize*1.5,"long names wrap inside the server row");
            result.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Check(settings.ProfileSelections[path]==names[^1] && settings.ProfileSubscriptions[path].SelectedServer==names[^1]
                && ((TextBlock)Field("EndpointText")).Text==names[^1],"choosing from the subscription page updates the card and both stored selection fields");
            Check(AppSettings.Load(settingsPath).ProfileSelections[path]==names[^1] && File.ReadAllText(path)==original,"offline server choice survives reload and does not rewrite routing rules");
            Check(settings.ServerSelectionRevision==1 && AppSettings.Load(settingsPath).ServerSelectionRevision==1,"offline selection persists a new cache generation together with its server");
            var reopened=new MainWindow(AppSettings.Load(settingsPath),Path.Combine(scratch,"reopened.log")) { ShowInTaskbar=false };
            try
            {
                reopened.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var gate=(SemaphoreSlim)typeof(MainWindow).GetField("_operations",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(reopened)!; await gate.WaitAsync(); gate.Release();
                var endpoint=(TextBlock)typeof(MainWindow).GetField("EndpointText",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(reopened)!;
                Check(endpoint.Text==names[^1],"reopening the app shows the saved server while VPN is stopped");
            }
            finally { typeof(MainWindow).GetField("_allowClose",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(reopened,true); reopened.Close(); }

            // Model a running owned process while its controller is an in-memory
            // fixture. No tunnel, remote proxy or user process is involved.
            await core.StartAsync(CoreProcessManager.CreateStartInfo(fakeExe,path));
            var state=names[0]; var puts=0;
            using var diagnostics=new CoreDiagnostics(new ServerSelectionHandler(async(request,token)=>
            {
                var route=Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
                if(request.Method==HttpMethod.Put)
                {
                    using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); state=body.RootElement.GetProperty("name").GetString()!; puts++;
                    return new(HttpStatusCode.NoContent);
                }
                var bodyText=route=="/proxies/VPN"?JsonSerializer.Serialize(new { type="Selector",now=state,all=names })
                    :route=="/traffic"?"{\"up\":1,\"down\":2}\n":route=="/connections"?"{\"connections\":[]}":"{\"type\":\"Socks5\"}";
                return new(HttpStatusCode.OK) { Content=new StringContent(bodyText) };
            }));
            (Field("_diagnostics") as CoreDiagnostics)?.Dispose();
            typeof(MainWindow).GetField("_diagnostics",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,diagnostics);
            var pid=core.TrackedProcessId;
            Check(await window.SelectVpnServerAsync(names[20]) && state==names[20] && puts==1 && core.TrackedProcessId==pid,"online choice uses the owned controller without restarting its process");
            Check(settings.ProfileSelections[path]==names[20] && ((TextBlock)Field("EndpointText")).Text==names[20],"online choice is persisted only after the controller confirms it");
            var blocked=Path.Combine(scratch,"blocked-server-settings"); Directory.CreateDirectory(blocked);
            var stableRevision=settings.ServerSelectionRevision;
            typeof(AppSettings).GetField("_storagePath",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(settings,blocked);
            var dismiss=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(60) };
            dismiss.Tick+=(_,_)=> { var notice=Application.Current.Windows.Cast<Window>().FirstOrDefault(w=>w!=window && w.Title=="MorphocyteOS"); if(notice is not null) { dismiss.Stop(); notice.Close(); } };
            try
            {
                dismiss.Start(); var changed=await window.SelectVpnServerAsync(names[30]); dismiss.Stop();
                Check(!changed && state==names[20] && settings.ProfileSelections[path]==names[20] && settings.ProfileSubscriptions[path].SelectedServer==names[20]
                    && settings.ServerSelectionRevision==stableRevision,"settings write failure restores the previous controller selection, cache generation and metadata");
            }
            finally { dismiss.Stop(); typeof(AppSettings).GetField("_storagePath",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(settings,settingsPath); }
            await core.StopAsync();

            // Native sing-box controller, loopback only; no TUN or forwarded traffic.
            var native=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../Packaging/Engine/sing-box.exe"));
            var runtimePath=Path.Combine(scratch,"server-picker-native.json"); File.WriteAllText(runtimePath,runtime);
            var nativeCore=new CoreProcessManager();
            try
            {
                await nativeCore.StartAsync(CoreProcessManager.CreateStartInfo(native,runtimePath));
                Check(await nativeApi.ReadSelectionAsync("VPN",default)==names[^1],"actual sing-box starts on the remembered fortieth server without TUN");
                var nativePid=nativeCore.TrackedProcessId;
                Check(await nativeApi.RestoreSelectionAsync("VPN",names[10],default)
                    && await nativeApi.ReadSelectionAsync("VPN",default)==names[10] && nativeCore.TrackedProcessId==nativePid,"actual sing-box switches Unicode server names through its API without a restart");
                await nativeCore.StopAsync();
                var offlineRuntime=SingBoxConfig.Build(document,nativeApi.Port,nativeApi.Secret,false,includeTun:false,selectedServer:names[^1],selectionCacheId:"test-selection-1");
                using(var first=JsonDocument.Parse(runtime)) using(var second=JsonDocument.Parse(offlineRuntime))
                    Check(first.RootElement.GetProperty("experimental").GetProperty("cache_file").GetProperty("path").GetString()
                        ==second.RootElement.GetProperty("experimental").GetProperty("cache_file").GetProperty("path").GetString(),"server choice keeps the persistent FakeIP database in the same location");
                File.WriteAllText(runtimePath,offlineRuntime);
                await nativeCore.StartAsync(CoreProcessManager.CreateStartInfo(native,runtimePath));
                Check(await nativeApi.ReadSelectionAsync("VPN",default)==names[^1],"an old real selector cache cannot override a newer offline server choice on restart");
            }
            finally { await nativeCore.StopAsync(); }
        }
        finally { await core.StopAsync(); typeof(MainWindow).GetField("_allowClose",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true); window.Close(); }
        return count;
    }
}

internal sealed class ServerSelectionHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token);
}
