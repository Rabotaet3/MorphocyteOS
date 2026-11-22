using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Animation;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private VpnServerListView? _subscriptionServers;
    private ClashConfigDocument? _subscriptionServersDocument;
    private ServerPingSession? _subscriptionPing;
    private CancellationToken _subscriptionServersToken;
    private bool _subscriptionServersInProviderOrder;
    private bool _updatingSubscription;
    private bool _subscriptionDetailsExpanded;
    private int _subscriptionDetailsVersion;

    private void SubscriptionDetails_Click(object sender,RoutedEventArgs e) => SetSubscriptionDetailsExpanded(!_subscriptionDetailsExpanded);

    private void SetSubscriptionDetailsExpanded(bool expanded,bool animate=true)
    {
        _subscriptionDetailsExpanded=expanded;
        var version=++_subscriptionDetailsVersion;
        var clip=SubscriptionDetailsClip;
        var current=clip.IsVisible?clip.ActualHeight:0;
        clip.BeginAnimation(HeightProperty,null);
        clip.Height=current;
        clip.Visibility=Visibility.Visible;
        clip.IsEnabled=expanded;
        SubscriptionDetailsButton.Content=expanded?"Свернуть ⌃":"Подробнее ⌄";
        System.Windows.Automation.AutomationProperties.SetItemStatus(SubscriptionDetailsButton,expanded?"Развёрнуто":"Свёрнуто");
        SubscriptionDetailsContent.Measure(new Size(Math.Max(100,SubscriptionBanner.ActualWidth-26),double.PositiveInfinity));
        var target=expanded?SubscriptionDetailsContent.DesiredSize.Height:0;
        void Finish()
        {
            if(version!=_subscriptionDetailsVersion) return;
            clip.BeginAnimation(HeightProperty,null);
            clip.Height=expanded?double.NaN:0;
            clip.Visibility=expanded?Visibility.Visible:Visibility.Collapsed;
        }
        if(!animate||!IsLoaded||!SystemParameters.ClientAreaAnimation) { Finish(); return; }
        var motion=new DoubleAnimation(current,target,TimeSpan.FromMilliseconds(180)) {
            EasingFunction=new CubicEase { EasingMode=EasingMode.EaseInOut },FillBehavior=FillBehavior.HoldEnd
        };
        motion.Completed+=(_,_)=>Finish();
        clip.BeginAnimation(HeightProperty,motion,HandoffBehavior.SnapshotAndReplace);
    }

    private void EditSubscription_Click(object sender,RoutedEventArgs e) => ShowSubscriptionSource(this);
    private async void ReloadSubscription_Click(object sender, RoutedEventArgs e) => await RefreshSubscriptionAsync(_lifetime.Token);

    private void RefreshSubscriptionServers()
    {
        if (SubscriptionServersHost is null || _updatingSubscription) return;
        var selected=_selectedConnection.Length>0?_selectedConnection:RememberedVpnServer();
        var providerOrder = _document is { } current && _settings.ProfileSubscriptions.ContainsKey(current.Path);
        if(ReferenceEquals(_subscriptionServersDocument,_document) && _subscriptionServers is not null
            && _subscriptionServersToken==_lifetime.Token && _subscriptionServersInProviderOrder==providerOrder)
        { _subscriptionServers.SetCurrent(selected); return; }
        var sameProfile = _document is not null && string.Equals(_subscriptionServersDocument?.Path, _document.Path, StringComparison.OrdinalIgnoreCase)
            && _subscriptionServersToken == _lifetime.Token;
        _subscriptionServersDocument=_document;
        _subscriptionServersToken=_lifetime.Token;
        _subscriptionServersInProviderOrder = providerOrder;
        if (_document is not { } document)
        {
            _subscriptionServers?.Dispose();
            _subscriptionServers = null;
            SubscriptionServersHost.Content = null;
            return;
        }
        var servers=VpnServerCatalog.Choices(document,subscriptionOrder:providerOrder);
        var presentations=document.ProxyNodes.Concat(document.ProxyGroups)
            .GroupBy(node=>node.Children.TryGetValue(new YamlScalarNode("name"),out var name)?name.ToString():"",StringComparer.Ordinal)
            .ToDictionary(group=>group.Key,group=>VpnServerPresentation.Describe(group.Key,group.First()),StringComparer.Ordinal);
        if (providerOrder)
        {
            var number = 0;
            foreach (var name in servers)
            {
                var presentation = presentations[name];
                var title = Regex.Replace(presentation.Title, @"^№\s*[0-9]+", _ => $"№{++number}");
                presentations[name] = presentation with { Title = title };
            }
        }
        async Task<int> CheckServerAsync(string name, CancellationToken token)
        {
            if(!ReferenceEquals(document,_document) || _closing) throw new OperationCanceledException(token);
            if(_core.IsRunning && _diagnostics is { } diagnostics) return await diagnostics.CheckProxyAsync(name,token);
            _subscriptionPing??=new ServerPingSession(document);
            return await _subscriptionPing.CheckAsync(name,token);
        }
        if (sameProfile && _subscriptionServers is { } view)
        {
            view.ReplaceServers(servers, selected, presentations, CheckServerAsync);
            return;
        }
        _subscriptionServers?.Dispose();
        _subscriptionServers=new VpnServerListView(servers,selected,CheckServerAsync,SelectVpnServerAsync,presentations,_lifetime.Token);
        _subscriptionServers.ChecksFinished+=async()=>await DisposeSubscriptionPingAsync();
        SubscriptionServersHost.Content=_subscriptionServers;
    }

    private async Task DisposeSubscriptionPingAsync()
    {
        var session=_subscriptionPing;
        if(session is null) return;
        await session.DisposeAsync();
        if(ReferenceEquals(_subscriptionPing,session)) _subscriptionPing=null;
    }

    private async Task StopSubscriptionProbesAsync()
    {
        if(_subscriptionServers is { } view) await view.StopChecksAsync();
        await DisposeSubscriptionPingAsync();
    }
}
