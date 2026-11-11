using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunSubscriptionPageChecks(string scratch,string fakeExe)
    {
        var count=0;
        void Check(bool ok,string text) { if(!ok) throw new Exception("FAIL: "+text); count++; Console.WriteLine("PASS: "+text); }
        var primary=new[]{"№1 EU Авто-сервер + LTE","№2 EU Авто-сервер + LTE (Routing)","Франция Bridge","№41 Германия Bridge","№42 Финляндия Bridge"};
        var names=primary.Concat(new[]{"№1 Германия Bridge","№2 Финляндия Bridge"}).ToArray();
        var labels=names.ToDictionary(name=>name,name=>new VpnServerPresentation(name,"VLESS / TCP / REALITY / JSON",name.Contains("Авто")?"AUTO":name.Contains("Финляндия")?"FI":"DE"));
        var requests=0; var canceled=0;
        ThemeManager.Apply("Аврора");
        using(var list=new VpnServerListView(names,primary[0],async(name,token)=> {
            Interlocked.Increment(ref requests);
            try { await Task.Delay(200,token); return 117; } catch(OperationCanceledException) { Interlocked.Increment(ref canceled); throw; }
        },_=>Task.FromResult(true),labels))
        {
            var host=new Window { Content=list,Width=750,Height=360,ShowInTaskbar=false }; host.Show();
            host.SetResourceReference(Control.BackgroundProperty,"ThemeSurface");
            try
            {
                var rows=Descendants<StackPanel>(list).Single(item=>item.Name=="VpnServerRows");
                var ping=Descendants<Button>(list).Single(item=>item.Name=="VpnServerPing");
                var search=Descendants<TextBox>(list).Single();
                Check(rows.Children.OfType<Button>().Select(item=>item.Tag!.ToString()).SequenceEqual(names),"the list retains exact provider order, including repeated numbering and additional connection variants");
                Check(rows.Children.Count==names.Length&&!Descendants<TextBlock>(rows).Any(item=>item.Name=="AdditionalServerVariants"),"all servers form one continuous list without additional-variant headings");
                ping.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                search.Text="Финляндия";
                await Task.Delay(600);
                Check(rows.Children.OfType<Button>().Count()==2&&Descendants<TextBlock>(rows).Count(item=>item.Name=="VpnServerLatency"&&item.Text=="117 мс")==2,"ping fills each row and filtering during a batch retains measured results");
                search.Clear();
                var beforeSelection=rows.Children.OfType<Button>().ToArray();
                rows.Children.OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                host.UpdateLayout();
                Check(rows.Children.OfType<Button>().Count()==7&&rows.Children.OfType<Button>().Count(item=>System.Windows.Automation.AutomationProperties.GetItemStatus(item)=="Выбран")==1&&!Descendants<Border>(rows).Any(item=>item.Name=="VpnServerSelected"),"selection highlights exactly one server without checkboxes");
                Check(rows.Children.OfType<Button>().SequenceEqual(beforeSelection),"selection updates existing rows instead of replacing controls and losing focus");
                var scroll=Descendants<ScrollViewer>(list).Single(item=>item.Name=="VpnServerScroll");
                scroll.LineDown(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                var offset=scroll.VerticalOffset;
                var firstVisible=(FrameworkElement)rows.Children[(int)offset];
                var presenter=Descendants<ScrollContentPresenter>(scroll).Single();
                Check(offset>=1 && firstVisible.TransformToAncestor(scroll).TransformBounds(new Rect(firstVisible.RenderSize)).Top>=presenter.TransformToAncestor(scroll).TransformBounds(new Rect(presenter.RenderSize)).Top-.5,"scrolling aligns the first visible row instead of cutting its title in half");
                Check(scroll.ClipToBounds&&rows.Children.OfType<Button>().All(item=>item.Opacity==1&&item.IsHitTestVisible),"viewport clips cards at its boundary without hiding the partially visible bottom server");
                var focused=(Button)firstVisible; focused.Focus();
                var beforePing=rows.Children.OfType<Button>().ToArray();
                ping.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(600); host.UpdateLayout();
                Check(rows.Children.OfType<Button>().SequenceEqual(beforePing)&&scroll.VerticalOffset==offset&&focused.IsKeyboardFocused,"completing PING preserves row controls, scroll position and keyboard focus");
                Check(!scroll.Focusable&&scroll.FocusVisualStyle is null,"server viewport cannot acquire the system dotted focus rectangle");
                scroll.ScrollToEnd(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                var finalRow=rows.Children.OfType<Button>().Last();
                var finalBounds=finalRow.TransformToAncestor(presenter).TransformBounds(new Rect(finalRow.RenderSize));
                Check(finalRow.Opacity==1&&finalBounds.Top>=-.5&&finalBounds.Bottom<=presenter.ActualHeight+.5,"the final server remains fully reachable when scrolling to the end");
                scroll.ScrollToTop(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                var motion=(ServerListMotion)typeof(VpnServerListView).GetField("_scrollMotion",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(list)!;
                motion.AnimationsEnabled=()=>true;
                var anchor=(FrameworkElement)rows.Children[0];
                var top=anchor.TranslatePoint(new Point(),scroll).Y;
                motion.ScrollByRows(2);
                var initialShift=((System.Windows.Media.TranslateTransform)rows.RenderTransform).Y;
                Check(motion.IsAnimating&&Math.Abs(anchor.TranslatePoint(new Point(),scroll).Y-top)<.5,"smooth scrolling starts at the currently displayed position without a jump");
                Check(rows.Children.OfType<Button>().All(item=>!ToolTipService.GetIsEnabled(item)),"scrolling closes stale server tooltips while cards move beneath the pointer");
                await Task.Delay(65);
                Check(((System.Windows.Media.TranslateTransform)rows.RenderTransform).Y is >0 and var middle&&middle<initialShift,"server rows move through intermediate scroll positions");
                CaptureInteraction(host,Path.Combine(scratch,"scroll-middle.png"));
                var beforeReverse=anchor.TranslatePoint(new Point(),scroll).Y;
                motion.ScrollByRows(-1);
                Check(Math.Abs(anchor.TranslatePoint(new Point(),scroll).Y-beforeReverse)<.5,"reversing the wheel preserves the current visual position");
                await Task.Delay(270); host.UpdateLayout();
                Check(!motion.IsAnimating&&((System.Windows.Media.TranslateTransform)rows.RenderTransform).Y==0,"scroll animation settles on complete rows and releases its clock");
                bool VisibleRowsPainted() => rows.Children.OfType<Button>().All(item=>item.Opacity==1&&item.IsHitTestVisible);
                Check(VisibleRowsPainted()&&rows.Children.OfType<Button>().All(ToolTipService.GetIsEnabled),"settled rows remain painted and restore their tooltips after reversing the scroll");
                foreach(var scale in new[]{1d,1.25})
                {
                    list.LayoutTransform=new System.Windows.Media.ScaleTransform(scale,scale); host.UpdateLayout();
                    scroll.ScrollToTop(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    foreach(var delta in new[]{-120,-960,120,960,-120})
                    {
                        var previous=scroll.VerticalOffset;
                        scroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,delta) {
                            RoutedEvent=System.Windows.Input.Mouse.PreviewMouseWheelEvent
                        });
                        Check(Math.Abs(scroll.VerticalOffset-previous)<=2.01,$"wheel step stays within two server cards at {scale*100:0}% scale and delta {delta}");
                        await Task.Delay(270); host.UpdateLayout();
                        Check(VisibleRowsPainted(),$"no blank visible rows remain after scroll at {scale*100:0}% scale and delta {delta}");
                        CaptureInteraction(host,Path.Combine(scratch,$"scroll-settled-{scale*100:0}-{delta}.png"));
                    }
                }
                list.LayoutTransform=System.Windows.Media.Transform.Identity; host.UpdateLayout();
                motion.AnimationsEnabled=()=>false; motion.ScrollByRows(-1);
                Check(!motion.IsAnimating,"reduced motion disables the decorative scroll animation");
                requests=0; ping.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await list.StopChecksAsync();
                Check(requests<=4&&canceled>0,"leaving the page cancels queued ping work and waits for active requests");
            }
            finally { host.Close(); }
        }
        var path=Path.Combine(scratch,"page-profile.yaml");
        string Fixture(string name,int index)
        {
            if(index>=2) return $" - {{ name: '{name}', type: socks5, server: 192.0.2.{index+1}, port: 1080 }}";
            const string json="{\"outbounds\":[{\"tag\":\"one\",\"protocol\":\"vless\",\"settings\":{\"vnext\":[{\"address\":\"192.0.2.10\",\"port\":443,\"users\":[{\"id\":\"00000000-0000-0000-0000-000000000001\",\"encryption\":\"none\"}]}]}}],\"routing\":{\"balancers\":[{\"tag\":\"auto\",\"selector\":[\"one\"],\"strategy\":{\"type\":\"leastPing\"}}],\"rules\":[{\"type\":\"field\",\"network\":\"tcp,udp\",\"balancerTag\":\"auto\"}]}}";
            return $" - {{ name: '{name}', type: xray-profile, server: 192.0.2.10, port: 443, xray-json: '{json}' }}";
        }
        var yaml="proxies:\n"+string.Join("\n",names.Select(Fixture))+"\nproxy-groups:\n - { name: VPN, type: select, proxies: ["+string.Join(", ",names.Select(name=>"'"+name+"'"))+"] }\nrules:\n - DOMAIN-SUFFIX,keep.example,DIRECT\n - DOMAIN-SUFFIX,vpn.example,VPN\n - MATCH,DIRECT\n";
        File.WriteAllText(path,yaml);
        var settingsPath=Path.Combine(scratch,"page-settings.json");
        var settings=AppSettings.Load(settingsPath); settings.ConfigPath=path; settings.CorePath=fakeExe; settings.AutoCheckUpdates=false;
        var source=SubscriptionSource.Create("https://subscription.example/fixture");
        source.Metadata=new SubscriptionMetadata { Title="wpnUP Subscription",Download=8L*1024*1024*1024,Total=10240L*1024*1024*1024,Expires=new DateTimeOffset(2026,12,6,0,0,0,TimeSpan.Zero).ToUnixTimeSeconds(),CheckedAt=DateTimeOffset.UtcNow,Announcement="Регулярно обновляйте подписку, чтобы получать актуальные конфигурации. Устройств: 6/6." };
        settings.ProfileSubscriptions[path]=source;
        var window=new MainWindow(settings,Path.Combine(scratch,"page.log")) { ShowInTaskbar=false };
        T Field<T>(string name)=>(T)typeof(MainWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
        async Task Idle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); var gate=Field<SemaphoreSlim>("_operations"); await gate.WaitAsync(); gate.Release(); window.UpdateLayout(); }
        async Task Tab(string name) { Field<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(); await Task.Delay(220); }
        try
        {
            window.Show(); await Idle();
            await Tab("SubscriptionTabButton");
            Check(Field<Grid>("SubscriptionPage").IsVisible&&!Field<Grid>("RoutesPage").IsVisible&&!Field<Border>("JournalPage").IsVisible,"subscription is a third independent workspace page");
            var view=Field<ContentControl>("SubscriptionServersHost").Content;
            Check(view is VpnServerListView&&Field<Border>("SubscriptionBanner").IsVisible,"subscription information and server choice share the subscription page");
            string[] ServerTitles() => Descendants<TextBlock>((DependencyObject)view!).Where(item => item.Name == "VpnServerTitle").Select(item => item.Text).ToArray();
            var titles = ServerTitles();
            Check(titles.SequenceEqual(new[] { primary[0], primary[1], primary[2], "№3 Германия Bridge", "№4 Финляндия Bridge", "№5 Германия Bridge", "№6 Финляндия Bridge" }),
                "subscription page numbers titled servers continuously across both formats and leaves unnumbered titles intact");
            var serverSearch = Descendants<TextBox>((DependencyObject)view!).Single();
            serverSearch.Text = "№6";
            window.UpdateLayout();
            Check(ServerTitles().SequenceEqual(new[] { titles[^1] }), "filtering by the displayed number keeps the same server number");
            serverSearch.Clear();
            window.UpdateLayout();
            var serverViewport=Descendants<Border>((DependencyObject)view!).Single(item=>item.Name=="VpnServerViewport");
            var compactHeight=serverViewport.ActualHeight;
            Check(Field<Border>("SubscriptionDetailsClip").Visibility==Visibility.Collapsed&&Field<Border>("SubscriptionBanner").ActualHeight<85,"subscription starts as a compact header with its details collapsed");
            Field<Button>("SubscriptionDetailsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(240); window.UpdateLayout();
            Check(Field<Border>("SubscriptionDetailsClip").IsVisible&&serverViewport.ActualHeight<compactHeight-35,"subscription details expand locally instead of opening a modal");
            CaptureInteraction(window,Path.Combine(scratch,"subscription-expanded.png"));
            Field<Button>("SubscriptionDetailsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(240); window.UpdateLayout();
            Check(Math.Abs(serverViewport.ActualHeight-compactHeight)<1,"collapsing subscription details returns the space to the server list");
            Check(Field<Button>("ReloadSubscriptionButton").IsEnabled&&!Descendants<Button>(window).Any(item=>item.Name=="RefreshSubscriptionInfoButton"),"the compact header exposes only the full subscription refresh button");
            Check(!Descendants<TextBlock>((DependencyObject)view!).Any(item=>item.IsVisible&&(item.Text.StartsWith("Серверов:")||item.Text=="Задержка")),"no server-count or latency-heading row takes space above the list");
            var searchControl=Descendants<TextBox>((DependencyObject)view!).Single(); var pingControl=Descendants<Button>((DependencyObject)view!).Single(item=>item.Name=="VpnServerPing");
            Check(Math.Abs(searchControl.TranslatePoint(new Point(),serverViewport.Parent as UIElement).Y-pingControl.TranslatePoint(new Point(),serverViewport.Parent as UIElement).Y)<3,"search and PING share a single toolbar row");
            var workspace=Field<Border>("WorkspacePanel");
            var inner=Field<Grid>("MainPageHost");
            var contentBounds=inner.TransformToAncestor(workspace).TransformBounds(new Rect(inner.RenderSize));
            Check(contentBounds.Left>=16&&contentBounds.Top>=16&&contentBounds.Right<=workspace.ActualWidth-16,"all pages have a shared framed workspace with real inset margins");
            var quota=Field<ProgressBar>("SubscriptionUsageBar");
            foreach(var percent in new[]{0d,.2,50d,100d})
            {
                quota.Value=percent; window.UpdateLayout();
                var track=(Grid)quota.Template.FindName("PART_Track",quota);
                var indicator=(Border)quota.Template.FindName("PART_Indicator",quota);
                Check(Math.Abs(indicator.ActualWidth-track.ActualWidth*percent/100)<1.1&&track.Clip is System.Windows.Media.RectangleGeometry,$"quota fill is proportional and clipped inside the rounded track at {percent}%");
                CaptureInteraction(window,Path.Combine(scratch,$"quota-{percent:0.0}.png"));
            }
            quota.Value=source.Metadata!.UsagePercent;
            var last=Descendants<Button>((DependencyObject)view!).Last(item=>item.Tag?.ToString()==names[^1]);
            last.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Check(AppSettings.Load(settingsPath).ProfileSelections[path]==names[^1]&&!Field<CoreProcessManager>("_core").HasTrackedProcess,"page selection persists without starting or restarting a stopped VPN");
            foreach(var theme in ThemeManager.ThemeNames)
            {
                ThemeManager.Apply(theme); window.Width=1120; window.Height=760; window.ApplyInterfaceScale(1); await Idle();
                await Tab("SubscriptionTabButton");
                var bounds=Field<Grid>("MainPageHost").RenderSize;
                CaptureInteraction(window,Path.Combine(scratch,$"subscription-theme-{Array.IndexOf(ThemeManager.ThemeNames,theme)}.png"));
                await Tab("RoutesTabButton");
                Check(Field<Grid>("MainPageHost").RenderSize==bounds&&!Field<Border>("SubscriptionBanner").IsVisible,$"switching pages preserves workspace geometry and isolates subscription information in {theme}");
                CaptureInteraction(window,Path.Combine(scratch,$"routes-theme-{Array.IndexOf(ThemeManager.ThemeNames,theme)}.png"));
            }
            foreach(var scale in new[]{.8,1d,1.5})
            {
                window.ApplyInterfaceScale(scale); window.Width=window.MinWidth; window.Height=window.MinHeight; await Idle();
                foreach(var tab in new[]{"SubscriptionTabButton","RoutesTabButton","JournalTabButton"})
                {
                    await Tab(tab);
                    Check(Field<Grid>("MainPageHost").ActualWidth>300&&Field<Grid>("MainPageHost").ActualHeight>300,$"{tab} fits the minimum window at {scale*100:0}% scale");
                    CaptureInteraction(window,Path.Combine(scratch,$"{tab}-{scale*100:0}.png"));
                }
            }
            Check(File.ReadAllText(path)==yaml&&!Field<CoreProcessManager>("_core").HasTrackedProcess,"sorting, page navigation and theme changes leave VPN settings and user rules untouched");
        }
        finally { typeof(MainWindow).GetField("_allowClose",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true); window.Close(); }
        count+=await RunSubscriptionReliabilityChecks(scratch,fakeExe);
        Console.WriteLine("Screenshots: "+scratch);
        return count;
    }
}
