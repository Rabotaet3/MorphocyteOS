using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MorphocyteRouter;

internal sealed record SettingsDialogSelection(string ConfigPath, string CorePath, string Theme, double InterfaceScale, bool AutoCheckUpdates,
    ImportedProfile? Imported = null, bool LaunchAtSignIn = false, bool AutoConnectOnStartup = true, ConfigurationPackage? Transfer = null,
    bool AutoReconnect = true, bool AutoConnectOnOpen = false);

internal static partial class UtilityDialogs
{
    private static Brush Ink => ThemeManager.GetBrush("ThemeText");
    private static Brush Muted => ThemeManager.GetBrush("ThemeMuted");

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
        AddFaq(body, "Как подключиться впервые?", "Открой «Подписка» и добавь ссылку провайдера. Для готового YAML, ссылки на отдельный сервер или JSON Xray используй «Настройки → VPN-профиль». Выбери сервер и нажми «Запустить VPN». Основное ядро sing-box входит в сборку. Mihomo можно выбрать в настройках.");
        AddFaq(body, "Что делает тумблер TUN?", "При включённом TUN трафик по умолчанию идёт через VPN с учётом правил «Напрямую» и «Блокировать». При выключенном TUN действуют правила профиля и его маршрут по умолчанию. Смена положения тумблера переподключает запущенный VPN.");
        AddFaq(body, "Как подключить только выбранные приложения?", $"Открой «Настройки → VPN-профиль → Локальный прокси» и включи его. В нужном приложении укажи HTTP или SOCKS5 по адресу 127.0.0.1:{CoreBackend.LocalProxyPort}. Этот режим работает без TUN.");
        AddFaq(body, "Как обновить подписку и проверить серверы?", "На вкладке «Подписка» нажми «Обновить». Морфоцит сохранит твои правила и папки. Если список серверов изменился, активный VPN может переподключиться. Кнопка PING замеряет задержку HTTPS через каждый сервер. Повторное нажатие во время замера останавливает проверку.");
        AddFaq(body, "Как направить сайт через VPN?", "Введи домен, например example.com, выбери «Весь домен», маршрут VPN и нажми «Добавить». Затем нажми «Применить в YAML». «Весь домен» включает поддомены, «Точное имя» — только этот адрес, «По слову» — совпадение части домена.");
        AddFaq(body, "Как добавить целую программу?", "Нажми «Выбрать .exe» или «Запущенные процессы». Выбранные приложения добавляются в черновик правил; можно выбрать несколько через Ctrl/Shift. Затем нажми «Применить в YAML». Правило охватывает соединения этой программы.");
        AddFaq(body, "Как изменить маршрут правила?", "Нажми маршрут справа в строке правила и выбери VPN, «Напрямую» или «Блокировать». Если строка входит в выделенную группу, маршрут изменится у всех выделенных правил. Изменения начнут действовать после «Применить в YAML».");
        AddFaq(body, "Что означают DIRECT и REJECT?", "DIRECT направляет трафик через обычное подключение, REJECT блокирует его. Для исключения из правила приложения добавь нужный домен с DIRECT.");
        AddFaq(body, "Как работают папки и выделение?", "Перетащи правило на другое, чтобы создать папку, или в существующую папку. Ctrl + клик выбирает несколько правил, Shift + клик — диапазон. Галочка выбранного правила переключает всю выделенную группу. Галочка папки переключает все её правила, включая скрытые поиском.");
        AddFaq(body, "Как перемещать и удалять папки?", "Тяни заголовок папки вверх или вниз для изменения порядка. Правила можно вынести в «Общие правила». При удалении папки её правила остаются в общих.");
        AddFaq(body, "Изменения сохраняются автоматически?", "Список, папки, отключённые правила и оформление сохраняются между запусками. Черновик начинает влиять на трафик после «Применить в YAML». Перед записью профиль проверяется ядром; остаются три последние резервные копии.");
        AddFaq(body, "Как отменить изменение правил?", "Кнопки «Отменить» и ↷ восстанавливают изменения правил и папок до применения YAML. Ctrl+Z отменяет изменение, Ctrl+Y повторяет. После применения начинается новая история.");
        AddFaq(body, "Почему правило не действует?", "Проверь, что VPN запущен и изменения применены в YAML. В режиме правил учитываются порядок совпадений и маршрут MATCH. С включённым TUN учитываются явные прямые маршруты и блокировки. Выбери соединение в журнале и нажми «Подробнее», чтобы увидеть фактическое правило и маршрут.");
        AddFaq(body, "Почему после удаления правила трафик всё ещё идёт через VPN?", "У профиля есть остальные правила и действие по умолчанию (MATCH). Они сохраняются: удаление правила программы не заставляет весь её трафик идти напрямую. Сложные RULE-SET, GEOIP и другие правила не показаны в этом редакторе; их нужно проверять в самом YAML.");
        AddFaq(body, "Как вернуть черновик после правки YAML?", "После загрузки изменённого профиля в «Маршрутах» появится «Сравнить черновик», если сохранилась прежняя версия. Перенеси нужные правила в текущий черновик и нажми «Применить в YAML».");
        AddFaq(body, "Как настроить внешний вид и открыть журнал?", "Тема и масштаб находятся в настройках. Вкладка «Журнал и соединения» показывает события ядра, процессы и активные соединения. В деталях соединения можно посмотреть адрес назначения, сработавшее правило и фактический маршрут.");
        AddFaq(body, "Как работают трей и автозапуск?", "Крестик сворачивает окно в трей, оставляя VPN работать. Команда «Выход» в трее останавливает VPN и закрывает приложение. В настройках → «Запуск» есть отдельные варианты подключения при открытии Морфоцита и при входе в Windows. Автоподключение использует сохранённый профиль.");
        AddFaq(body, "Как перенести настройки?", "В настройках → «VPN-профиль» → «Перенос» нажми «Экспорт настроек». Файл .morphocyte содержит выбранный профиль с данными подключения, правила, папки и параметры приложения. На другом компьютере выбери «Импорт настроек» и «Сохранить». Создаётся отдельная копия профиля; неприменённые правила остаются черновиком. Ядро берётся с нового компьютера. Файл содержит секреты VPN — передавай его только доверенному человеку.");
        AddFaq(body, "Что означают статус, пинг и скорость?", "«VPN отвечает» появляется после успешного HTTPS-запроса. Пинг показывает задержку этого запроса. «Приём» и «Отдача» показывают текущий трафик через ядро. Кнопка «Скорость» запускает отдельный замер пропускной способности.");
        AddFaq(body, "Что делать, если VPN не отвечает?", "Попробуй другой сервер или открой «Журнал и соединения → Проверить подключение». Диагностика покажет этап, на котором возникла проблема. Кнопка «Копировать диагностику» копирует результаты проверки без профиля и ключей.");
        AddFaq(body, "Что происходит после сна или смены сети?", "Приложение проверяет работавшее подключение и при необходимости перезапускает своё ядро, до трёх попыток. Ручное отключение VPN отменяет восстановление. Неприменённые правила остаются черновиком. В настройках → «Запуск» восстановление можно отключить.");
        AddFaq(body, "Как обновлять приложение?", "Автоматическая проверка ищет новую версию на GitHub при запуске и каждые 30 минут. Она только показывает уведомление: загрузка и установка начинаются исключительно по кнопке пользователя. В настройках → «Обновления» можно отключить проверку или проверить релиз вручную. Установка остановит VPN; личные профили и настройки сохраняются. После обновления запусти VPN вручную.");
        var dialog = DialogChrome.CreateWindow(owner, "ЧаВо?", 680, 690, 530, 480, ResizeMode.CanResize);
        var close = DialogChrome.MakeButton("ЗАКРЫТЬ", false);
        close.Click += (_, _) => dialog.Close();
        dialog.Content = DialogChrome.BuildFrame(dialog, "ЧаВо?", Scroll(body), Footer(close));
        DialogChrome.ShowModal(owner, dialog);
    }

    public static SettingsDialogSelection? ShowSettings(MainWindow owner, string configPath, string corePath, string theme,
        bool coreRunning, double interfaceScale, bool autoCheckUpdates,
        Func<SettingsDialogSelection, Task<SettingsDialogSelection?>>? onSave)
    {
        var dialog = DialogChrome.CreateWindow(owner, "НАСТРОЙКИ", 660, 650, 590, 500, ResizeMode.CanResize);
        using var lifetime = new CancellationTokenSource();
        dialog.Closed += (_, _) =>
        {
            // A dialog can also be destroyed by its owner after construction failed.
            // In that case the method's using scope has already disposed the source.
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        };
        var selectedConfig = configPath;
        var selectedCore = corePath;
        ImportedProfile? importedProfile = null;
        ConfigurationPackage? pendingTransfer = null;
        var selectedTheme = ThemeManager.Normalize(theme);
        var selectedScale = AppSettings.NormalizeScale(interfaceScale);
        SettingsDialogSelection? result = null;
        var committedTheme = selectedTheme;

        var layout = new Grid { Margin = new Thickness(22, 12, 22, 16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        var navigation = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var sections = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
        var settingsScroll = Scroll(sections);
        settingsScroll.Name = "SettingsScroll";
        Grid.SetRow(settingsScroll, 1);
        layout.Children.Add(navigation);
        layout.Children.Add(settingsScroll);

        var appearance = new StackPanel { Name = "SettingsAppearance" };
        var profile = new StackPanel { Name = "SettingsProfile" };
        var updates = new StackPanel { Name = "SettingsUpdates" };
        var startup = new StackPanel { Name = "SettingsStartup" };
        var sectionViews = new[] { appearance, profile, updates, startup };
        var names = new[] { "Внешний вид", "VPN-профиль", "Обновления", "Запуск" };
        for (var i = 0; i < sectionViews.Length; i++)
        {
            var section = sectionViews[i];
            section.Margin = new Thickness(0, 0, 0, 18);
            var heading = new TextBlock { Text = names[i], FontSize = 16, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 4, 0, 12) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
            section.Children.Add(heading);
            sections.Children.Add(section);
        }
        var tabs = new List<Button>();
        void HighlightSection(int index)
        {
            for (var i = 0; i < tabs.Count; i++)
            {
                tabs[i].SetResourceReference(Control.BackgroundProperty, i == index ? "ThemeSurfaceSelected" : "ThemeButton");
                tabs[i].SetResourceReference(Control.BorderBrushProperty, i == index ? "ThemeAccent" : "ThemeButtonBorder");
            }
        }
        for (var i = 0; i < names.Length; i++)
        {
            var index = i;
            var tab = DialogChrome.MakeButton(names[i], false);
            tab.Margin = new Thickness(0, 0, 8, 4);
            tab.Click += (_, _) =>
            {
                dialog.UpdateLayout();
                settingsScroll.ScrollToVerticalOffset(sectionViews[index].TranslatePoint(new Point(), sections).Y);
            };
            tabs.Add(tab);
            navigation.Children.Add(tab);
        }
        settingsScroll.ScrollChanged += (_, _) =>
        {
            var index = 0;
            for (var i = 1; i < sectionViews.Length; i++)
                if (sectionViews[i].TranslatePoint(new Point(), sections).Y <= settingsScroll.VerticalOffset + 20) index = i;
            if (settingsScroll.ScrollableHeight > 0 && settingsScroll.VerticalOffset >= settingsScroll.ScrollableHeight - 1)
                index = sectionViews.Length - 1;
            HighlightSection(index);
        };
        HighlightSection(0);

        CheckBox LabeledOption(string text, bool value)
        {
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
            var option = new CheckBox { Content = label, IsChecked = value, Focusable = true,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(option, text);
            return option;
        }
        var launchAtSignIn = LabeledOption("Запускать приложение при входе в Windows", owner.LaunchAtSignIn);
        var autoConnect = LabeledOption("Подключать VPN при входе в Windows", owner.AutoConnectOnStartup);
        var connectOnOpen = LabeledOption("Подключать VPN при открытии Морфоцита", owner.AutoConnectOnOpen);
        autoConnect.IsEnabled = launchAtSignIn.IsChecked == true;
        launchAtSignIn.Checked += (_, _) => autoConnect.IsEnabled = true;
        launchAtSignIn.Unchecked += (_, _) => autoConnect.IsEnabled = false;
        var startupOptions = new StackPanel();
        startupOptions.Children.Add(launchAtSignIn);
        startupOptions.Children.Add(autoConnect);
        startupOptions.Children.Add(connectOnOpen);
        startup.Children.Add(Card("АВТОЗАПУСК", null, startupOptions));
        var autoReconnect = LabeledOption("Переподключать VPN при пробуждении компьютера и смене сети", owner.AutoReconnect);
        autoReconnect.Name = "AutoReconnectOption";
        startup.Children.Add(Card("ПОДКЛЮЧЕНИЕ", "Если VPN был включён, приложение попробует восстановить его после выхода компьютера из спящего режима или перехода на другую сеть.", autoReconnect));

        var themePicker = new ComboBox { ItemsSource = ThemeManager.ThemeNames, SelectedItem = selectedTheme, Margin = new Thickness(0, 9, 0, 0) };
        themePicker.SelectionChanged += (_, _) =>
        {
            if (themePicker.SelectedItem is not string next) return;
            selectedTheme = next;
            ThemeManager.Apply(next);
        };
        appearance.Children.Add(Card("ТЕМА", null, themePicker));
        var scaleOptions = new List<double> { .8, .9, 1, 1.1, 1.25, 1.5 };
        if (!scaleOptions.Contains(selectedScale)) { scaleOptions.Add(selectedScale); scaleOptions.Sort(); }
        var scalePicker = new ComboBox { ItemsSource = scaleOptions.Select(value => $"{value * 100:0}%").ToArray(), Margin = new Thickness(0, 9, 0, 0) };
        scalePicker.SelectedIndex = scaleOptions.IndexOf(selectedScale);
        var sample = new TextBlock { Text = "Морфоцит OS · Пример текста", FontSize = 15 * selectedScale, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        sample.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
        scalePicker.SelectionChanged += (_, _) =>
        {
            if (scalePicker.SelectedIndex < 0) return;
            selectedScale = scaleOptions[scalePicker.SelectedIndex];
            sample.FontSize = 15 * selectedScale;
        };
        void SelectScale(double value)
        {
            selectedScale = value;
            if (!scaleOptions.Contains(value)) { scaleOptions.Add(value); scaleOptions.Sort(); }
            scalePicker.ItemsSource = scaleOptions.Select(item => $"{item * 100:0}%").ToArray();
            scalePicker.SelectedIndex = scaleOptions.IndexOf(value);
        }
        var scaleBody = new StackPanel();
        scaleBody.Children.Add(scalePicker);
        scaleBody.Children.Add(sample);
        appearance.Children.Add(Card("МАСШТАБ ИНТЕРФЕЙСА", null, scaleBody));

        var configValue = PathValue(selectedConfig);
        var coreValue = PathValue(selectedCore);
        profile.Children.Add(PathSection("ВАШ ПРОФИЛЬ YAML", configValue, "Выбрать YAML", () =>
        {
            var picker = new OpenFileDialog { Filter = "YAML (*.yaml;*.yml)|*.yaml;*.yml", CheckFileExists = true };
            if (File.Exists(selectedConfig)) picker.InitialDirectory = Path.GetDirectoryName(selectedConfig);
            if (picker.ShowDialog(dialog) == true) { selectedConfig = picker.FileName; importedProfile = null; pendingTransfer = null; UpdatePath(configValue, selectedConfig); }
        }));
        var importActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var importProfile = DialogChrome.MakeButton("Импорт профиля", true);
        importProfile.Margin = new Thickness(0, 0, 8, 8);
        importProfile.Click += (_, _) =>
        {
            if (ProfileImportDialog.Show(dialog) is not { } imported) return;
            pendingTransfer = null;
            importedProfile = imported;
            configValue.Text = "Новый профиль: " + imported.Description + " · будет создан после сохранения";
            configValue.ToolTip = null;
        };
        var template = DialogChrome.MakeButton("Создать шаблон", false);
        template.Margin = new Thickness(0, 0, 0, 8);
        template.Click += (_, _) =>
        {
            pendingTransfer = null;
            importedProfile = new ImportedProfile(BundledResources.TemplateText, "Нейтральный шаблон", false);
            configValue.Text = "Новый шаблон без сервера · будет создан после сохранения";
            configValue.ToolTip = null;
        };
        importActions.Children.Add(importProfile); importActions.Children.Add(template); profile.Children.Add(importActions);
        var corePicker = new ComboBox { Name = "CoreBackendPicker", ItemsSource = new[] { "Mihomo", "sing-box" },
            SelectedIndex = CoreBackend.IsSingBox(selectedCore) ? 1 : 0, Margin = new Thickness(0, 9, 0, 0), IsEnabled = !coreRunning };
        var syncingCorePicker = false;
        void SyncCorePicker()
        {
            syncingCorePicker = true;
            try { corePicker.SelectedIndex = CoreBackend.IsSingBox(selectedCore) ? 1 : 0; }
            finally { syncingCorePicker = false; }
        }
        corePicker.SelectionChanged += (_, _) =>
        {
            if (syncingCorePicker || corePicker.SelectedIndex < 0) return;
            // Hash and native checks run before Save commits this choice.
            selectedCore = corePicker.SelectedIndex == 1 ? CoreBackend.BundledSingBoxPath : Path.Combine(AppContext.BaseDirectory, BundledResources.CoreFileName);
            UpdatePath(coreValue, selectedCore);
        };
        profile.Children.Add(Card("ЯДРО ПОДКЛЮЧЕНИЯ", null, corePicker));
        profile.Children.Add(PathSection("Установленное ядро:", coreValue, "Выбрать ядро", () =>
        {
            var picker = new OpenFileDialog { Filter = "Приложение Windows (*.exe)|*.exe", CheckFileExists = true };
            if (File.Exists(selectedCore)) picker.InitialDirectory = Path.GetDirectoryName(selectedCore);
            if (picker.ShowDialog(dialog) == true) { selectedCore = picker.FileName; UpdatePath(coreValue, selectedCore); SyncCorePicker(); }
        }, coreRunning));
        var bundled = DialogChrome.MakeButton("Использовать ядро из комплекта", false);
        bundled.IsEnabled = !coreRunning;
        bundled.HorizontalAlignment = HorizontalAlignment.Left;
        bundled.Click += async (_, _) =>
        {
            bundled.IsEnabled = false;
            try
            {
                selectedCore = corePicker.SelectedIndex == 1 ? await CoreBackend.EnsureSingBoxAsync(lifetime.Token) : await BundledResources.EnsureCoreAsync(lifetime.Token);
                if (!lifetime.IsCancellationRequested) UpdatePath(coreValue, selectedCore);
            }
            catch (OperationCanceledException) { }
            catch { if (!lifetime.IsCancellationRequested) coreValue.Text = "Не удалось проверить выбранное ядро из комплекта. Распакуй все файлы рядом с приложением."; }
            finally { if (!lifetime.IsCancellationRequested) bundled.IsEnabled = true; }
        };
        profile.Children.Add(bundled);
        if (coreRunning) profile.Children.Add(Paragraph("Перед сменой ядра останови VPN."));

        var automatic = LabeledOption("Проверять наличие обновлений", autoCheckUpdates);
        updates.Children.Add(Card("ОБНОВЛЕНИЯ", null, automatic));
        var status = new TextBlock { Text = ReleaseUpdateService.SourceStatus, Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        var check = DialogChrome.MakeButton("Проверить обновления", false);
        check.HorizontalAlignment = HorizontalAlignment.Left;
        var download = DialogChrome.MakeButton("ОБНОВИТЬ ПРИЛОЖЕНИЕ", true);
        download.Margin = new Thickness(0, 9, 0, 0);
        void RefreshUpdate(UpdateCheckResult? found)
        {
            download.Visibility = Visibility.Visible;
            download.Content = found is { UpdateAvailable: true } && (found.Asset is not null || found.IndexAsset is not null)
                ? "ОБНОВИТЬ ДО " + found.LatestVersion : "ОБНОВИТЬ ПРИЛОЖЕНИЕ";
            status.Text = found?.Message ?? ReleaseUpdateService.SourceStatus;
        }
        RefreshUpdate(owner.AvailableUpdate);
        owner.UpdateStateChanged += RefreshUpdate;
        dialog.Closed += (_, _) => owner.UpdateStateChanged -= RefreshUpdate;
        download.Click += async (_, _) =>
        {
            download.IsEnabled = false; check.IsEnabled = false;
            try { await owner.InstallAvailableUpdateAsync(dialog); }
            finally { if (!lifetime.IsCancellationRequested) { download.IsEnabled = true; check.IsEnabled = true; } }
        };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            status.Text = "Проверяю релиз…";
            try
            {
                var found = await owner.CheckUpdatesAsync(lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                status.Text = found.Message;
                RefreshUpdate(found);
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
        updateBody.Children.Add(download);
        updates.Children.Add(Card("РЕЛИЗ", null, updateBody));

        var cancel = DialogChrome.MakeButton("ОТМЕНА", false);
        cancel.Margin = new Thickness(0, 0, 9, 0);
        cancel.Click += (_, _) => dialog.Close();
        var save = DialogChrome.MakeButton("СОХРАНИТЬ", true);
        save.IsDefault = true;
        var saveFeedback = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0),
            FontSize = 11
        };
        saveFeedback.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(saveFeedback);
        Grid.SetColumn(cancel, 2);
        Grid.SetColumn(save, 3);
        footer.Children.Add(cancel);
        footer.Children.Add(save);
        SettingsDialogSelection CurrentSelection() => new(selectedConfig, selectedCore, selectedTheme,
            selectedScale, automatic.IsChecked == true, importedProfile, launchAtSignIn.IsChecked == true,
            autoConnect.IsChecked == true, pendingTransfer, autoReconnect.IsChecked == true, connectOnOpen.IsChecked == true);
        var subscriptionActions = new WrapPanel();
        var sourceButton = DialogChrome.MakeButton("Ссылка подписки", false);
        var refreshSubscription = DialogChrome.MakeButton("Обновить подписку", false);
        sourceButton.Margin = new Thickness(0, 0, 8, 6); refreshSubscription.Margin = new Thickness(0, 0, 0, 6);
        var subscriptionFeedback = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        subscriptionFeedback.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        bool SavedProfileSelected() => importedProfile is null && pendingTransfer is null
            && string.Equals(selectedConfig, owner.CurrentProfilePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(selectedCore, owner.CurrentCorePath, StringComparison.OrdinalIgnoreCase);
        sourceButton.Click += (_, _) =>
        {
            if (!SavedProfileSelected()) { subscriptionFeedback.Text = "Сначала сохрани выбранный профиль."; return; }
            var applied = owner.ShowSubscriptionSource(dialog);
            subscriptionFeedback.Text = applied ? "Подписка применена; список серверов обновлён." : "Подписка не изменена.";
        };
        subscriptionActions.Children.Add(sourceButton); subscriptionActions.Children.Add(refreshSubscription);
        var subscriptionBody = new StackPanel(); subscriptionBody.Children.Add(subscriptionActions); subscriptionBody.Children.Add(subscriptionFeedback);
        profile.Children.Add(Card("ПОДПИСКА", null, subscriptionBody));
        var proxyMode = DialogChrome.MakeButton("", false);
        void UpdateProxyModeButton() => proxyMode.Content = owner.LocalProxyMode ? "Выключить локальный прокси" : "Включить локальный прокси";
        UpdateProxyModeButton();
        proxyMode.Click += async (_, _) =>
        {
            if (!SavedProfileSelected()) { saveFeedback.Text = "Сначала сохрани выбранный профиль."; return; }
            var enabled = !owner.LocalProxyMode;
            proxyMode.IsEnabled = false;
            try
            {
                await owner.SetLocalProxyModeAsync(enabled);
                if (!lifetime.IsCancellationRequested)
                    saveFeedback.Text = owner.LocalProxyMode != enabled
                        ? "Режим подключения не изменён. Проверь выбранный профиль и журнал."
                        : enabled ? "Локальный прокси включён." : "Восстановлена маршрутизация по правилам.";
            }
            finally
            {
                if (!lifetime.IsCancellationRequested)
                {
                    UpdateProxyModeButton();
                    proxyMode.IsEnabled = true;
                }
            }
        };
        profile.Children.Add(Card("ЛОКАЛЬНЫЙ ПРОКСИ",
            $"HTTP и SOCKS5 на 127.0.0.1:{CoreBackend.LocalProxyPort}. Укажи этот адрес в нужном приложении.", proxyMode));
        profile.Children.Add(DeviceIdentityCard(owner,dialog));
        var transferActions = new WrapPanel();
        var export = DialogChrome.MakeButton("Экспорт настроек", false);
        export.Margin = new Thickness(0, 0, 8, 4);
        var import = DialogChrome.MakeButton("Импорт настроек", false);
        import.Margin = new Thickness(0, 0, 0, 4);
        transferActions.Children.Add(export);
        transferActions.Children.Add(import);
        profile.Children.Add(Card("ПЕРЕНОС", null, transferActions));
        refreshSubscription.Click += async (_, _) =>
        {
            if (!SavedProfileSelected()) { subscriptionFeedback.Text = "Сначала сохрани выбранный профиль."; return; }
            if (!owner.HasSubscriptionSource) { subscriptionFeedback.Text = "Укажи ссылку подписки."; return; }
            refreshSubscription.IsEnabled = false; sourceButton.IsEnabled = false; save.IsEnabled = false; export.IsEnabled = false; import.IsEnabled = false;
            subscriptionFeedback.Text = "Обновляю подписку…";
            try
            {
                var updated = await owner.RefreshSubscriptionAsync(lifetime.Token);
                if (!lifetime.IsCancellationRequested) subscriptionFeedback.Text = updated ? "Подписка обновлена." : "Подписка не изменена.";
            }
            finally
            {
                if (!lifetime.IsCancellationRequested) { refreshSubscription.IsEnabled = true; sourceButton.IsEnabled = true; save.IsEnabled = true; export.IsEnabled = true; import.IsEnabled = true; }
            }
        };
        export.Click += async (_, _) =>
        {
            var picker = new SaveFileDialog { Filter = "Настройки MorphocyteOS (*.morphocyte)|*.morphocyte", DefaultExt = ".morphocyte",
                AddExtension = true, FileName = "MorphocyteOS-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".morphocyte" };
            if (picker.ShowDialog(dialog) != true) return;
            if (!string.Equals(Path.GetExtension(picker.FileName), ".morphocyte", StringComparison.OrdinalIgnoreCase))
            { saveFeedback.Text = "Выбери файл с расширением .morphocyte."; return; }
            export.IsEnabled = false; import.IsEnabled = false; save.IsEnabled = false; refreshSubscription.IsEnabled = false; sourceButton.IsEnabled = false;
            saveFeedback.Text = "Экспортирую…";
            try
            {
                var package = await owner.CaptureConfigurationAsync(CurrentSelection());
                await Task.Run(() => AtomicFile.Write(picker.FileName, ConfigurationTransfer.Serialize(package)), lifetime.Token);
                if (!lifetime.IsCancellationRequested) saveFeedback.Text = "Экспорт завершён.";
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!lifetime.IsCancellationRequested) saveFeedback.Text = "Не удалось экспортировать. Проверь доступ к файлу и профиль."; }
            finally { if (!lifetime.IsCancellationRequested) { export.IsEnabled = true; import.IsEnabled = true; save.IsEnabled = true; refreshSubscription.IsEnabled = true; sourceButton.IsEnabled = true; } }
        };
        import.Click += async (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Настройки MorphocyteOS (*.morphocyte)|*.morphocyte", CheckFileExists = true };
            if (picker.ShowDialog(dialog) != true) return;
            export.IsEnabled = false; import.IsEnabled = false; save.IsEnabled = false; refreshSubscription.IsEnabled = false; sourceButton.IsEnabled = false;
            try
            {
                var package = await Task.Run(() => ConfigurationTransfer.Read(picker.FileName), lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                pendingTransfer = package; importedProfile = null;
                selectedTheme = ThemeManager.Normalize(package.Options.Theme);
                selectedScale = package.Options.InterfaceScale;
                themePicker.SelectedItem = selectedTheme;
                SelectScale(package.Options.InterfaceScale);
                automatic.IsChecked = package.Options.AutoCheckUpdates;
                launchAtSignIn.IsChecked = package.Options.LaunchAtSignIn;
                autoConnect.IsChecked = package.Options.AutoConnectOnStartup;
                connectOnOpen.IsChecked = package.Options.AutoConnectOnOpen ?? owner.AutoConnectOnOpen;
                autoReconnect.IsChecked = package.Options.AutoReconnect;
                configValue.Text = "Профиль из файла переноса";
                configValue.ToolTip = null;
                saveFeedback.Text = "Импорт готов. Нажми «Сохранить».";
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!lifetime.IsCancellationRequested) saveFeedback.Text = "Не удалось импортировать. Проверь файл переноса."; }
            finally { if (!lifetime.IsCancellationRequested) { export.IsEnabled = true; import.IsEnabled = true; save.IsEnabled = true; refreshSubscription.IsEnabled = true; sourceButton.IsEnabled = true; } }
        };
        save.Click += async (_, _) =>
        {
            var selection = CurrentSelection();
            if (onSave is null)
            {
                result = selection;
                dialog.Close();
                return;
            }
            save.IsEnabled = false;
            export.IsEnabled = false; import.IsEnabled = false; refreshSubscription.IsEnabled = false; sourceButton.IsEnabled = false;
            saveFeedback.Text = "Сохраняю…";
            try
            {
                var applied = await onSave(selection);
                if (lifetime.IsCancellationRequested) return;
                if (applied is null)
                {
                    saveFeedback.Text = "Не удалось сохранить. Проверь журнал событий и попробуй ещё раз.";
                    return;
                }

                result = applied;
                committedTheme = applied.Theme;
                selectedConfig = applied.ConfigPath;
                selectedCore = applied.CorePath;
                SyncCorePicker();
                selectedTheme = applied.Theme;
                selectedScale = applied.InterfaceScale;
                importedProfile = null;
                pendingTransfer = null;
                themePicker.SelectedItem = selectedTheme;
                SelectScale(selectedScale);
                automatic.IsChecked = applied.AutoCheckUpdates;
                launchAtSignIn.IsChecked = applied.LaunchAtSignIn;
                autoConnect.IsChecked = applied.AutoConnectOnStartup;
                connectOnOpen.IsChecked = applied.AutoConnectOnOpen;
                autoReconnect.IsChecked = applied.AutoReconnect;
                UiScaleManager.Apply(dialog, (FrameworkElement)dialog.Content, selectedScale);
                UpdatePath(configValue, selectedConfig);
                UpdatePath(coreValue, selectedCore);
                UpdateProxyModeButton();
                saveFeedback.Text = "Сохранено. Можно продолжать работу с настройками.";
            }
            catch (Exception ex)
            {
                if (!lifetime.IsCancellationRequested) saveFeedback.Text = "Не удалось сохранить: " + ex.Message;
            }
            finally
            {
                if (!lifetime.IsCancellationRequested) { save.IsEnabled = true; export.IsEnabled = true; import.IsEnabled = true; refreshSubscription.IsEnabled = true; sourceButton.IsEnabled = true; }
            }
        };
        dialog.Content = DialogChrome.BuildFrame(dialog, "НАСТРОЙКИ", layout, footer);
        DialogChrome.ShowModal(owner, dialog);
        ThemeManager.Apply(committedTheme);
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
