using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunServerSortingChecks(string scratch)
    {
        var count=RunInformationEntryChecks(scratch);
        ThemeManager.Apply("Океан");
        void Check(bool ok,string label) { if(!ok) throw new Exception("FAIL: "+label); count++; Console.WriteLine("PASS: "+label); }
        string[] names=["Сервер 10", "Сервер 2", "Сервер 1", "Сервер 7"];
        var values=new Dictionary<string,int?> { [names[0]]=10,[names[1]]=30,[names[2]]=90,[names[3]]=null };
        var yaml="proxies:\n - {name: Alpha, type: socks5, server: 192.0.2.1, port: 1080}\n - {name: Beta, type: socks5, server: 192.0.2.2, port: 1080}\nproxy-groups:\n - {name: VPN, type: select, proxies: [Beta, Alpha]}\nrules:\n - MATCH,DIRECT\n";
        var profile=ClashConfigDocument.Parse(Path.Combine(scratch,"sorting.yaml"),yaml);
        Check(VpnServerCatalog.Choices(profile,subscriptionOrder:true).SequenceEqual(new[]{"Alpha","Beta"}),"legacy profiles with the selected server moved first recover the subscription's node order");
        var merged=SubscriptionRefresh.Merge(profile.Path,yaml,SubscriptionImporter.ParseContent(yaml),"Beta");
        Check(merged.SelectedServer=="Beta" && VpnServerCatalog.Choices(ClashConfigDocument.Parse(profile.Path,merged.Yaml)).SequenceEqual(new[]{"Alpha","Beta"}),"refresh preserves the selected server independently of provider order");
        var presentationNode=new YamlDotNet.RepresentationModel.YamlMappingNode {
            {"type","vless"},{"network","tcp"},{"tls","true"},{"reality-opts",new YamlDotNet.RepresentationModel.YamlMappingNode { {"public-key","fixture"} }}
        };
        var display=VpnServerPresentation.Describe("№2 🇫🇮 Финляндия 🔗 Bridge",presentationNode);
        Check(display.CountryCode=="FI" && display.Title=="№2 Финляндия 🔗 Bridge" && display.Details=="VLESS / TCP / REALITY","flag glyphs become separate country metadata and connection details come from the configuration");
        var plain=VpnServerPresentation.Describe("№5 Швеция Bridge",presentationNode);
        Check(plain.CountryCode=="SE" && plain.Title=="№5 Швеция Bridge","plain country names also receive a country badge without renaming the server");
        Check(VpnServerPresentation.Describe("Авто",null).CountryCode is null,"unknown locations remain unspecified instead of receiving an invented country");
        var automatic=VpnServerPresentation.Describe("🇸🇴 №1 🇸🇴 EU Авто",new YamlDotNet.RepresentationModel.YamlMappingNode { {"type",XrayFullProfile.NodeType} });
        Check(automatic.CountryCode=="AUTO" && automatic.Title=="№1 EU Авто" && CountryBadge.Create(automatic.CountryCode) is Image,
            "automatic profiles use a star badge and remove decorative repeated flag glyphs from their display title");
        var hy=new YamlDotNet.RepresentationModel.YamlMappingNode { {"type","hysteria2"} };
        Check(VpnServerPresentation.Describe("Node",hy).Details=="HYSTERIA2 / QUIC / TLS","Hysteria2 is not mislabeled TCP or Reality");
        var xray=new YamlDotNet.RepresentationModel.YamlMappingNode { {"type","xray"}, {"xray-link","vless://fixture@vpn.example:443?type=xhttp&security=reality#Server"} };
        Check(VpnServerPresentation.Describe("Node",xray).Details=="VLESS / XHTTP / REALITY","Xray connection labels expose protocol and transport without leaking the share link");
        Check(CountryBadge.Create("SE") is System.Windows.Controls.Image && CountryBadge.Create("FI") is System.Windows.Controls.Image
            && CountryBadge.Create("CH") is System.Windows.Controls.Image,"country flags are actual vector images and do not depend on emoji fonts");

        var settingsPath=Path.Combine(scratch,"sorting-settings.json"); var settings=AppSettings.Load(settingsPath);
        var requests=0; var active=0; var maximum=0;
        using var diagnostics=new CoreDiagnostics(new ServerSelectionHandler(async(request,token)=> {
            if(request.Method!=HttpMethod.Get || !request.RequestUri!.AbsolutePath.EndsWith("/delay")) throw new Exception("Probe attempted to change a selector");
            if(request.Headers.Authorization?.Scheme!="Bearer") throw new Exception("Missing local API authentication");
            requests++; active++; maximum=Math.Max(maximum,active);
            try
            {
                await Task.Delay(350,token);
                var name=Uri.UnescapeDataString(request.RequestUri.AbsolutePath.Split('/')[2]);
                return new HttpResponseMessage(values[name].HasValue?HttpStatusCode.OK:HttpStatusCode.ServiceUnavailable) {
                    Content=new StringContent(values[name] is { } ms?$"{{\"delay\":{ms}}}":"{}")
                };
            }
            finally { active--; }
        }));
        var owner=new Window { Width=700,Height=620,ShowInTaskbar=false }; owner.Show();
        try
        {
            Exception? failed=null; var stage=0; var watch=System.Diagnostics.Stopwatch.StartNew();
            var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(60) };
            timer.Tick+=(_,_)=> {
                var dialog=Application.Current.Windows.Cast<Window>().FirstOrDefault(w=>w.Title=="СЕРВЕРЫ ПОДПИСКИ"); if(dialog is null) return;
                try
                {
                    if(watch.Elapsed>TimeSpan.FromSeconds(15)) throw new Exception("Sorting modal timed out");
                    if(watch.ElapsedMilliseconds<500) return;
                    var rows=Descendants<StackPanel>(dialog).Single(p=>p.Name=="VpnServerRows");
                    var ping=Descendants<Button>(dialog).Single(p=>p.Name=="VpnServerPing");
                    string[] Order()=>rows.Children.OfType<Button>().Select(b=>(string)b.Tag).ToArray();
                    if(stage==0)
                    {
                        Check(Order().SequenceEqual(names),"opening the dialog does not move the selected server to the top");
                        var selected=rows.Children.OfType<Button>().Single(b=>(string)b.Tag==names[1]);
                        Check(System.Windows.Automation.AutomationProperties.GetItemStatus(selected)=="Выбран"&&!Descendants<Border>(selected).Any(b=>b.Name=="VpnServerSelected"),"selected server uses a row highlight without a check box");
                        Check(!Descendants<ComboBox>(dialog).Any(),"server list no longer contains the sorting control");
                        stage=1; ping.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if(stage==2)
                    {
                        CaptureInteraction(dialog,Path.Combine(scratch,"server-sorting-ping.png"));
                        timer.Stop(); dialog.Close();
                    }
                    else if(ping.Content!.ToString()=="PING")
                    {
                        Check(Order().SequenceEqual(names),"ping results preserve provider order and do not move any server");
                        var labels=rows.Children.OfType<Button>().SelectMany(b=>Descendants<TextBlock>(b).Where(t=>t.Name=="VpnServerLatency")).ToArray();
                        Check(labels.Select(t=>t.Text).SequenceEqual(new[]{"10 мс","30 мс","90 мс","Недоступен"}),"measured delays and failures appear at the right side of the correct rows");
                        dialog.Width=dialog.MinWidth; dialog.UpdateLayout();
                        Check(labels.All(label=> { var parent=rows.Children.OfType<Button>().Single(b=>Descendants<TextBlock>(b).Contains(label)); var rect=label.TransformToAncestor(parent).TransformBounds(new Rect(label.RenderSize)); return rect.Right<=parent.ActualWidth && rect.Left>=0; }),"latency labels fit inside rows at the minimum window width");
                        stage=2;
                    }
                    else if(!Order().SequenceEqual(names)) throw new Exception("Rows moved during a running ping batch");
                }
                catch(Exception error) { failed=error; timer.Stop(); dialog.Close(); }
            };
            timer.Start();
            var presentations=names.ToDictionary(name=>name,name=>new VpnServerPresentation(name,"VLESS / TCP / REALITY",name==names[0]?"SE":name==names[1]?"FI":name==names[2]?"DE":"CH"));
            var result=TestServerPicker.Show(owner,names,names[1],probe:diagnostics.CheckProxyAsync,presentations:presentations);
            timer.Stop(); if(failed is not null) throw failed;
            Check(result is null && requests==5 && maximum<=4,"manual probes only read authenticated delay endpoints, use bounded concurrency and do not select a server");

            var started=0; var cancelled=0;
            async Task<int> SlowProbe(string name,CancellationToken token) {
                started++; try { await Task.Delay(10000,token); return 1; }
                catch(OperationCanceledException) { cancelled++; throw; }
            }
            stage=0; watch.Restart(); failed=null;
            timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(60) };
            timer.Tick+=(_,_)=> {
                var dialog=Application.Current.Windows.Cast<Window>().FirstOrDefault(w=>w.Title=="СЕРВЕРЫ ПОДПИСКИ"); if(dialog is null) return;
                try
                {
                    if(watch.Elapsed>TimeSpan.FromSeconds(10)) throw new Exception("Cancellation modal timed out");
                    if(watch.ElapsedMilliseconds<500) return;
                    var ping=Descendants<Button>(dialog).Single(p=>p.Name=="VpnServerPing");
                    if(stage==0) { stage=1; ping.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                    else if(stage==1) { stage=2; ping.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                    else if(stage==2 && ping.Content!.ToString()=="PING")
                    {
                        Check(Descendants<TextBlock>(dialog).Where(t=>t.Name=="VpnServerLatency").All(t=>t.Text=="—"),"stopping a batch does not mark cancelled or queued probes as unreachable");
                        stage=3; ping.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if(stage==3) { timer.Stop(); dialog.Close(); }
                }
                catch(Exception error) { failed=error; timer.Stop(); dialog.Close(); }
            };
            timer.Start(); TestServerPicker.Show(owner,Enumerable.Range(0,12).Select(i=>"Node "+i).ToArray(),"Node 2",probe:SlowProbe); timer.Stop();
            await Task.Delay(100);
            if(failed is not null) throw failed;
            Check(started==8 && cancelled==8,"stop and close cancel active probes, allow a retry and never start queued servers");
            foreach(var scale in new[]{.8,1d,1.25,1.5})
            {
                UiScaleManager.CurrentScale=scale;
                watch.Restart(); failed=null;
                timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(60) };
                var longName="№12 🇫🇮 Финляндия 🔗 Bridge Fragment — Длинное название сервера и дополнительная информация о подключении";
                var info=new Dictionary<string,VpnServerPresentation> { [longName]=VpnServerPresentation.Describe(longName,presentationNode) };
                timer.Tick+=(_,_)=> {
                    var dialog=Application.Current.Windows.Cast<Window>().FirstOrDefault(w=>w.Title=="СЕРВЕРЫ ПОДПИСКИ"); if(dialog is null) return;
                    try
                    {
                        if(watch.Elapsed>TimeSpan.FromSeconds(8)) throw new Exception("Row layout timed out");
                        if(watch.ElapsedMilliseconds<500) return;
                        var row=Descendants<StackPanel>(dialog).Single(p=>p.Name=="VpnServerRows").Children.OfType<Button>().Single();
                        Rect Bounds(FrameworkElement item)=>item.TransformToAncestor(row).TransformBounds(new Rect(item.RenderSize));
                        var title=Descendants<TextBlock>(row).Single(t=>t.Name=="VpnServerTitle");
                        var details=Descendants<TextBlock>(row).Single(t=>t.Name=="VpnServerDetails");
                        var latency=Descendants<TextBlock>(row).Single(t=>t.Name=="VpnServerLatency");
                        var flag=Descendants<FrameworkElement>(row).Single(t=>t.Name=="VpnServerCountry");
                        Check(Bounds(title).Right<=Bounds(latency).Left && Bounds(details).Bottom<=row.ActualHeight && Bounds(title).Left>=Bounds(flag).Right,
                            $"server title, protocol details and ping do not overlap at {scale*100:0}% scale");
                        Check(Math.Abs((Bounds(flag).Top+Bounds(flag).Bottom)/2-row.ActualHeight/2)<1.5,
                            $"country flag remains vertically centered without a checkmark at {scale*100:0}% scale");
                        CaptureInteraction(dialog,Path.Combine(scratch,$"server-row-{scale*100:0}.png")); timer.Stop(); dialog.Close();
                    }
                    catch(Exception error) { failed=error; timer.Stop(); dialog.Close(); }
                };
                timer.Start(); TestServerPicker.Show(owner,new[]{longName},longName,presentations:info); timer.Stop(); if(failed is not null) throw failed;
            }
        }
        finally { UiScaleManager.CurrentScale=1; ThemeManager.Apply("Тёмная морфоцитная"); owner.Close(); }
        return count;
    }
}
