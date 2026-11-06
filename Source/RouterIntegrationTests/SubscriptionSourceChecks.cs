using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

internal partial class Program
{
    private static async Task<int> RunSubscriptionSourceChecks(string scratch, string fakeExe)
    {
        var count=0;
        void Check(bool ok,string text) { if(!ok) throw new Exception("FAIL: "+text); count++; Console.WriteLine("PASS: "+text); }
        const string oldUrl="https://subscription.example/old/private-token";
        const string newUrl="https://subscription.example/new/private-token";
        const string uiUrl="https://subscription.example/ui/private-token";
        const string original="morphocyte-fake-api: startup-only\nproxies:\n  - {name: Beta, type: socks5, server: old.example, port: 1080}\nproxy-groups:\n  - {name: VPN, type: select, proxies: [Beta]}\nrules:\n  - DOMAIN-SUFFIX,keep.example,VPN\n  - MATCH,DIRECT\n";
        const string fresh="proxies:\n  - {name: Beta, type: socks5, server: new.example, port: 1080}\n  - {name: Gamma, type: socks5, server: other.example, port: 1080}\nproxy-groups:\n  - {name: VPN, type: select, proxies: [Beta, Gamma]}\nrules:\n  - MATCH,DIRECT\n";
        var settingsPath=Path.Combine(scratch,"source-settings.json"); var profilePath=Path.Combine(scratch,"source-profile.yaml");
        var emptyMerge=SubscriptionRefresh.Merge(profilePath,BundledResources.TemplateText,SubscriptionImporter.ParseContent(fresh),"");
        var mergedYaml=new YamlStream(); mergedYaml.Load(new StringReader(emptyMerge.Yaml));
        var mergedRoot=(YamlMappingNode)mergedYaml.Documents[0].RootNode;
        var vpnGroup=((YamlSequenceNode)mergedRoot.Children[new YamlScalarNode("proxy-groups")]).Children.OfType<YamlMappingNode>()
            .Single(g=>g.Children[new YamlScalarNode("name")].ToString()=="VPN");
        Check(emptyMerge.SelectedServer=="Beta" && ((YamlSequenceNode)vpnGroup.Children[new YamlScalarNode("proxies")]).Children[0].ToString()=="Beta",
            "applying a subscription to the empty template selects an imported VPN server instead of keeping DIRECT");
        File.WriteAllText(profilePath,original);
        var settings=AppSettings.Load(settingsPath); settings.CorePath=fakeExe; settings.ConfigPath=profilePath; settings.AutoCheckUpdates=false; settings.AutoReconnect=false;
        settings.ProfileSubscriptions[profilePath]=SubscriptionSource.Create(oldUrl,sendHardwareId:false,allowPartialImport:false);
        settings.ProfileSubscriptions[profilePath].SelectedServer="Beta"; settings.Save();
        var window=new MainWindow(settings,Path.Combine(scratch,"source.log")) { ShowInTaskbar=false };
        object Field(string name)=>typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
        var core=(CoreProcessManager)Field("_core");
        async Task Idle() { var gate=(SemaphoreSlim)Field("_operations"); await gate.WaitAsync(); gate.Release(); }
        Task<(bool Applied,string Message)> Change(string url,CancellationToken token,HttpClient client)=>window.ApplySubscriptionSourceAsync(url,token,client);
        string? identity=null; var requests=0;
        using var good=new HttpClient(new SourceCheckHandler((request,token)=>
        {
            requests++;
            var sent=request.Headers.GetValues("x-hwid").Single();
            Check(Guid.TryParseExact(sent,"D",out _) && (identity is null || identity==sent),"source replacement sends the same app ID from isolated storage");
            identity=sent;
            Check(settings.ProfileSubscriptions[profilePath].ReadUrl()==oldUrl,"new URL is not persisted before downloaded servers validate");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(fresh) });
        }));
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Idle();
            using var forbidden=new HttpClient(new SourceCheckHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
            var rejected=await Change(newUrl,default,forbidden);
            Check(!rejected.Applied && rejected.Message.Contains("403") && !rejected.Message.Contains("private-token"),"HTTP failure returns useful inline feedback without leaking the source URL");
            Check(settings.ProfileSubscriptions[profilePath].ReadUrl()==oldUrl && File.ReadAllText(profilePath)==original,"failed download preserves both old URL and old servers");
            using(var canceled=new CancellationTokenSource())
            {
                canceled.Cancel(); var result=await Change(newUrl,canceled.Token,good);
                Check(!result.Applied && requests==0 && File.ReadAllText(profilePath)==original,"canceled source change does not download or overwrite a working subscription");
            }
            var changed=await Change(newUrl,default,good);
            Check(changed.Applied && File.ReadAllText(profilePath).Contains("new.example") && !File.ReadAllText(profilePath).Contains("old.example"),"applying a new source immediately replaces the actual server list");
            Check(AppSettings.Load(settingsPath).ProfileSubscriptions[profilePath].ReadUrl()==newUrl
                && settings.ProfileSubscriptions[profilePath].UpdatedAt.HasValue && settings.ProfileSubscriptions[profilePath].AllowPartialImport
                && settings.ProfileSubscriptions[profilePath].SendHardwareId,"new source and both automatic subscription defaults persist together");
            Check(File.ReadAllText(profilePath).Contains("keep.example,VPN") && ((TextBlock)Field("EndpointText")).Text=="Beta","source replacement preserves routing and refreshes the selected connection card");
            var stable=File.ReadAllText(profilePath);
            using var nativeBad=new HttpClient(new SourceCheckHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(fresh.Replace("new.example","INVALID.example")) })));
            var bad=await Change(uiUrl,default,nativeBad);
            Check(!bad.Applied && settings.ProfileSubscriptions[profilePath].ReadUrl()==newUrl && File.ReadAllText(profilePath)==stable,"native validation failure cannot replace the remembered source or servers");

            // Drive the real modal through failure, retry and successful apply.
            using var uiClient=new HttpClient(new SourceCheckHandler((request,_)=>Task.FromResult(new HttpResponseMessage(
                request.RequestUri!.AbsolutePath.StartsWith("/failed/")?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK)
                { Content=new StringContent(fresh.Replace("new.example","ui.example")) })));
            var stage=0; Exception? uiFailure=null; var clock=System.Diagnostics.Stopwatch.StartNew();
            var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(60) };
            timer.Tick+=(_,_)=>
            {
                var dialog=Application.Current.Windows.Cast<Window>().FirstOrDefault(w=>w.Title=="ПОДПИСКА");
                if(dialog is null) return;
                try
                {
                    if(clock.Elapsed>TimeSpan.FromSeconds(20)) throw new Exception("Source modal did not finish");
                    var input=Descendants<TextBox>(dialog).Single(t=>t.Name=="SubscriptionUrlInput");
                    var button=Descendants<Button>(dialog).Single(b=>b.Name=="ApplySubscriptionSourceButton");
                    var feedback=Descendants<TextBlock>(dialog).Single(b=>b.Name=="SubscriptionSourceFeedback");
                    if(stage==0)
                    {
                        if(clock.Elapsed<TimeSpan.FromMilliseconds(500)) return;
                        Check(input.Text==newUrl,"source dialog opens with the actual saved subscription URL");
                        Check(!Descendants<CheckBox>(dialog).Any(),"source dialog has neither of the two removed subscription switches");
                        dialog.UpdateLayout(); CaptureInteraction(dialog,Path.Combine(scratch,"subscription-source-dialog.png"));
                        Check(input.ActualWidth>350 && button.ActualWidth>140 && button.IsVisible,"source input and load/apply action retain usable bounds");
                        stage=1; input.Text="https://subscription.example/failed/private-token"; button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if(stage==1 && button.IsEnabled && feedback.Text.Contains("503"))
                    {
                        Check(dialog.IsVisible && input.IsEnabled && settings.ProfileSubscriptions[profilePath].ReadUrl()==newUrl,"failed apply keeps the source dialog open and its previous source intact");
                        stage=2; input.Text=uiUrl; button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                }
                catch(Exception error) { uiFailure=error; timer.Stop(); dialog.Close(); }
            };
            timer.Start(); var applied=window.ShowSubscriptionSource(window,uiClient); timer.Stop(); await Idle();
            if(uiFailure is not null) throw uiFailure;
            Check(applied && stage==2 && settings.ProfileSubscriptions[profilePath].ReadUrl()==uiUrl
                && File.ReadAllText(profilePath).Contains("ui.example"),"real source dialog retries and applies the new source without a second refresh click");

            var beforeCancel=File.ReadAllText(profilePath); var started=false;
            using var slow=new HttpClient(new SourceCheckHandler(async(_,token)=> { started=true; await Task.Delay(Timeout.Infinite,token); return new(HttpStatusCode.OK); }));
            var cancelStage=0;
            var cancelTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(60) };
            cancelTimer.Tick+=(_,_)=>
            {
                var dialog=Application.Current.Windows.Cast<Window>().FirstOrDefault(w=>w.Title=="ПОДПИСКА"); if(dialog is null) return;
                if(cancelStage==0)
                {
                    cancelStage=1;
                    Descendants<TextBox>(dialog).Single(t=>t.Name=="SubscriptionUrlInput").Text="https://subscription.example/canceled/private-token";
                    Descendants<Button>(dialog).Single(b=>b.Name=="ApplySubscriptionSourceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else if(started) { cancelTimer.Stop(); dialog.Close(); }
            };
            cancelTimer.Start(); var canceledUi=window.ShowSubscriptionSource(window,slow); cancelTimer.Stop(); await Idle();
            Check(!canceledUi && File.ReadAllText(profilePath)==beforeCancel && settings.ProfileSubscriptions[profilePath].ReadUrl()==uiUrl,"closing the source dialog cancels its download without a later source replacement");

            settings.ProfileSubscriptions[profilePath].SendHardwareId=false;
            settings.ProfileSubscriptions[profilePath].AllowPartialImport=false;
            using var legacy=new HttpClient(new SourceCheckHandler((request,_)=>
            {
                Check(request.Headers.GetValues("x-hwid").Single()==identity,"refreshing legacy subscription metadata also sends the existing app ID automatically");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(fresh.Replace("new.example","ui.example")) });
            }));
            Check(await window.RefreshSubscriptionAsync(default,legacy) && settings.ProfileSubscriptions[profilePath].SendHardwareId
                && settings.ProfileSubscriptions[profilePath].AllowPartialImport,"successful refresh upgrades both old disabled flags to the automatic defaults");
            beforeCancel=File.ReadAllText(profilePath);

            // Force a settings-save error after YAML commit; both must roll back.
            var blocked=Path.Combine(scratch,"source-blocked-settings"); Directory.CreateDirectory(blocked);
            typeof(AppSettings).GetField("_storagePath",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(settings,blocked);
            using var finalClient=new HttpClient(new SourceCheckHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(fresh) })));
            try
            {
                var failedSave=await Change(newUrl,default,finalClient);
                Check(!failedSave.Applied && settings.ProfileSubscriptions[profilePath].ReadUrl()==uiUrl
                    && File.ReadAllText(profilePath)==beforeCancel,"settings write failure restores both the previous servers and their original source association");
            }
            finally { typeof(AppSettings).GetField("_storagePath",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(settings,settingsPath); }
        }
        finally
        {
            await core.StopAsync(); typeof(MainWindow).GetField("_allowClose",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true); window.Close();
        }
        return count;
    }
}

internal sealed class SourceCheckHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token);
}
