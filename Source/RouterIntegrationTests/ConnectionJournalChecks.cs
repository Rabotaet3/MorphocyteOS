using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunConnectionJournalChecks(string scratch, string fakeExe)
    {
        var root = Path.Combine(scratch, "connection-journal");
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "profile.yaml");
        File.WriteAllText(config, "proxies:\n  - {name: Test, type: socks5, server: 127.0.0.1, port: 9999}\nrules:\n  - MATCH,DIRECT\n");
        var settingsPath = Path.Combine(root, "settings.json");
        var settings = AppSettings.Load(settingsPath);
        settings.ConfigPath = config; settings.CorePath = fakeExe; settings.AutoCheckUpdates = false;
        settings.Theme = "Аврора"; settings.ShowEventLog = true;
        var window = new MainWindow(settings, Path.Combine(root, "test.log")) { ShowInTaskbar = false };
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        object? Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        async Task Idle()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = (SemaphoreSlim)Field("_operations"); await gate.WaitAsync(); gate.Release();
            window.UpdateLayout();
        }
        void Snapshot(IEnumerable<ConnectionActivity> rows)
        {
            typeof(MainWindow).GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, rows.ToArray());
            Invoke("FilterConnections");
        }
        async Task Toggle(bool value)
        {
            var toggle = (CheckBox)Field("CoreLogToggle"); toggle.IsChecked = value;
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
        }
        try
        {
            window.Show(); await Idle();
            ((DispatcherTimer)Field("_diagnosticTimer")).Stop();
            ((DispatcherTimer)Field("_logRefreshTimer")).Stop();
            var grid = (DataGrid)Field("ConnectionsGrid");
            var log = (TextBox)Field("LogText");
            var host = (ContentControl)Field("CoreLogHost");
            var surface = (Border)Field("CoreLogSurface");
            Check(!settings.ShowCoreLog && !host.IsVisible && ((RowDefinition)Field("CoreLogRow")).Height.Value == 0,
                "text log is initially off and the table receives all available height");
            var rows = Enumerable.Range(0, 180).Select(i => new ConnectionActivity($"app-{i:D3}.exe", $"host-{i:D3}.example:443",
                i % 2 == 0 ? "TCP" : "UDP", "domain_keyword=example => DIRECT", i % 2 == 0 ? "DIRECT" : "Test ← VPN", "c" + i)).ToArray();
            Snapshot(rows); await Idle();
            var view = (ListCollectionView)grid.ItemsSource;
            var header = Descendants<DataGridColumnHeader>(grid).Single(value => value.Column == grid.Columns[0]);
            typeof(DataGridColumnHeader).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(header, null);
            Check(view.SortDescriptions.Single().PropertyName == "Process" && grid.Columns[0].SortDirection == ListSortDirection.Ascending,
                "real header click sorts the stable connection view and displays an ascending glyph");
            view.SortDescriptions.Add(new SortDescription(nameof(ConnectionTableRow.Destination), ListSortDirection.Descending));
            var sort = view.SortDescriptions.ToArray();
            grid.Columns[0].Width = new DataGridLength(240);
            grid.Columns[1].Width = new DataGridLength(320);
            grid.Columns[4].DisplayIndex = 0;
            grid.SelectedItems.Add(view.GetItemAt(85)); grid.SelectedItems.Add(view.GetItemAt(86));
            var selection = grid.SelectedItems.Cast<object>().ToArray();
            var scroll = Descendants<ScrollViewer>(grid).First();
            scroll.ScrollToVerticalOffset(38 * 80 + 7); scroll.ScrollToHorizontalOffset(25);
            await Idle();
            var before = scroll.VerticalOffset;
            var horizontal = scroll.HorizontalOffset;
            var anchor = view.GetItemAt((int)(before / 38));
            var updated = rows.Where(row => row.Id != "c160").Select(row => row.Id == "c85" ? row with { Rule = "ProcessName app-085.exe", Upload = 42 } : row)
                .Prepend(new ConnectionActivity("aaa-new.exe", "new.example:443", "TCP", "Match", "DIRECT", "new")).ToArray();
            Snapshot(updated); await Idle();
            Check(ReferenceEquals(view, grid.ItemsSource) && view.SortDescriptions.SequenceEqual(sort)
                && ((ConnectionTableRow)view.GetItemAt(0)).Process == "aaa-new.exe",
                "new connections preserve both sort keys and enter the correct sorted position");
            Check(grid.Columns[0].Width.Value == 240 && grid.Columns[4].DisplayIndex == 0,
                "snapshot refresh preserves user column widths and column order");
            Check(selection.All(item => grid.SelectedItems.Contains(item)) && grid.SelectedItems.Count == 2,
                "snapshot refresh preserves multiple selected connections, including an updated row");
            Check(Math.Abs(scroll.VerticalOffset - (view.IndexOf(anchor) * 38 + before % 38)) < 1
                && Math.Abs(scroll.HorizontalOffset - horizontal) < 1,
                "inserting a sorted row preserves the visible anchor and horizontal reading position");
            var changed = (ConnectionTableRow)selection[0];
            Check(changed.RuleLabel == "Приложение: app-085.exe" && changed.RuleHint.Contains("ProcessName"),
                "rule updates use a readable label and retain the exact technical rule in the tooltip");
            var search = (TextBox)Field("ConnectionSearchBox");
            search.Text = "new.example"; await Idle();
            Snapshot(updated.Append(new("other.exe", "new.example:80", "TCP", "Match", "DIRECT", "new2"))); await Idle();
            Check(view.Count == 2 && search.Text == "new.example" && view.SortDescriptions.SequenceEqual(sort),
                "the search filter and sorting both survive newly arriving matching connections");
            search.Text = "ProcessName"; await Idle();
            Check(view.Count == 1, "technical rule text remains searchable even when its friendly label is shown");
            search.Clear();
            var anonymous = new ConnectionActivity("same.exe", "same.example:443", "TCP", "Match", "DIRECT");
            Snapshot(new[] { anonymous, anonymous });
            var first = view.GetItemAt(0);
            Snapshot(new[] { anonymous, anonymous });
            Check(view.Count == 2 && ReferenceEquals(first, view.GetItemAt(0)),
                "connections without controller IDs retain distinct stable identities across snapshots");
            Snapshot(updated); await Idle();
            foreach (var pair in new[] {
                ("DomainSuffix example.org", "Домен и поддомены: example.org"),
                ("domain_keyword=vk => DIRECT", "Слово в домене: vk"),
                ("process_path_regex=(?i)example => DIRECT", "Приложение: sample.exe"),
                ("Match", "По умолчанию"), ("final", "По умолчанию"), ("", "Правило не указано"),
                ("NewUnrecognizedRule abc", "Другое правило") })
                Check(ConnectionRuleLabel.Format(pair.Item1, "sample.exe") == pair.Item2, "friendly rule: " + pair.Item1);

            foreach (var offset in new[] { 0d, scroll.ScrollableHeight / 2, scroll.ScrollableHeight })
            {
                scroll.ScrollToVerticalOffset(offset); await Idle();
                var bar = Descendants<ScrollBar>(grid).Single(item => item.Orientation == Orientation.Vertical && item.IsVisible);
                var track = (Track)bar.Template.FindName("PART_Track", bar);
                var bounds = track.Thumb.TransformToAncestor(bar).TransformBounds(new Rect(track.Thumb.RenderSize));
                Check(bounds.Height >= 27 && bounds.Top >= -.5 && bounds.Bottom <= bar.ActualHeight + .5,
                    "vertical scrollbar thumb stays usable and unclipped at offset " + offset.ToString("0"));
            }
            var hbar = Descendants<ScrollBar>(grid).Single(item => item.Orientation == Orientation.Horizontal && item.IsVisible);
            var htrack = (Track)hbar.Template.FindName("PART_Track", hbar);
            Check(htrack.Thumb.ActualHeight <= hbar.ActualHeight && htrack.Thumb.ActualWidth >= 27,
                "horizontal scrollbar has no oversized vertical thumb protruding from its track");
            grid.Columns[0].Width = new DataGridLength(130);
            grid.Columns[1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            grid.Columns[4].DisplayIndex = 4;
            scroll.ScrollToTop(); scroll.ScrollToLeftEnd(); await Idle();
            CaptureInteraction(window, Path.Combine(root, "journal-table-only.png"));

            await Toggle(true);
            Check(settings.ShowCoreLog && AppSettings.Load(settingsPath).ShowCoreLog && host.IsVisible,
                "the TUN-style text-log switch shows the pane and persists its independent preference");
            Invoke("AppendLogEntries", (object)Enumerable.Range(0, 140).Select(i => "Journal test line " + i).ToArray());
            await Idle();
            Check(log.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden && !Descendants<ScrollBar>(log).Any(bar => bar.IsVisible),
                "text log has no visible right-hand scrollbar");
            log.ScrollToVerticalOffset(120); await Idle();
            var reading = log.VerticalOffset;
            log.LineUp(); await Idle();
            Check(log.VerticalOffset < reading, "text log still scrolls without a visible scrollbar");
            log.Select(log.Text.IndexOf("Journal test line 20", StringComparison.Ordinal), 19);
            var selectedText = log.SelectedText;
            reading = log.VerticalOffset;
            Invoke("AppendLogEntries", (object)new[] { "Later test line" }); await Idle();
            Check(log.SelectedText == selectedText && Math.Abs(log.VerticalOffset - reading) < 1,
                "new log lines preserve selected text and the user's reading position");
            CaptureInteraction(window, Path.Combine(root, "journal-inline.png"));
            foreach (var name in new[] { "CoreLogTopGrip", "CoreLogBottomGrip", "CoreLogLeftGrip", "CoreLogRightGrip" })
            {
                var grip = (Thumb)Field(name);
                grip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                grip.RaiseEvent(new DragDeltaEventArgs(1, 1) { RoutedEvent = Thumb.DragDeltaEvent });
                Check((double)Field("_coreLogDragX") == 1 && (double)Field("_coreLogDragY") == 1,
                    "drag gesture is connected to edge: " + name);
                var floating = (Window)Invoke("DetachCoreLog")!;
                await Idle();
                Check(floating.IsVisible && !host.IsVisible && ReferenceEquals(log, Field("LogText"))
                    && log.SelectedText == selectedText && ((RowDefinition)Field("CoreLogRow")).Height.Value == 0,
                    "detaching moves the same log and selection to a non-modal window and releases table space: " + name);
                if (name == "CoreLogTopGrip")
                {
                    Invoke("SelectMainPage", false);
                    Invoke("AppendLogEntries", (object)new[] { "Floating log live update" });
                    Check(log.Text.Contains("Floating log live update") && ((FrameworkElement)Field("Workspace")).IsEnabled,
                        "detached log receives live lines while routes remain usable in the main window");
                    ThemeManager.Apply("Светлая морфоцитная"); floating.UpdateLayout();
                    Check(Equals(log.Foreground, ThemeManager.GetBrush("ThemeLogText")), "detached log follows theme changes");
                    window.ApplyInterfaceScale(1.25); await Idle();
                    Check(Math.Abs(((FrameworkElement)floating.Content).LayoutTransform.Value.M11 - 1.25) < .01,
                        "detached log follows interface scale changes");
                    CaptureInteraction(floating, Path.Combine(root, "journal-detached-light.png"));
                    window.ApplyInterfaceScale(1); ThemeManager.Apply("Аврора");
                    Invoke("SelectMainPage", true);
                    CaptureInteraction(floating, Path.Combine(root, "journal-detached.png"));
                }
                floating.Close(); await Idle();
                Check(host.IsVisible && ReferenceEquals(host.Content, surface) && log.SelectedText == selectedText,
                    "closing the detached window returns its log without losing selection: " + name);
            }
            var lastWindow = (Window)Invoke("DetachCoreLog")!;
            await Toggle(false);
            Check(!lastWindow.IsVisible && !host.IsVisible && !settings.ShowCoreLog && !AppSettings.Load(settingsPath).ShowCoreLog,
                "turning the switch off also closes the floating log without reopening its embedded pane");
            await Toggle(true);
            Check(log.Text.Contains("Floating log live update"), "hiding a log never deletes collected history");
            await Toggle(false);
            Check(!((CoreProcessManager)Field("_core")).HasTrackedProcess, "journal tests never start a real VPN or change its routing");
            Console.WriteLine("JOURNAL PREVIEWS: " + root);
            return count;
        }
        finally
        {
            typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close(); UiScaleManager.CurrentScale = 1; ThemeManager.Apply(null);
        }
    }
}
