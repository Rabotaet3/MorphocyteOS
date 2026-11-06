using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunSubscriptionReliabilityChecks(string scratch,string fakeExe)
    {
        scratch=Path.Combine(scratch,"subscription-reliability");
        Directory.CreateDirectory(scratch);
        var count=0;
        void Check(bool ok,string text) { if(!ok) throw new Exception("FAIL: "+text); count++; Console.WriteLine("PASS: "+text); }
        var requests=0;
        using(var list=new VpnServerListView(new[]{"First","Second"},"First",(_,_)=> { requests++; return Task.FromResult(70); },_=>Task.FromResult(true)))
        {
            var host=new Window { Content=list,Width=650,Height=400,ShowInTaskbar=false }; host.Show();
            try
            {
                var ping=Descendants<Button>(list).Single(item=>item.Name=="VpnServerPing");
                Func<Task> failedCleanup=()=>Task.FromException(new IOException("Fixture cleanup failed"));
                list.ChecksFinished+=failedCleanup;
                ping.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await list.StopChecksAsync();
                Check(ping.Content!.ToString()=="PING"&&Descendants<TextBlock>(list).Any(item=>item.Text.Contains("Не удалось завершить")),"failed ping cleanup restores the button and reports an issue instead of faulting the UI event handler");
                ping.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await list.StopChecksAsync();
                Check(requests==4,"a cleanup failure cannot leave the latency list stuck in its previous batch");
                list.ChecksFinished-=failedCleanup;
                var completed=new List<int>();
                list.ChecksFinished+=async()=> { await Task.Delay(60); completed.Add(1); };
                list.ChecksFinished+=()=> { completed.Add(2); return Task.CompletedTask; };
                ping.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await list.StopChecksAsync();
                Check(completed.SequenceEqual(new[]{1,2}),"all asynchronous ping cleanup subscribers finish in order before the batch is released");
            }
            finally { host.Close(); }
        }

        const string yaml="proxies:\n - {name: Beta, type: socks5, server: old.example, port: 1080}\nproxy-groups:\n - {name: VPN, type: select, proxies: [Beta]}\nrules:\n - MATCH,DIRECT\n";
        var profilePath=Path.Combine(scratch,"reliability-profile.yaml"); File.WriteAllText(profilePath,yaml);
        var settingsPath=Path.Combine(scratch,"reliability-settings.json"); var settings=AppSettings.Load(settingsPath);
        settings.ConfigPath=profilePath; settings.CorePath=fakeExe; settings.AutoCheckUpdates=false; settings.AutoReconnect=false;
        settings.ProfileSubscriptions[profilePath]=SubscriptionSource.Create("https://subscription.example/reliability"); settings.Save();
        var legacyIdentity=SubscriptionDeviceIdentity.GetOrCreate(settings.StorageDirectory,()=>null);
        var window=new MainWindow(settings,Path.Combine(scratch,"subscription-reliability.log")) { ShowInTaskbar=false };
        T Field<T>(string name)=>(T)typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
        async Task StopPing()=>await (Task)typeof(MainWindow).GetMethod("DisposeSubscriptionPingAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
        HttpResponseMessage Response(string title) {
            var response=new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(yaml.Replace("old.example","new.example")) };
            response.Headers.TryAddWithoutValidation("profile-title",title); return response;
        }
        var releaseStop=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedCore=new CoreProcessManager(async process=> {
            await releaseStop.Task;
            if(!process.HasExited) process.Kill(entireProcessTree:true);
            await process.WaitForExitAsync();
        },TimeSpan.FromMilliseconds(80));
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var operations=Field<SemaphoreSlim>("_operations"); await operations.WaitAsync(); operations.Release();
            foreach(var rejected in new[]{false,true})
            {
                var responseGate=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var delayed=new HttpClient(new SourceCheckHandler((_,_)=> { started.TrySetResult(); return responseGate.Task; }));
                var oldRequest=window.RefreshSubscriptionInformationAsync(delayed); await started.Task;
                using var fresh=new HttpClient(new SourceCheckHandler((_,_)=>Task.FromResult(Response(rejected?"Newest subscription":"Fresh subscription"))));
                Check(await window.RefreshSubscriptionAsync(default,fresh),"a complete subscription refresh can finish while an older header-only request is pending");
                responseGate.SetResult(rejected?new HttpResponseMessage(HttpStatusCode.Forbidden):Response("Stale subscription")); await oldRequest;
                var expected=rejected?"Newest subscription":"Fresh subscription";
                Check(settings.ProfileSubscriptions[profilePath].Metadata?.Title==expected&&AppSettings.Load(settingsPath).ProfileSubscriptions[profilePath].Metadata?.Title==expected,
                    rejected?"a late metadata failure preserves the newer subscription and its saved information":"a late metadata response cannot overwrite the newer complete subscription response");
                Check(!Field<TextBlock>("SubscriptionMessageText").Text.Contains("Не удалось"),"obsolete metadata requests cannot replace the current page with an error message");
            }
            await delayedCore.StartAsync(CoreProcessManager.CreateStartInfo(fakeExe,profilePath));
            var session=new ServerPingSession(ClashConfigDocument.Load(profilePath),delayedCore);
            typeof(MainWindow).GetField("_subscriptionPing",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,session);
            var refused=false;
            try { await StopPing(); } catch(IOException) { refused=true; }
            Check(refused&&delayedCore.HasTrackedProcess&&ReferenceEquals(Field<ServerPingSession>("_subscriptionPing"),session),"an unconfirmed probe stop retains process ownership and the session for a retry");
            releaseStop.TrySetResult();
            await Task.WhenAll(session.DisposeAsync().AsTask(),session.DisposeAsync().AsTask());
            await StopPing();
            Check(!delayedCore.HasTrackedProcess&&Field<object?>("_subscriptionPing") is null,"concurrent cleanup and retry stop the owned probe once and clear the session only after confirmation");
            await session.DisposeAsync();
            Check(!delayedCore.HasTrackedProcess,"repeated disposal after successful probe cleanup is harmless");
            if(SubscriptionDeviceIdentity.ReadWindowsIdentity() is { } computerIdentity)
            {
                var host=new Window { Width=650,Height=320,ShowInTaskbar=false,Owner=window };
                var card=UtilityDialogs.DeviceIdentityCard(window,host); host.Content=card; host.Show();
                try
                {
                    var migrate=Descendants<Button>(card).Single(item=>item.Name=="UseComputerIdentityButton");
                    Check(migrate.IsVisible&&migrate.IsEnabled,"legacy users can explicitly switch to the computer identity from settings");
                    void Confirm(bool accept)
                    {
                        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(80) };
                        timer.Tick+=(_,_)=> {
                            var prompt=Application.Current.Windows.Cast<Window>().SingleOrDefault(item=>item.Title=="ID УСТРОЙСТВА");
                            if(prompt is null) return;
                            timer.Stop(); Descendants<Button>(prompt).Single(item=>item.Content?.ToString()==(accept?"ПЕРЕЙТИ":"ОТМЕНА")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        };
                        timer.Start(); try { migrate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } finally { timer.Stop(); }
                    }
                    Confirm(false);
                    Check(SubscriptionDeviceIdentity.GetOrCreate(settings.StorageDirectory)==legacyIdentity,"canceling the migration prompt preserves the previous identity");
                    var profileBefore=File.ReadAllText(profilePath); var settingsBefore=File.ReadAllText(settingsPath);
                    Confirm(true);
                    Check(SubscriptionDeviceIdentity.GetOrCreate(settings.StorageDirectory)==computerIdentity&&migrate.Visibility==Visibility.Collapsed,"confirming migration uses this PC's ID and hides the obsolete migration action");
                    Check(File.ReadAllText(profilePath)==profileBefore&&File.ReadAllText(settingsPath)==settingsBefore&&!Field<CoreProcessManager>("_core").HasTrackedProcess,"identity migration neither rewrites VPN profiles nor starts a connection");
                    CaptureInteraction(host,Path.Combine(scratch,"computer-identity-settings.png"));
                }
                finally { host.Close(); }
            }
        }
        finally
        {
            releaseStop.TrySetResult(); await delayedCore.StopAsync();
            typeof(MainWindow).GetField("_allowClose",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true); window.Close();
        }
        return count;
    }
}
