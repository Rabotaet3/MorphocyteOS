using System.Windows;
using System.Windows.Controls;

namespace MorphocyteRouter;

internal static partial class UtilityDialogs
{
    internal static Border DeviceIdentityCard(MainWindow owner,Window promptOwner)
    {
        var directory=owner.SubscriptionIdentityDirectory;
        var body=new StackPanel();
        var status=Paragraph(""); status.Name="SubscriptionDeviceIdentityStatus";
        var migrate=DialogChrome.MakeButton("Использовать ID компьютера",false);
        migrate.Name="UseComputerIdentityButton"; migrate.HorizontalAlignment=HorizontalAlignment.Left;
        string current="";
        void Refresh()
        {
            try
            {
                current=SubscriptionDeviceIdentity.GetOrCreate(directory);
                var computer=SubscriptionDeviceIdentity.ReadWindowsIdentity();
                var shared=current==computer;
                status.Text=shared?"Используется ID этого компьютера. Совместимые VPN-клиенты могут использовать тот же слот подписки."
                    :"Используется ранее сохранённый ID MorphocyteOS. Можно перейти на ID этого компьютера, который использует стандартная настройка Happ для Windows.";
                migrate.Visibility=shared?Visibility.Collapsed:Visibility.Visible;
                migrate.IsEnabled=computer is not null;
                if(computer is null) status.Text+=" ID Windows сейчас недоступен; текущий ID сохранён.";
            }
            catch { status.Text="Не удалось прочитать ID устройства. Он не был заменён. Проверь доступ к локальным данным."; migrate.IsEnabled=false; }
        }
        migrate.Click+=(_,_)=> {
            if(!ShowConfirm(promptOwner,"ID УСТРОЙСТВА",
                "Новый ID будет передаваться всем добавленным провайдерам подписки. Если Happ использует стандартный ID этого компьютера, оба клиента смогут использовать один слот. Старую запись MorphocyteOS в кабинете провайдера нужно удалить отдельно после проверки. Изменение применяется сразу.","ПЕРЕЙТИ","ОТМЕНА")) return;
            try { owner.UseWindowsSubscriptionIdentity(current); Refresh(); }
            catch { status.Text="Не удалось изменить ID. Текущий ID сохранён; повтори после завершения других операций."; }
        };
        body.Children.Add(status); body.Children.Add(migrate); Refresh();
        return Card("ID УСТРОЙСТВА",null,body);
    }
}
