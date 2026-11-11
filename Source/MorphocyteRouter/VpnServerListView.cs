using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MorphocyteRouter;

// Server selection and latency probes on the subscription page.
internal sealed class VpnServerListView : Grid, IDisposable
{
    private readonly CancellationTokenSource _lifetime;
    private Task _checking=Task.CompletedTask;
    private Action _cancel=()=>{};
    private Action _close=()=>{};
    private Action<string> _setCurrent=_=>{};
    private bool _disposed;
    private ServerListMotion? _scrollMotion;
    internal event Func<Task>? ChecksFinished;
    internal void SetCurrent(string name)=>_setCurrent(name);
    internal async Task StopChecksAsync() { _cancel(); await _checking; }
    public void Dispose() { if(_disposed) return; _disposed=true; _scrollMotion?.Dispose(); _close(); _lifetime.Dispose(); }
    private static ControlTemplate RowTemplate() => (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
            <Border x:Name="RowBorder" Margin="3" CornerRadius="9" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}">
                <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Stretch" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="RowBorder" Property="BorderBrush" Value="{DynamicResource ThemeAccent}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="RowBorder" Property="BorderBrush" Value="{DynamicResource ThemeAccent}"/></Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>
        """);


    internal VpnServerListView(IReadOnlyList<string> servers,string current,
        Func<string,CancellationToken,Task<int>>? probe,Func<string,Task<bool>> select,
        IReadOnlyDictionary<string,VpnServerPresentation>? presentations=null,CancellationToken token=default)
    {
        _lifetime=CancellationTokenSource.CreateLinkedTokenSource(token);
        var lifetime=_lifetime;
        CancellationTokenSource? batch=null;
        var closed=false; var selecting=false;
        var ordered=servers.ToArray();
        var delays = new Dictionary<string,int?>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var latencyLabels = new Dictionary<string,TextBlock>(StringComparer.Ordinal);
        var search=new TextBox { Name="VpnServerSearch",Padding=new Thickness(11,9,11,9),Margin=new Thickness(0,0,0,8),ToolTip="Найти сервер по названию или стране" };
        var searchHost=new Grid { Margin=search.Margin }; search.Margin=new Thickness(0);
        var hint=new TextBlock { Text="Поиск по названию или стране",Margin=new Thickness(12,0,12,0),VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false };
        hint.SetResourceReference(TextBlock.ForegroundProperty,"ThemeMuted"); searchHost.Children.Add(search); searchHost.Children.Add(hint);
        var count=new TextBlock { Name="VpnServerCount",FontSize=12,Visibility=Visibility.Collapsed,Margin=new Thickness(0,0,0,8),TextWrapping=TextWrapping.Wrap };
        count.SetResourceReference(TextBlock.ForegroundProperty,"ThemeMuted");
        var ping = DialogChrome.MakeButton("PING",false); ping.Name="VpnServerPing"; ping.Height=36; ping.MinWidth=112;
        ping.Margin=new Thickness(10,0,0,0); ping.IsEnabled=probe is not null&&servers.Count>0;
        ping.ToolTip=probe is null ? "Запусти VPN для проверки серверов." : "Проверить задержку HTTPS через каждый сервер без переключения текущего подключения.";
        var toolbar = new Grid { Margin=new Thickness(0,0,0,10) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        searchHost.Margin=new Thickness(0);
        toolbar.Children.Add(searchHost); Grid.SetColumn(ping,1); toolbar.Children.Add(ping);
        var probeIssue="";
        var rows=new StackPanel { Name="VpnServerRows" };
        var rowTemplate=RowTemplate();
        var scroll=new ScrollViewer { Name="VpnServerScroll",Content=rows,CanContentScroll=true,Focusable=false,IsTabStop=false,FocusVisualStyle=null,
            Padding=new Thickness(4),ClipToBounds=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        // The viewport clips the cards naturally. Hiding incomplete rows here
        // made the bottom card disappear when the animation finished.
        _scrollMotion=new ServerListMotion(scroll,rows,()=> {
            foreach(var item in rows.Children.OfType<FrameworkElement>())
                ToolTipService.SetIsEnabled(item,_scrollMotion?.IsAnimating!=true);
        });
        var viewport=new Border { Name="VpnServerViewport",CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1),Padding=new Thickness(3),Child=scroll };
        viewport.SetResourceReference(Border.BorderBrushProperty,"ThemeSurfaceBorder");
        viewport.SetResourceReference(Border.BackgroundProperty,"ThemeSurface");
        var body=this;
        body.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.Children.Add(toolbar); Grid.SetRow(count,1); body.Children.Add(count); Grid.SetRow(viewport,2); body.Children.Add(viewport);
        var matchCount=servers.Count;
        void UpdateCount()
        {
            System.Windows.Automation.AutomationProperties.SetHelpText(scroll,matchCount==servers.Count?$"Серверов: {servers.Count}":$"Найдено: {matchCount} из {servers.Count}");
            count.Text=probeIssue;
            count.Visibility=probeIssue.Length>0?Visibility.Visible:Visibility.Collapsed;
        }
        void UpdateSelection()
        {
            foreach(var button in rows.Children.OfType<Button>())
            {
                var selected=button.Tag?.ToString()==current;
                button.SetResourceReference(Control.BorderBrushProperty,selected?"ThemeAccent":"ThemeButtonBorder");
                button.SetResourceReference(Control.BackgroundProperty,selected?"ThemeSurfaceSelected":"ThemeButton");
                System.Windows.Automation.AutomationProperties.SetItemStatus(button,selected?"Выбран":"");
            }
        }
        void UpdateLatency(string name)
        {
            if (!latencyLabels.TryGetValue(name,out var label)) return;
            label.Text=pending.Contains(name)?"…":delays.TryGetValue(name,out var delay)?delay is { } ms?$"{ms} мс":"Недоступен":"—";
            label.ToolTip=pending.Contains(name)?"Проверяется…":delays.TryGetValue(name,out var value)?value.HasValue
                ?"Задержка HTTPS через этот сервер. Это не измерение скорости загрузки."
                :"Сервер не ответил на проверочные HTTPS-запросы.":"Нажми PING для проверки.";
            label.SetResourceReference(TextBlock.ForegroundProperty,delays.TryGetValue(name,out var measured)&&measured.HasValue?"ThemeAccent":"ThemeMuted");
        }
        void Render(bool resetScroll = true)
        {
            _scrollMotion?.Cancel();
            var offset=scroll.VerticalOffset;
            rows.Children.Clear();
            latencyLabels.Clear();
            hint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
            var query=search.Text.Trim();
            var matches = ordered.Where(name =>
            {
                var presentation = presentations?.GetValueOrDefault(name);
                return (presentation?.Title ?? name).Contains(query, StringComparison.OrdinalIgnoreCase)
                    || presentation?.CountryCode?.Equals(query, StringComparison.OrdinalIgnoreCase) == true;
            }).ToArray();
            matchCount=matches.Length; UpdateCount();
            if(matches.Length==0)
            {
                var empty=new TextBlock { Name="VpnServerEmpty",Text=servers.Count==0?"В профиле нет доступных серверов. Добавь или обнови подписку.":"Ничего не найдено. Попробуй другое название или страну.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(14),FontSize=12 };
                empty.SetResourceReference(TextBlock.ForegroundProperty,"ThemeMuted"); rows.Children.Add(empty);
            }
            foreach(var name in matches)
            {
                var selected=name==current;
                var button=DialogChrome.MakeButton("",false);
                button.Template=rowTemplate; button.Height=double.NaN; button.MinHeight=46;
                button.Tag=name; button.Margin=new Thickness(0,0,5,4); button.Padding=new Thickness(11,6,11,6); button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
                button.ToolTip=name;
                var row=new Grid();
                if(presentations is not null) row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(36) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
                var info=presentations?.GetValueOrDefault(name)??VpnServerPresentation.Describe(name,null);
                var title=presentations is null?name:info.Title;
                var label=new TextBlock { Name="VpnServerTitle",Text=title,TextWrapping=TextWrapping.Wrap,FontSize=14,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center };
                var textColumn=new StackPanel { VerticalAlignment=VerticalAlignment.Center }; textColumn.Children.Add(label);
                if(info.Details.Length>0)
                {
                    var details=new TextBlock { Name="VpnServerDetails",Text=info.Details,FontSize=10,Margin=new Thickness(0,4,0,0),TextWrapping=TextWrapping.Wrap };
                    details.SetResourceReference(TextBlock.ForegroundProperty,"ThemeMuted"); textColumn.Children.Add(details);
                }
                var textIndex=presentations is null?0:1;
                Grid.SetColumn(textColumn,textIndex); row.Children.Add(textColumn);
                if(presentations is not null)
                {
                    var flag=CountryBadge.Create(info.CountryCode); flag.Name="VpnServerCountry"; row.Children.Add(flag);
                }
                var latency=new TextBlock { Name="VpnServerLatency",MinWidth=76,FontSize=12,Margin=new Thickness(10,0,0,0),TextAlignment=TextAlignment.Right,
                    VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right };
                Grid.SetColumn(latency,textIndex+1); row.Children.Add(latency); latencyLabels[name]=latency; UpdateLatency(name);
                if(selected) { button.SetResourceReference(Control.BorderBrushProperty,"ThemeAccent"); button.SetResourceReference(Control.BackgroundProperty,"ThemeSurfaceSelected"); }
                System.Windows.Automation.AutomationProperties.SetItemStatus(button,selected?"Выбран":"");
                button.Content=row;
                button.Click+=async (_,_)=> {
                    if(selecting || closed) return;
                    selecting=true;
                    try { if(await select(name) && !closed) { current=name; UpdateSelection(); } }
                    finally { selecting=false; }
                };
                rows.Children.Add(button);
            }
            if(resetScroll) scroll.ScrollToTop(); else scroll.ScrollToVerticalOffset(offset);
        }
        search.TextChanged+=(_,_)=>Render();
        async Task CheckAllAsync() {
            if(batch is not null) { batch.Cancel(); return; }
            if(probe is null) return;
            using var run=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            batch=run;
            probeIssue="";
            delays.Clear(); pending.UnionWith(servers);
            foreach(var name in servers) UpdateLatency(name);
            var completed=0; ping.Content=$"СТОП · 0/{servers.Count}";
            ping.ToolTip="Остановить проверку. Полученные результаты сохранятся в этом окне.";
            // Keep rows still during the batch. Only update the right-hand labels;
            // retain provider ordering and scroll position when the batch finishes.
            using var concurrency=new SemaphoreSlim(4);
            try
            {
                await Task.WhenAll(servers.Select(async name=> {
                    await concurrency.WaitAsync(run.Token);
                    try
                    {
                        run.Token.ThrowIfCancellationRequested();
                        try
                        {
                            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(run.Token);
                            deadline.CancelAfter(TimeSpan.FromSeconds(11));
                            var ms=await probe(name,deadline.Token);
                            if(ms is <0 or >120000) throw new System.IO.InvalidDataException();
                            run.Token.ThrowIfCancellationRequested();
                            delays[name]=ms;
                        }
                        catch(ServerPingSetupException) { probeIssue="Не удалось подготовить проверку. Закрой список и повтори попытку."; run.Cancel(); throw new OperationCanceledException(run.Token); }
                        catch(OperationCanceledException) when(run.IsCancellationRequested) { throw; }
                        catch { delays[name]=null; }
                        if(closed) return;
                        pending.Remove(name); UpdateLatency(name);
                        ping.Content=$"СТОП · {++completed}/{servers.Count}";
                    }
                    finally { concurrency.Release(); }
                }));
            }
            catch(OperationCanceledException) when(run.IsCancellationRequested) { }
            finally
            {
                try
                {
                    if(ChecksFinished is { } finished)
                        foreach(Func<Task> cleanup in finished.GetInvocationList()) await cleanup();
                }
                catch { probeIssue="Не удалось завершить проверку серверов. Повтори остановку перед запуском VPN."; }
                finally
                {
                    batch=null;
                    if(!closed)
                    {
                        pending.Clear(); ping.Content="PING";
                        ping.ToolTip="Повторить проверку задержки HTTPS через каждый сервер.";
                        foreach(var name in latencyLabels.Keys) UpdateLatency(name);
                        UpdateCount();
                    }
                }
            }
        }
        _setCurrent=value=> { if(current==value || closed) return; current=value; UpdateSelection(); };
        ping.Click+=async (_,_)=> {
            if(batch is not null) { batch.Cancel(); return; }
            _checking=CheckAllAsync();
            await _checking;
        };
        search.KeyDown+=(_,e)=> { if(e.Key==Key.Enter && rows.Children.OfType<Button>().ToArray() is [var button]) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        _cancel=()=>batch?.Cancel();
        _close=()=> { closed=true; lifetime.Cancel(); batch?.Cancel(); };
        Render();

    }
}
