using System.Net.Http;
using System.Windows;
using System.Globalization;
using System.Text;

namespace MorphocyteRouter;

public partial class MainWindow
{
    internal void UseWindowsSubscriptionIdentity(string expectedIdentity)
    {
        if(_busy||_closing) throw new InvalidOperationException("Другая операция ещё выполняется.");
        if(SubscriptionDeviceIdentity.UseWindowsIdentity(_settings.StorageDirectory,expectedIdentity))
        {
            _subscriptionInfoGeneration++;
            SetLog("Для запросов подписки выбран ID этого компьютера. Прежний ID сохранён в зашифрованной резервной копии.");
        }
    }

    private void QuotaTrack_SizeChanged(object sender,SizeChangedEventArgs e)
    {
        if(sender is FrameworkElement track)
            track.Clip=new System.Windows.Media.RectangleGeometry(new Rect(e.NewSize),5,5);
    }
    private bool _readingSubscriptionInfo;
    private int _subscriptionInfoGeneration;
    private void RefreshSubscriptionRefreshButton()
    {
        SetWorkspaceControlAvailability(ReloadSubscriptionButton, _settings.ProfileSubscriptions.ContainsKey(_settings.ConfigPath),
            temporarilyLocked: _updatingSubscription);
        ReloadSubscriptionButton.Content = _updatingSubscription ? "ОБНОВЛЯЮ…" : "ОБНОВИТЬ";
    }

    private void RefreshSubscriptionBanner()
    {
        var source=_settings.ProfileSubscriptions.GetValueOrDefault(_settings.ConfigPath);
        SubscriptionBanner.Visibility=source is null?Visibility.Collapsed:Visibility.Visible;
        SubscriptionEmptyPanel.Visibility=source is null?Visibility.Visible:Visibility.Collapsed;
        RefreshSubscriptionRefreshButton();
        RefreshSubscriptionServers();
        var data=source?.Metadata;
        var title=data?.Title is { Length:>0 } label?label:"ПОДПИСКА";
        var first=StringInfo.GetNextTextElement(title);
        var hasEmoji=Rune.GetUnicodeCategory(Rune.GetRuneAt(first,0))==UnicodeCategory.OtherSymbol;
        SubscriptionTitleEmoji.Text=hasEmoji?first:"🚀";
        SubscriptionTitleText.Text=hasEmoji?title[first.Length..].TrimStart():title;
        SubscriptionTitleText.ToolTip=title;
        SubscriptionUpdatedText.Text=data is null?"Сведения ещё не загружены":"🗓  Обновлено "+data.CheckedAt.ToLocalTime().ToString("dd.MM · HH:mm");
        SubscriptionTrafficText.Text=data?.TrafficLabel??"";
        SubscriptionExpiryText.Text=data is null?"":"📅  "+data.ExpiryLabel;
        SubscriptionUsageBar.Value=data?.UsagePercent??0;
        SubscriptionUsageBar.Visibility=data?.Total is >0?Visibility.Visible:Visibility.Collapsed;
        SubscriptionMessageText.Text=data is null?"ℹ️  Информация ещё не загружена. Нажми «Обновить».":DecorateSubscriptionMessage(data.Announcement);
        SubscriptionMessageText.Visibility=SubscriptionMessageText.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
    }

    private static string DecorateSubscriptionMessage(string message) => string.Join('\n',message.Split('\n').Select(line=> {
        var text=line.Trim();
        if(text.Length==0) return "";
        var first=Rune.GetRuneAt(text,0);
        if(Rune.GetUnicodeCategory(first)==UnicodeCategory.OtherSymbol) return text;
        return (text.StartsWith("ID",StringComparison.OrdinalIgnoreCase)?"🆔  ":"ℹ️  ")+text;
    }));

    internal async Task RefreshSubscriptionInformationAsync(HttpClient? client=null)
    {
        if(_readingSubscriptionInfo || _updatingSubscription || _closing || !_settings.ProfileSubscriptions.TryGetValue(_settings.ConfigPath,out var source)) return;
        var path=_settings.ConfigPath;
        var generation=_subscriptionInfoGeneration;
        _readingSubscriptionInfo=true; RefreshSubscriptionBanner();
        try
        {
            var id=SubscriptionDeviceIdentity.GetOrCreate(_settings.StorageDirectory);
            var downloaded=await SubscriptionImporter.ImportAsync(source.ReadUrl(),_lifetime.Token,client,hardwareId:id,headersOnly:true);
            if(_closing || generation!=_subscriptionInfoGeneration || _settings.ConfigPath!=path || !ReferenceEquals(_settings.ProfileSubscriptions.GetValueOrDefault(path),source)) return;
            var previous=source.Metadata;
            source.Metadata=downloaded.Metadata;
            try { await SaveSettingsAsync(); }
            catch { source.Metadata=previous; throw; }
            RefreshSubscriptionBanner();
        }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) { }
        catch
        {
            if(!_closing && generation==_subscriptionInfoGeneration && _settings.ConfigPath==path
                && ReferenceEquals(_settings.ProfileSubscriptions.GetValueOrDefault(path),source))
            {
                SubscriptionMessageText.Text="Не удалось обновить сведения подписки. Сохранённые данные и подключение не изменены.";
                SubscriptionMessageText.Visibility=Visibility.Visible;
                SetSubscriptionDetailsExpanded(true,animate:false);
            }
        }
        finally { _readingSubscriptionInfo=false; }
    }
}
