using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunSubscriptionMetadataChecks(string scratch,string fakeExe)
    {
        var count=0;
        void Check(bool ok,string message) { if(!ok) throw new Exception("FAIL: "+message); count++; Console.WriteLine("PASS: "+message); }
        const string title="🚀 Тестовая подписка";
        const string announcement="ID: fixture · Устройства 6/6\nОбновляйте подписку для получения актуального списка серверов.";
        HttpResponseMessage Response()
        {
            var response=new HttpResponseMessage(HttpStatusCode.OK) { Content=new UnreadableSubscriptionBody() };
            response.Headers.TryAddWithoutValidation("profile-title","base64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(title)));
            response.Headers.TryAddWithoutValidation("announce","base64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(announcement)));
            response.Headers.TryAddWithoutValidation("subscription-userinfo","upload=1073741824; download=7516192768; total=1099511627776; expire=1796504400");
            return response;
        }
        using(var response=Response())
        {
            var data=SubscriptionMetadata.FromResponse(response);
            Check(data.Title==title && data.Announcement==announcement,"subscription title and announcement decode from provider headers as plain text");
            Check(data.Upload==1073741824 && data.Download==7516192768 && data.Total==1099511627776 && data.Expires==1796504400,"traffic and expiry use provider values without being inferred from the client");
            Check(data.TrafficLabel.Contains("8") && data.UsagePercent is >0 and <1 && data.ExpiryLabel.Contains("2026"),"traffic sums both directions and shows a bounded quota indicator");
        }
        using(var invalid=new HttpResponseMessage(HttpStatusCode.OK))
        {
            invalid.Headers.TryAddWithoutValidation("subscription-userinfo","upload=-1;download=9223372036854775808;total=abc;expire=9999999999999999");
            invalid.Headers.TryAddWithoutValidation("profile-title","base64:%%%");
            var data=SubscriptionMetadata.FromResponse(invalid);
            Check(data.Title=="" && data.Download is null && data.Upload is null && data.Expires is null,"malformed or overflowing optional headers never break profile import");
        }
        Check(new SubscriptionMetadata { Download=1 }.TrafficLabel.Contains("не указан"),"an absent quota is not presented as an unlimited subscription");
        Check(new SubscriptionMetadata { Upload=long.MaxValue,Download=long.MaxValue,Total=1 }.UsagePercent==100,"large provider counters cannot overflow quota calculations");

        var path=Path.Combine(scratch,"metadata-profile.yaml");
        const string yaml="mixed-port: 0\nproxies:\n - {name: Server, type: socks5, server: 127.0.0.1, port: 1080}\nproxy-groups:\n - {name: VPN, type: select, proxies: [Server]}\nrules:\n - MATCH,DIRECT\n";
        File.WriteAllText(path,yaml);
        var settingsPath=Path.Combine(scratch,"metadata-settings.json"); var settings=AppSettings.Load(settingsPath);
        settings.ConfigPath=path; settings.CorePath=fakeExe; settings.AutoReconnect=false; settings.AutoCheckUpdates=false;
        var source=SubscriptionSource.Create("https://subscription.example/fixture"); settings.ProfileSubscriptions[path]=source; settings.Save();
        var window=new MainWindow(settings,Path.Combine(scratch,"metadata.log")) { ShowInTaskbar=false };
        T Field<T>(string name)=>(T)typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
        var core=Field<CoreProcessManager>("_core");
        var identities=new List<string>();
        using var client=new HttpClient(new ServerSelectionHandler((request,token)=> {
            if(request.Method!=HttpMethod.Get) throw new Exception("Unexpected metadata mutation request");
            identities.Add(request.Headers.GetValues("x-hwid").Single());
            return Task.FromResult(Response());
        }));
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var operations=Field<SemaphoreSlim>("_operations"); await operations.WaitAsync(); operations.Release();
            Field<Button>("SubscriptionTabButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await operations.WaitAsync(); operations.Release();
            await window.RefreshSubscriptionInformationAsync(client);
            Check(Field<TextBlock>("SubscriptionTitleEmoji").Text+" "+Field<TextBlock>("SubscriptionTitleText").Text==title && Field<TextBlock>("SubscriptionMessageText").Text.Contains("6/6"),"main window displays subscription name and the provider's device information");
            Check(AppSettings.Load(settingsPath).ProfileSubscriptions[path].Metadata?.Title==title,"subscription information persists between application launches");
            Check(File.ReadAllText(path)==yaml && !core.HasTrackedProcess && source.ReadUrl()=="https://subscription.example/fixture","refreshing metadata never replaces the profile, changes its URL or starts a VPN");
            await window.RefreshSubscriptionInformationAsync(client);
            Check(identities.Count==2 && identities[0]==identities[1],"metadata refresh reuses the same MorphocyteOS ID instead of consuming a new device slot");
            Field<Button>("SubscriptionDetailsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(230);
            foreach(var scale in new[]{.8,1d,1.5})
            {
                window.ApplyInterfaceScale(scale); window.Width=window.MinWidth; window.Height=window.MinHeight;
                await Task.Delay(150); window.UpdateLayout();
                var banner=Field<Border>("SubscriptionBanner");
                var traffic=Field<TextBlock>("SubscriptionTrafficText"); var expiry=Field<TextBlock>("SubscriptionExpiryText");
                var refresh=Field<Button>("ReloadSubscriptionButton");
                Rect Bounds(FrameworkElement element)=>element.TransformToAncestor(banner).TransformBounds(new Rect(element.RenderSize));
                Check(!Bounds(traffic).IntersectsWith(Bounds(expiry)) && Bounds(expiry).Right<=banner.ActualWidth
                    && Bounds(refresh).Right<=banner.ActualWidth,$"subscription counters and refresh control fit at {scale*100:0}% scale");
                CaptureInteraction(window,Path.Combine(scratch,$"subscription-banner-{scale*100:0}.png"));
            }
            var previous=source.Metadata;
            using var denied=new HttpClient(new ServerSelectionHandler((request,token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
            await window.RefreshSubscriptionInformationAsync(denied);
            Check(ReferenceEquals(source.Metadata,previous) && Field<TextBlock>("SubscriptionMessageText").Text.Contains("Не удалось"),"failed metadata requests preserve cached values and report a safe error");
            var blocker=Path.Combine(scratch,"metadata-write-blocker"); Directory.CreateDirectory(blocker);
            typeof(AppSettings).GetField("_storagePath",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(settings,blocker);
            await window.RefreshSubscriptionInformationAsync(client);
            typeof(AppSettings).GetField("_storagePath",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(settings,settingsPath);
            Check(ReferenceEquals(source.Metadata,previous),"metadata save failure restores the previous in-memory snapshot");
        }
        finally
        {
            window.ApplyInterfaceScale(1);
            typeof(MainWindow).GetField("_allowClose",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true); window.Close();
        }
        return count;
    }
}

// Header-only refresh must never read, parse or import the response body.
internal sealed class UnreadableSubscriptionBody : HttpContent
{
    protected override bool TryComputeLength(out long length) { length=0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context) => throw new Exception("Subscription body was read");
}
