using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MorphocyteRouter;

internal static class ProfileImportDialog
{
    internal static ImportedProfile? Show(Window owner)
    {
        var dialog = DialogChrome.CreateWindow(owner, "ИМПОРТ ПРОФИЛЯ", 690, 600, 570, 440, ResizeMode.CanResize);
        ImportedProfile? result = null;
        using var lifetime = new CancellationTokenSource();
        dialog.Closed += (_, _) => lifetime.Cancel();
        var body = new Grid { Margin = new Thickness(22, 16, 22, 18) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var explanation = new TextBlock
        {
            Text = "Вставь HTTPS-ссылку на подписку, ссылку vless:// или JSON Xray. Программа создаст отдельный YAML с TUN и списком серверов.",
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
        body.Children.Add(explanation);
        var note = new TextBlock
        {
            Text = "Из подписки импортируются серверы, а не чужие правила или DNS. Поддерживаются YAML Clash/Mihomo, списки VLESS и Base64. По умолчанию трафик идёт напрямую; нужные приложения добавь в правила VPN.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        Grid.SetRow(note, 1); body.Children.Add(note);
        var input = new TextBox
        {
            Name = "ImportProfileInput", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontSize = 13, FontFamily = new System.Windows.Media.FontFamily("Consolas"), MaxLength = ProfileImporter.MaxInputLength,
            VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(12),
            ToolTip = "HTTPS-ссылка отправляется серверу подписки и его перенаправлениям. При VPN Fake-IP имя сайта проверяется через DNS Cloudflare/Google без пути и ключа ссылки. Не делись ссылкой: в ней может быть ключ доступа."
        };
        Grid.SetRow(input, 2); body.Children.Add(input);
        var status = new TextBlock { Text = "", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        Grid.SetRow(status, 3); body.Children.Add(status);
        var load = DialogChrome.MakeButton("Открыть JSON", false);
        load.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "JSON Xray (*.json)|*.json", CheckFileExists = true };
            if (picker.ShowDialog(dialog) != true) return;
            try
            {
                if (new FileInfo(picker.FileName).Length > ProfileImporter.MaxInputLength * 4L) throw new IOException("Файл слишком большой.");
                var text = File.ReadAllText(picker.FileName);
                if (text.Length > ProfileImporter.MaxInputLength) throw new IOException("Файл слишком большой.");
                input.Text = text;
            }
            catch { status.Text = "Не удалось прочитать файл. Нужен доступный JSON размером до 256 тысяч символов."; }
        };
        var cancel = DialogChrome.MakeButton("ОТМЕНА", false);
        cancel.Click += (_, _) => dialog.Close();
        var import = DialogChrome.MakeButton("СОЗДАТЬ ПРОФИЛЬ", true);
        import.Click += async (_, _) =>
        {
            var text = input.Text;
            import.IsEnabled = false;
            load.IsEnabled = false;
            input.IsEnabled = false;
            status.Text = text.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "Загружаю подписку… Можно отменить." : "Проверяю подключение…";
            try
            {
                var profile = await SubscriptionImporter.ImportAsync(text, lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                if (profile.NeedsName)
                {
                    var name = ConnectionNameDialog.Show(dialog);
                    if (lifetime.IsCancellationRequested) return;
                    if (name is null) { status.Text = "Для создания профиля задай название подключения."; return; }
                    profile = ProfileImporter.NameConnection(profile, name);
                }
                if (profile.InsecureTls && !UtilityDialogs.ShowConfirm(dialog, "ПРОВЕРКА СЕРТИФИКАТА ОТКЛЮЧЕНА",
                    "В профиле отключена проверка TLS-сертификата. Это снижает безопасность подключения. Сохранить именно эти настройки?", "СОХРАНИТЬ", "НАЗАД")) return;
                result = profile; dialog.Close();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (InvalidDataException ex) { if (!lifetime.IsCancellationRequested) { status.Text = ex.Message; status.SetResourceReference(TextBlock.ForegroundProperty, "ThemeAccent"); } }
            catch { if (!lifetime.IsCancellationRequested) status.Text = "Не удалось импортировать подключение. Проверь формат и параметры профиля."; }
            finally
            {
                if (!lifetime.IsCancellationRequested)
                {
                    import.IsEnabled = true;
                    load.IsEnabled = true;
                    input.IsEnabled = true;
                }
            }
        };
        var footer = new Grid(); footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(load);
        load.HorizontalAlignment = HorizontalAlignment.Left;
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        cancel.Margin = new Thickness(0, 0, 9, 0); actions.Children.Add(cancel); actions.Children.Add(import);
        Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        dialog.Content = DialogChrome.BuildFrame(dialog, "ИМПОРТ ПРОФИЛЯ", body, footer);
        dialog.Loaded += (_, _) => input.Focus();
        DialogChrome.ShowModal(owner, dialog);
        return result;
    }
}

internal static class ProfileStorage
{
    internal static async Task<string> CreateAsync(ImportedProfile imported, string corePath, string storageDirectory, CancellationToken token)
    {
        if (imported.NeedsName) throw new InvalidDataException("Сначала задай название подключения.");
        var directory = Path.Combine(storageDirectory, "Profiles");
        Directory.CreateDirectory(directory);
        var name = "profile-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var candidate = Path.Combine(directory, ".import-" + Guid.NewGuid().ToString("N") + ".yaml");
        var path = Path.Combine(directory, name + ".yaml");
        try
        {
            await Task.Run(() => AtomicFile.Write(candidate, imported.Yaml), token);
            var check = await CoreProcessManager.ValidateAsync(corePath, candidate, token);
            if (!check.Success) throw new InvalidDataException("Выбранное ядро не приняло импортированный профиль. Проверь совместимость ядра и параметры ссылки. Текущий профиль не изменён.");
            token.ThrowIfCancellationRequested();
            File.Move(candidate, path); // Unique target; never overwrite the current or an earlier imported profile.
            return path;
        }
        finally { if (File.Exists(candidate)) File.Delete(candidate); }
    }
}
