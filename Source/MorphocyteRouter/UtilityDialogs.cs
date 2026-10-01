using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MorphocyteRouter;

internal sealed record SettingsDialogSelection(string ConfigPath, string CorePath, string Theme, double InterfaceScale, bool AutoCheckUpdates, ImportedProfile? Imported = null);

internal static class UtilityDialogs
{
    private static Brush Ink => ThemeManager.GetBrush("ThemeText");
    private static Brush Muted => ThemeManager.GetBrush("ThemeMuted");
    private static Brush Accent => ThemeManager.GetBrush("ThemeAccent");

    public static void ShowNotice(Window? owner, string title, string message, string buttonText = "ПОНЯТНО")
    {
        var dialog = DialogChrome.CreateWindow(owner, title, 540, 320, 450, 250, ResizeMode.CanResize);
        var text = new TextBlock { Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 16, 22, 18) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
        var close = DialogChrome.MakeButton(buttonText, true);
        close.Click += (_, _) => dialog.Close();
        dialog.Content = DialogChrome.BuildFrame(dialog, title, Scroll(text), Footer(close));
        DialogChrome.ShowModal(owner, dialog);
    }

    public static bool ShowConfirm(Window owner, string title, string message, string acceptText, string cancelText)
    {
        var dialog = DialogChrome.CreateWindow(owner, title, 590, 360, 500, 300, ResizeMode.CanResize);
        var text = new TextBlock { Text = message, Foreground = Ink, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(22, 16, 22, 18) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
        var result = false;
        var cancel = DialogChrome.MakeButton(cancelText, false);
        cancel.Margin = new Thickness(0, 0, 9, 0);
        cancel.Click += (_, _) => dialog.Close();
        var accept = DialogChrome.MakeButton(acceptText, true);
        accept.Click += (_, _) => { result = true; dialog.Close(); };
        dialog.Content = DialogChrome.BuildFrame(dialog, title, Scroll(text), Footer(cancel, accept));
        DialogChrome.ShowModal(owner, dialog);
        return result;
    }

    public static void ShowFaq(MainWindow owner)
    {
        var body = new StackPanel { Margin = new Thickness(22, 12, 22, 18) };
        AddFaq(body, "С чего начать?", "Ядро Mihomo поставляется рядом с приложением отдельным EXE, а нейтральный YAML-шаблон встроен. Нажми «Импорт профиля», вставь свою VLESS-ссылку или JSON Xray и создай YAML. Либо выбери готовый YAML в настройках. Чужих серверов и ключей в программе нет.");
        AddFaq(body, "Что делает импорт?", "Переносит одно VLESS-подключение из ссылки или JSON Xray в новый локальный YAML с TUN. DNS, inbounds и routing Xray не копируются: настройки формируются для Mihomo. По умолчанию трафик идёт напрямую; через VPN идут добавленные правила. HTTPS-подписки и другие протоколы пока не поддерживаются. Исходный профиль не перезаписывается.");
        AddFaq(body, "Как направить сайт через VPN?", "Введи домен, например example.com, выбери «Весь домен», маршрут VPN и нажми «Добавить». Затем нажми «Применить в YAML». «Весь домен» включает поддомены, «Точное имя» — только этот адрес, «По слову» — совпадение части домена.");
        AddFaq(body, "Как добавить целую программу?", "Нажми «Выбрать .exe» или «Запущенные процессы». В поле появится имя исполняемого файла. Добавь правило и примени изменения. Правило охватывает все соединения этой программы.");
        AddFaq(body, "Что означают DIRECT и REJECT?", "DIRECT направляет трафик через обычное подключение, REJECT блокирует его. Для исключения из правила приложения добавь нужный домен с DIRECT.");
        AddFaq(body, "Как работают папки и выделение?", "Перетащи правило на другое, чтобы создать папку, или в существующую папку. Ctrl + клик выбирает несколько правил, Shift + клик — диапазон. Галочка выбранного правила переключает всю выделенную группу. Галочка папки переключает все её правила, включая скрытые поиском.");
        AddFaq(body, "Как перемещать и удалять папки?", "Тяни заголовок папки вверх или вниз для изменения порядка. Правила можно вынести в «Общие правила». При удалении папки её правила остаются в общих.");
        AddFaq(body, "Изменения сохраняются автоматически?", "Список, папки, отключённые правила и оформление сохраняются между запусками. Черновик начинает влиять на трафик после «Применить в YAML». Перед записью профиль проверяется ядром; остаются три последние резервные копии.");
        AddFaq(body, "Почему правило не действует?", "Проверь, что профиль работает в режиме rule, TUN включён, VPN запущен и изменения применены. Иногда сервису нужны дополнительные домены. Если другое правило совпало раньше, оно может иметь приоритет.");
        AddFaq(body, "Почему после удаления правила трафик всё ещё идёт через VPN?", "У профиля есть остальные правила и действие по умолчанию (MATCH). Они сохраняются: удаление правила программы не заставляет весь её трафик идти напрямую. Сложные RULE-SET, GEOIP и другие правила не показаны в этом редакторе; их нужно проверять в самом YAML.");
        AddFaq(body, "Как настроить внешний вид?", "В настройках можно выбрать тему и масштаб. Масштаб меняет кнопки, списки, окна и шрифты. Журнал событий можно скрыть переключателем в нижней части окна.");
        AddFaq(body, "Как обновлять приложение?", "Ручная проверка доступна в настройках. Автоматическая проверка выполняется при запуске, если ты её включил. При доступном релизе программа предлагает открыть страницу загрузки.");
        var dialog = DialogChrome.CreateWindow(owner, "ЧАСТЫЕ ВОПРОСЫ", 680, 690, 530, 480, ResizeMode.CanResize);
        var close = DialogChrome.MakeButton("ЗАКРЫТЬ", false);
        close.Click += (_, _) => dialog.Close();
        dialog.Content = DialogChrome.BuildFrame(dialog, "ЧАСТЫЕ ВОПРОСЫ", Scroll(body), Footer(close));
        DialogChrome.ShowModal(owner, dialog);
    }

    public static SettingsDialogSelection? ShowSettings(MainWindow owner, string configPath, string corePath, string theme,
        bool coreRunning, double interfaceScale = 1, bool autoCheckUpdates = false)
    {
        var dialog = DialogChrome.CreateWindow(owner, "НАСТРОЙКИ", 660, 650, 590, 500, ResizeMode.CanResize);
        using var lifetime = new CancellationTokenSource();
        dialog.Closed += (_, _) => lifetime.Cancel();
        var selectedConfig = configPath;
        var selectedCore = corePath;
        ImportedProfile? importedProfile = null;
        var selectedTheme = ThemeManager.Normalize(theme);
        var selectedScale = AppSettings.NormalizeScale(interfaceScale);
        SettingsDialogSelection? result = null;

        var layout = new Grid { Margin = new Thickness(22, 12, 22, 16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var pages = new Grid();
        Grid.SetRow(pages, 1);
        layout.Children.Add(navigation);
        layout.Children.Add(pages);

        var appearance = new StackPanel();
        var profile = new StackPanel();
        var updates = new StackPanel();
        var pageViews = new[] { Scroll(appearance), Scroll(profile), Scroll(updates) };
        foreach (var page in pageViews) pages.Children.Add(page);
        var tabs = new List<Button>();
        var names = new[] { "Внешний вид", "VPN-профиль", "Обновления" };
        void SelectTab(int index)
        {
            for (var i = 0; i < pageViews.Length; i++)
            {
                pageViews[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
                tabs[i].SetResourceReference(Control.BackgroundProperty, i == index ? "ThemeSurfaceSelected" : "ThemeButton");
                tabs[i].SetResourceReference(Control.BorderBrushProperty, i == index ? "ThemeAccent" : "ThemeButtonBorder");
            }
        }
        for (var i = 0; i < names.Length; i++)
        {
            var index = i;
            var tab = DialogChrome.MakeButton(names[i], false);
            tab.Margin = new Thickness(0, 0, 8, 0);
            tab.Click += (_, _) => SelectTab(index);
            tabs.Add(tab);
            navigation.Children.Add(tab);
        }
        SelectTab(0);

        var themePicker = new ComboBox { ItemsSource = ThemeManager.ThemeNames, SelectedItem = selectedTheme, Margin = new Thickness(0, 9, 0, 0) };
        themePicker.SelectionChanged += (_, _) =>
        {
            if (themePicker.SelectedItem is not string next) return;
            selectedTheme = next;
            ThemeManager.Apply(next);
        };
        appearance.Children.Add(Card("ТЕМА", "Цвета меняются сразу — можно посмотреть оформление перед сохранением.", themePicker));
        var scaleOptions = new[] { .8, .9, 1, 1.1, 1.25, 1.5 };
        var scalePicker = new ComboBox { ItemsSource = scaleOptions.Select(value => $"{value * 100:0}%").ToArray(), Margin = new Thickness(0, 9, 0, 0) };
        scalePicker.SelectedIndex = Enumerable.Range(0, scaleOptions.Length).MinBy(index => Math.Abs(scaleOptions[index] - selectedScale));
        var sample = new TextBlock { Text = "Морфоцит OS · Пример текста", FontSize = 15 * selectedScale, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        sample.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
        scalePicker.SelectionChanged += (_, _) =>
        {
            if (scalePicker.SelectedIndex < 0) return;
            selectedScale = scaleOptions[scalePicker.SelectedIndex];
            sample.FontSize = 15 * selectedScale;
        };
        var scaleBody = new StackPanel();
        scaleBody.Children.Add(scalePicker);
        scaleBody.Children.Add(sample);
        appearance.Children.Add(Card("МАСШТАБ ИНТЕРФЕЙСА", "Меняет весь интерфейс: шрифты, кнопки, списки и дополнительные окна. Применяется после сохранения.", scaleBody));

        var configValue = PathValue(selectedConfig);
        var coreValue = PathValue(selectedCore);
        profile.Children.Add(PathSection("ВАШ ПРОФИЛЬ YAML", configValue, "Выбрать YAML", () =>
        {
            var picker = new OpenFileDialog { Filter = "YAML (*.yaml;*.yml)|*.yaml;*.yml", CheckFileExists = true };
            if (File.Exists(selectedConfig)) picker.InitialDirectory = Path.GetDirectoryName(selectedConfig);
            if (picker.ShowDialog(dialog) == true) { selectedConfig = picker.FileName; importedProfile = null; UpdatePath(configValue, selectedConfig); }
        }));
        var importActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var importProfile = DialogChrome.MakeButton("Импорт профиля", true);
        importProfile.Margin = new Thickness(0, 0, 8, 8);
        importProfile.Click += (_, _) =>
        {
            if (ProfileImportDialog.Show(dialog) is not { } imported) return;
            importedProfile = imported;
            configValue.Text = "Новый профиль: " + imported.Description + " · будет создан после сохранения";
            configValue.ToolTip = null;
        };
        var template = DialogChrome.MakeButton("Создать шаблон", false);
        template.Margin = new Thickness(0, 0, 0, 8);
        template.Click += (_, _) =>
        {
            importedProfile = new ImportedProfile(BundledResources.TemplateText, "Нейтральный шаблон", false);
            configValue.Text = "Новый шаблон без сервера · будет создан после сохранения";
            configValue.ToolTip = null;
        };
        importActions.Children.Add(importProfile); importActions.Children.Add(template); profile.Children.Add(importActions);
        profile.Children.Add(PathSection("ЯДРО MIHOMO", coreValue, "Выбрать ядро", () =>
        {
            var picker = new OpenFileDialog { Filter = "Приложение Windows (*.exe)|*.exe", CheckFileExists = true };
            if (File.Exists(selectedCore)) picker.InitialDirectory = Path.GetDirectoryName(selectedCore);
            if (picker.ShowDialog(dialog) == true) { selectedCore = picker.FileName; UpdatePath(coreValue, selectedCore); }
        }, coreRunning));
        var bundled = DialogChrome.MakeButton("Использовать ядро из комплекта", false);
        bundled.IsEnabled = !coreRunning;
        bundled.HorizontalAlignment = HorizontalAlignment.Left;
        bundled.Click += async (_, _) =>
        {
            bundled.IsEnabled = false;
            try
            {
                selectedCore = await BundledResources.EnsureCoreAsync(lifetime.Token);
                if (!lifetime.IsCancellationRequested) UpdatePath(coreValue, selectedCore);
            }
            catch (OperationCanceledException) { }
            catch { if (!lifetime.IsCancellationRequested) coreValue.Text = "Не удалось проверить ядро из комплекта. Убедись, что mihomo.exe лежит рядом с приложением."; }
            finally { if (!lifetime.IsCancellationRequested) bundled.IsEnabled = true; }
        };
        profile.Children.Add(bundled);
        profile.Children.Add(Paragraph(coreRunning ? "Перед сменой ядра останови VPN." : "Профиль и ядро выбираются независимо. Название YAML и имя файла приложения могут быть любыми."));
        profile.Children.Add(Paragraph("Профиль содержит секретные данные подключения. Не отправляй его незнакомым людям. Программа сохраняет настройки только на этом компьютере."));

        var automatic = new CheckBox { IsChecked = autoCheckUpdates, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var autoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
        autoRow.Children.Add(automatic);
        var autoCaption = new TextBlock { Text = "Проверять автоматически при запуске", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        autoCaption.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
        autoRow.Children.Add(autoCaption);
        updates.Children.Add(Card("АВТООБНОВЛЕНИЕ", "При появлении новой версии предложит открыть страницу загрузки. Установка и перезапуск происходят по твоему решению.", autoRow));
        var status = new TextBlock { Text = ReleaseUpdateService.SourceStatus, Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        var check = DialogChrome.MakeButton("Проверить актуальный релиз", true);
        check.MinWidth = 240;
        var open = DialogChrome.MakeButton("Открыть страницу релиза", false);
        open.Visibility = Visibility.Collapsed;
        open.Margin = new Thickness(0, 9, 0, 0);
        string? releaseUrl = null;
        open.Click += (_, _) =>
        {
            if (releaseUrl is null) return;
            try { Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true }); }
            catch (Exception ex) { status.Text = "Не удалось открыть страницу: " + ex.Message; }
        };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            status.Text = "Проверяю релиз…";
            open.Visibility = Visibility.Collapsed;
            try
            {
                var found = await ReleaseUpdateService.CheckAsync(lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                status.Text = found.Message;
                releaseUrl = found.ReleaseUrl;
                open.Visibility = found.UpdateAvailable && releaseUrl is not null ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) status.Text = "Проверка не выполнена: " + ex.Message; }
            finally { if (!lifetime.IsCancellationRequested) check.IsEnabled = true; }
        };
        var updateBody = new StackPanel();
        var versionText = new TextBlock { Text = "Установленная версия: " + BuildInfo.Version, FontSize = 13 };
        versionText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
        updateBody.Children.Add(versionText);
        updateBody.Children.Add(status);
        updateBody.Children.Add(check);
        updateBody.Children.Add(open);
        updates.Children.Add(Card("РЕЛИЗ", null, updateBody));

        var cancel = DialogChrome.MakeButton("ОТМЕНА", false);
        cancel.Margin = new Thickness(0, 0, 9, 0);
        cancel.Click += (_, _) => dialog.Close();
        var save = DialogChrome.MakeButton("СОХРАНИТЬ", true);
        save.IsDefault = true;
        save.Click += (_, _) =>
        {
            result = new SettingsDialogSelection(selectedConfig, selectedCore, selectedTheme, selectedScale, automatic.IsChecked == true, importedProfile);
            dialog.Close();
        };
        dialog.Content = DialogChrome.BuildFrame(dialog, "НАСТРОЙКИ", layout, Footer(cancel, save));
        DialogChrome.ShowModal(owner, dialog);
        if (result is null) ThemeManager.Apply(theme);
        return result;
    }

    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        CanContentScroll = false
    };

    private static StackPanel Footer(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    private static TextBlock Paragraph(string text)
    {
        var paragraph = new TextBlock
        {
            Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            LineHeight = 19, Margin = new Thickness(2, 8, 2, 8)
        };
        paragraph.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        return paragraph;
    }

    private static Border Card(string title, string? description, UIElement body)
    {
        var stack = new StackPanel();
        var caption = new TextBlock { Text = title, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "ThemeAccent");
        stack.Children.Add(caption);
        if (description is not null) stack.Children.Add(Paragraph(description));
        stack.Children.Add(body);
        var card = new Border { Child = stack, Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "ThemeSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "ThemeSurfaceBorder");
        return card;
    }

    private static Border PathSection(string title, TextBlock value, string caption, Action choose, bool disabled = false)
    {
        var button = DialogChrome.MakeButton(caption, false);
        button.IsEnabled = !disabled;
        button.Margin = new Thickness(0, 12, 0, 0);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Click += (_, _) => choose();
        var body = new StackPanel();
        body.Children.Add(value);
        body.Children.Add(button);
        return Card(title, null, body);
    }

    private static TextBlock PathValue(string path)
    {
        var value = new TextBlock { Foreground = Ink, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        value.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
        UpdatePath(value, path);
        return value;
    }

    private static void UpdatePath(TextBlock text, string path)
    {
        text.Text = string.IsNullOrWhiteSpace(path) ? "Не выбран" : Path.GetFileName(path);
        text.ToolTip = string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static void AddFaq(Panel panel, string title, string answer) => panel.Children.Add(Card(title, null, Paragraph(answer)));
}
