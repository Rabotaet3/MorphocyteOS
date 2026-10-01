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
        var body = new Grid { Margin = new Thickness(22, 16, 22, 18) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var explanation = new TextBlock
        {
            Text = "Вставь свою ссылку vless:// или JSON Xray. Программа создаст YAML с TUN и маршрутизацией по правилам. Поддерживаются TCP, WebSocket, gRPC, HTTPUpgrade, TLS и Reality.",
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
        body.Children.Add(explanation);
        var note = new TextBlock
        {
            Text = "Импортируется подключение, а не DNS / routing / inbounds Xray. По умолчанию трафик идёт напрямую; нужные сайты и приложения добавь в правила VPN. HTTPS-подписки пока не поддерживаются.",
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
            ToolTip = "Личные ключи сохраняются только в вашем локальном профиле."
        };
        Grid.SetRow(input, 2); body.Children.Add(input);
        var status = new TextBlock { Text = "Ссылка и ключи не отправляются в интернет при импорте.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
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
        import.Click += (_, _) =>
        {
            try
            {
                var profile = ProfileImporter.Parse(input.Text);
                if (profile.InsecureTls && !UtilityDialogs.ShowConfirm(dialog, "ПРОВЕРКА СЕРТИФИКАТА ОТКЛЮЧЕНА",
                    "В профиле отключена проверка TLS-сертификата. Это снижает безопасность подключения. Сохранить именно эти настройки?", "СОХРАНИТЬ", "НАЗАД")) return;
                result = profile; dialog.Close();
            }
            catch (InvalidDataException ex) { status.Text = ex.Message; status.SetResourceReference(TextBlock.ForegroundProperty, "ThemeAccent"); }
            catch { status.Text = "Не удалось импортировать подключение. Проверь формат и параметры профиля."; }
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
