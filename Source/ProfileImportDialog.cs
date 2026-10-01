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
            FontSize = 13, FontFamily = new System.Windows.Media.FontFamily("Consolas"), MaxLength = 8 * 1024 * 1024,
            VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(12),
            ToolTip = "Личные ключи сохраняются только в вашем локальном профиле."
        };
        Grid.SetRow(input, 2); body.Children.Add(input);
        var status = new TextBlock { Visibility = Visibility.Collapsed, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        Grid.SetRow(status, 3); body.Children.Add(status);
        string? loadedYaml = null;
        string? loadedYamlPath = null;
        var loadingYaml = false;
        input.TextChanged += (_, _) =>
        {
            if (loadingYaml) return;
            loadedYaml = null;
            loadedYamlPath = null;
        };
        void SetStatus(string message)
        {
            status.Text = message;
            status.Visibility = Visibility.Visible;
        }
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
            catch { SetStatus("Не удалось прочитать JSON. Проверь размер файла и доступ к нему."); }
        };
        var loadYaml = DialogChrome.MakeButton("Открыть YAML", false);
        loadYaml.Margin = new Thickness(8, 0, 0, 0);
        loadYaml.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "YAML (*.yaml;*.yml)|*.yaml;*.yml", CheckFileExists = true };
            if (picker.ShowDialog(dialog) != true) return;
            try
            {
                if (new FileInfo(picker.FileName).Length > input.MaxLength * 4L) throw new InvalidDataException("Размер YAML превышает 32 МБ.");
                var yaml = File.ReadAllText(picker.FileName);
                if (yaml.Length > input.MaxLength) throw new InvalidDataException("Размер YAML превышает 8 МБ.");
                var document = ClashConfigDocument.Parse(picker.FileName, yaml);
                var errors = document.Validate().Concat(document.ValidateForRouting()).ToArray();
                if (errors.Length > 0) throw new InvalidDataException(string.Join(" ", errors));
                if (!document.HasVpnConnection) throw new InvalidDataException("В YAML ещё нет VPN-подключения.");
                loadedYaml = yaml;
                loadedYamlPath = Path.GetFullPath(picker.FileName);
                loadingYaml = true;
                try { input.Text = yaml; }
                finally { loadingYaml = false; }
                SetStatus("YAML загружен. Нажми «Создать профиль», чтобы использовать его.");
            }
            catch (InvalidDataException ex) { SetStatus(ex.Message); }
            catch { SetStatus("Не удалось прочитать YAML. Проверь размер файла и доступ к нему."); }
        };
        var cancel = DialogChrome.MakeButton("ОТМЕНА", false);
        cancel.Click += (_, _) => dialog.Close();
        var import = DialogChrome.MakeButton("СОЗДАТЬ ПРОФИЛЬ", true);
        import.Click += (_, _) =>
        {
            try
            {
                var profile = loadedYaml is not null && input.Text == loadedYaml
                    ? new ImportedProfile(loadedYaml, "YAML-профиль", false, loadedYamlPath)
                    : ProfileImporter.Parse(input.Text);
                if (profile.InsecureTls && !UtilityDialogs.ShowConfirm(dialog, "ПРОВЕРКА СЕРТИФИКАТА ОТКЛЮЧЕНА",
                    "В профиле отключена проверка TLS-сертификата. Это снижает безопасность подключения. Сохранить именно эти настройки?", "СОХРАНИТЬ", "НАЗАД")) return;
                result = profile; dialog.Close();
            }
            catch (InvalidDataException ex) { SetStatus(ex.Message); status.SetResourceReference(TextBlock.ForegroundProperty, "ThemeAccent"); }
            catch { SetStatus("Не удалось импортировать подключение. Проверь формат и параметры профиля."); }
        };
        var footer = new Grid(); footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var fileActions = new StackPanel { Orientation = Orientation.Horizontal };
        fileActions.Children.Add(load);
        fileActions.Children.Add(loadYaml);
        footer.Children.Add(fileActions);
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
