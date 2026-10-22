using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

internal partial class Program
{
    private static async Task<int> RunConnectionRouteChecks(string scratch, string fakeExe)
    {
        var root = Path.Combine(scratch, "connection-routes"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "profile.yaml");
        var initial = "# Keep unrelated configuration intact\nproxies:\n  - {name: Test, type: socks5, server: 127.0.0.1, port: 9999}\n" +
            "proxy-groups:\n  - {name: VPN, type: select, proxies: [Test]}\n" +
            "rules:\n  - PROCESS-NAME,comet.exe,DIRECT\n  - DOMAIN-KEYWORD,perplexity,DIRECT\n  - DOMAIN,old.example,DIRECT\n  - DOMAIN,ordinary.example,VPN\n  - MATCH,DIRECT\n";
        File.WriteAllText(path, initial);
        var settingsPath = Path.Combine(root, "settings.json");
        var settings = AppSettings.Load(settingsPath);
        settings.ConfigPath = path; settings.CorePath = fakeExe; settings.AutoCheckUpdates = false;
        settings.Theme = "Аврора";
        var window = new MainWindow(settings, Path.Combine(root, "test.log")) { ShowInTaskbar = false };
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        object? Invoke(string name, params object[] values) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, values);
        var count = 0;
        var expectedNotice = false;
        var noticeCount = 0;
        Exception? unexpectedNotice = null;
        var noticeGuard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        noticeGuard.Tick += (_, _) =>
        {
            foreach (var notice in window.OwnedWindows.OfType<Window>().Where(value => value.Title == "MorphocyteOS" && value.IsVisible).ToArray())
            {
                noticeCount++;
                if (!expectedNotice) unexpectedNotice = new Exception("Unexpected route fixture notice: " + ((TextBlock)Field("LastIssueText")).Text);
                notice.Close();
            }
        };
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        async Task Idle()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = (SemaphoreSlim)Field("_operations"); await gate.WaitAsync(); gate.Release(); window.UpdateLayout();
        }
        void Snapshot(params ConnectionActivity[] rows)
        {
            typeof(MainWindow).GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, rows);
            Invoke("FilterConnections");
        }
        static string[] NativeRules(string yaml)
        {
            var stream = new YamlStream(); stream.Load(new StringReader(yaml));
            return ((YamlSequenceNode)((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("rules")])
                .Children.Select(node => ((YamlScalarNode)node).Value!).ToArray();
        }
        try
        {
            window.Show(); await Idle();
            noticeGuard.Start();
            ((DispatcherTimer)Field("_diagnosticTimer")).Stop(); ((DispatcherTimer)Field("_logRefreshTimer")).Stop();
            Invoke("SelectMainPage", true);
            var rules = (ObservableCollection<DomainRule>)Field("_rules");
            var grid = (DataGrid)Field("ConnectionsGrid");
            foreach (var (input, expected) in new[] {
                ("www.Perplexity.ai:443", "www.perplexity.ai"), ("accounts.google.com:443", "accounts.google.com"),
                ("пример.рф:443", "xn--e1afmkfd.xn--p1ai"), ("www.example.com.:80", "www.example.com"), ("old.example", "old.example") })
                Check(ConnectionAddress.Domain(input) == expected, "connection address remains an exact normalized domain: " + input);
            foreach (var input in new[] { "", "127.0.0.1:443", "[::1]:443", "2001:db8::1:443", "1.2.3.4", "127.1:443", "host.example:0",
                "host.example:65536", "host.example:abc", "https://example.com:443", "evil@example.com:443", "a.example/path:443", "a.example?x:443", "a.example\n:443" })
                Check(ConnectionAddress.Domain(input) is null, "unsafe or IP-only connection cannot create a broader rule: " + input.Replace('\n', ' '));
            var first = new ConnectionActivity("comet.exe", "www.perplexity.ai:443", "TCP", "ProcessName comet.exe", "DIRECT", "first");
            Snapshot(first, first with { Id = "sibling", Destination = "api.perplexity.ai:443" },
                first with { Id = "subdomain", Destination = "sub.www.perplexity.ai:443" },
                first with { Id = "ip", Destination = "192.0.2.1:443" });
            await Idle();
            var rows = (List<ConnectionTableRow>)Field("_connectionRows");
            var row = rows.Single(value => value.Destination == first.Destination);
            Check(grid.Columns[4] is DataGridTemplateColumn { SortMemberPath: "Route" }, "editable route column retains actual-route sorting");
            var routeButton = Descendants<Button>(grid).First(value => ReferenceEquals(value.Tag, row));
            Check(Descendants<Button>(grid).Single(value => value.Tag is ConnectionTableRow { Destination: "192.0.2.1:443" }) is { IsEnabled: false },
                "IP-only rows visibly disable address routing instead of creating misleading domain rules");
            routeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            var menu = routeButton.ContextMenu!;
            var menuClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            menu.Closed += (_, _) => menuClosed.TrySetResult();
            var options = menu.Items.OfType<MenuItem>().ToArray();
            Check(options.Length == 4 && options[0].Header is StackPanel title
                && title.Children.OfType<TextBlock>().Any(value => value.Text == "www.perplexity.ai")
                && title.Children.OfType<TextBlock>().Any(value => value.Text.Contains("без поддоменов")), "route menu explicitly identifies the exact domain scope");
            Check(options.Skip(1).Select(value => value.Header.ToString()).SequenceEqual(new[] { "VPN", "Напрямую", "Блокировать" }),
                "connection route menu offers VPN, direct and block");
            Check(options[2].IsChecked && !options[1].IsChecked, "menu initially marks the actual DIRECT connection");
            menu.BeginAnimation(UIElement.OpacityProperty, null); menu.Opacity = 1; // Freeze fade-in for a deterministic preview.
            var checkMark = Descendants<System.Windows.Shapes.Path>(options[2]).Single(value => value.Name == "Check");
            var optionLabel = Descendants<ContentPresenter>(options[2]).Single(value => ReferenceEquals(value.TemplatedParent, options[2]));
            var checkY = checkMark.TranslatePoint(new Point(checkMark.ActualWidth / 2, checkMark.ActualHeight / 2), options[2]).Y;
            var labelY = optionLabel.TranslatePoint(new Point(optionLabel.ActualWidth / 2, optionLabel.ActualHeight / 2), options[2]).Y;
            Check(checkMark.Opacity == 1 && Math.Abs(checkY - labelY) <= 1,
                "selected route uses a vector checkmark vertically centered with the label, independent of font glyph baseline");
            CaptureInteraction(menu, Path.Combine(root, "journal-route-menu.png"));
            var menuPosition = menu.PointToScreen(new Point());
            var initialTableCount = grid.Items.Count;
            var deferredSnapshot = new[] { first with { Network = "UDP" }, first with { Id = "sibling", Destination = "api.perplexity.ai:443" },
                first with { Id = "subdomain", Destination = "sub.www.perplexity.ai:443" },
                first with { Id = "ip", Destination = "192.0.2.1:443" }, first with { Id = "arrival", Destination = "arrival.example:443" } };
            Snapshot(deferredSnapshot); await Idle();
            Check(menu.IsOpen && (menu.PointToScreen(new Point()) - menuPosition).Length < 2,
                "open route menu does not jump to screen origin when a new snapshot arrives");
            Check(grid.Items.Count == initialTableCount && row.Network == "TCP" && ReferenceEquals(menu.PlacementTarget, grid),
                "open route menu keeps its row stable and uses the non-recycled grid as its placement target");
            deferredSnapshot[0] = first with { Network = "UDP", Upload = 77 };
            Snapshot(deferredSnapshot); await Idle();
            options[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
            await menuClosed.Task.WaitAsync(TimeSpan.FromSeconds(3)); await Idle();
            Check(grid.Items.Count == deferredSnapshot.Length && row.Network == "UDP" && row.Route == "DIRECT",
                "closing the menu paints the latest deferred snapshot, without losing route-click context");
            Check(rules.Count == 5 && rules[0] is { Kind: "DOMAIN", Value: "www.perplexity.ai", Route: "VPN", AddressOverride: true },
                "real route-menu click creates one highest-priority exact-domain VPN draft");
            Check(rules.Single(value => value.Kind == "PROCESS-NAME").Route == "DIRECT"
                && rules.Single(value => value.Kind == "DOMAIN-KEYWORD").Route == "DIRECT", "address choice leaves browser and keyword routes unchanged");
            Check(File.ReadAllText(path) == initial && !((CoreProcessManager)Field("_core")).HasTrackedProcess,
                "choosing a journal route neither writes applied YAML nor starts/restarts any VPN");
            Check(row.Route == "DIRECT" && row.PendingRouteLabel == "Черновик: VPN"
                && rows.Where(value => value != row).All(value => value.PendingRouteLabel.Length == 0),
                "actual route stays honest; only the exact domain displays a separate pending preview");
            Check(((Border)Field("JournalDraftPanel")).IsVisible && ((Button)Field("JournalApplyButton")).IsEnabled
                && ((Button)Field("JournalUndoButton")).IsEnabled, "journal provides visible explicit Apply and Undo for the saved draft");
            Check(AppSettings.Load(settingsPath).GetDraft(path)!.Rules.Single(value => value.Value == "www.perplexity.ai").AddressOverride,
                "address priority persists to the saved per-profile draft");
            var savedDraft = settings.GetDraft(path);
            var savedUndoCount = ((System.Collections.ICollection)Field("_undoRules")).Count;
            expectedNotice = true;
            using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await window.ChangeConnectionRouteAsync("save-failure.example", "DIRECT");
            expectedNotice = false;
            Check(noticeCount == 1 && !rules.Any(value => value.Value == "save-failure.example") && ReferenceEquals(settings.GetDraft(path), savedDraft)
                && ((System.Collections.ICollection)Field("_undoRules")).Count == savedUndoCount && (bool)Field("_draftSaved"),
                "failed draft save rolls back new rule, settings, saved state and undo history, with an honest notice");
            var document = ClashConfigDocument.Load(path);
            var candidate = document.BuildText(rules);
            var parsed = ClashConfigDocument.Parse(path, candidate);
            Check(NativeRules(candidate)[0] == "DOMAIN,www.perplexity.ai,VPN" && parsed.Rules[0].AddressOverride,
                "priority marker is a YAML comment, not part of the native rule, and survives reloading");
            Check(candidate.StartsWith("# Keep unrelated configuration intact\nproxies:"), "address rules preserve unrelated YAML source text");
            Check(parsed.BuildText(parsed.Rules) == candidate, "marked address rules round-trip without duplication or reordering");
            var crlf = ClashConfigDocument.Parse(path, candidate.Replace("\n", "\r\n"));
            Check(crlf.Rules[0].AddressOverride, "priority marker loads correctly from Windows CRLF YAML");
            var tunnelRules = NativeRules(parsed.BuildRuntimeText(35462, new string('A', 64), true));
            Check(tunnelRules[0] == "DOMAIN,www.perplexity.ai,VPN" && tunnelRules.Contains("PROCESS-NAME,comet.exe,DIRECT")
                && !tunnelRules.Contains("DOMAIN,ordinary.example,VPN") && tunnelRules[^1] == "MATCH,VPN",
                "full TUN preserves explicit address VPN priority over app DIRECT without changing ordinary-rule filtering");
            using (var json = JsonDocument.Parse(SingBoxConfig.Build(parsed, 35462, new string('A', 64), true)))
            {
                var native = json.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().ToArray();
                var domainIndex = Array.FindIndex(native, value => value.TryGetProperty("domain", out var domains) && domains.ToString().Contains("www.perplexity.ai"));
                var processIndex = Array.FindIndex(native, value => value.TryGetProperty("process_path_regex", out _));
                Check(domainIndex >= 0 && domainIndex < processIndex && native[domainIndex].GetProperty("outbound").GetString() == "VPN",
                    "sing-box keeps exact address routing ahead of browser routing in TUN");
            }
            await window.ChangeConnectionRouteAsync("www.perplexity.ai", "DIRECT");
            Check(rules.Count(value => value.Value == "www.perplexity.ai") == 1 && row.PendingRouteLabel == "Черновик: Напрямую",
                "changing the same address updates its single draft, not a duplicate rule");
            await window.ChangeConnectionRouteAsync("www.perplexity.ai", "REJECT");
            Check(row.PendingRouteLabel == "Черновик: Блокировать", "blocking is also staged rather than applied immediately");
            Snapshot(first with { Upload = 123 }, first with { Id = "new", Destination = "new.example:443" }); await Idle();
            Check(ReferenceEquals(row, rows.Single(value => value.Destination == first.Destination)) && row.PendingRouteLabel.EndsWith("Блокировать"),
                "connection snapshot refresh preserves both draft preview and stable row identity");
            ((Button)Field("JournalUndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Check(row.PendingRouteLabel.EndsWith("Напрямую"), "journal Undo reverses the most recent route choice");
            await window.UndoRuleEditAsync(); await window.UndoRuleEditAsync();
            Check(!rules.Any(value => value.AddressOverride) && !((Border)Field("JournalDraftPanel")).IsVisible && row.PendingRouteLabel == "",
                "undoing all journal changes removes the exception and pending banner");
            await window.UndoRuleEditAsync(redo: true);
            Check(rules[0].AddressOverride && row.PendingRouteLabel.EndsWith("VPN"), "redo restores exact address priority and preview");
            var old = rules.Single(value => value.Value == "old.example"); old.Enabled = false;
            rules[0].Folder = "Адреса";
            ((ListView)Field("RulesList")).SelectedItems.Add(rules[0]);
            await window.ChangeConnectionRouteAsync("old.example:443", "VPN");
            Check(rules.Count(value => value.Value == "old.example") == 1 && old.Enabled && old.AddressOverride && old.Route == "VPN"
                && rules.Single(value => value.Value == "www.perplexity.ai").Folder == "Адреса",
                "existing disabled exact-domain rule is reused; unrelated selected rules and folders are untouched");
            var beforeInvalid = rules.Count;
            await window.ChangeConnectionRouteAsync("192.0.2.1:443", "DIRECT");
            Check(rules.Count == beforeInvalid && !rules.Any(value => value.Value == "192.0.2.1"), "IP-only route change is safely ignored without inventing a domain");
            candidate = document.BuildText(rules); parsed = ClashConfigDocument.Parse(path, candidate);
            var package = ConfigurationTransfer.Capture(path, initial, rules, new[] { "Адреса" }, new TransferOptions(), true);
            var imported = ConfigurationTransfer.Deserialize(Encoding.UTF8.GetBytes(ConfigurationTransfer.Serialize(package)));
            Check(imported.Rules.Count(value => value.AddressOverride) == 2 && imported.Rules.Select(value => value.ToRule()).First().AddressOverride,
                "configuration transfer preserves exact-address draft metadata");
            var providerYaml = candidate + "rule-providers:\n  local:\n    type: file\n    behavior: domain\n    path: local.txt\n";
            File.WriteAllText(Path.Combine(root, "local.txt"), "payload:\n  - '+.local.example'\n");
            var providerDoc = ClashConfigDocument.Parse(path, providerYaml);
            var providerPackage = ConfigurationTransfer.Capture(path, providerYaml, providerDoc.Rules, Array.Empty<string>(), new TransferOptions(), false);
            Check(ClashConfigDocument.Parse(path, providerPackage.Yaml).Rules.Count(value => value.AddressOverride) == 2,
                "provider-resource export preserves applied address priorities despite YAML reserialization");
            var disabled = rules.Select(value => value.Copy()).ToArray();
            foreach (var value in disabled.Where(value => value.AddressOverride)) value.Enabled = false;
            Check(!document.BuildText(disabled).Contains("morphocyte-address-route"), "disabled address overrides are not emitted into runtime rules");
            var invalid = new DomainRule { Kind = "DOMAIN-SUFFIX", Value = "wide.example", Route = "DIRECT", AddressOverride = true };
            try { document.BuildText(new[] { invalid }); Check(false, "non-exact priority must be rejected"); }
            catch (InvalidDataException) { Check(true, "address override cannot silently expand into a suffix rule"); }
            var nativePath = Path.Combine(root, "native-check.yaml"); File.WriteAllText(nativePath, candidate);
            foreach (var engine in new[] { "mihomo.exe", "sing-box.exe" })
                foreach (var tun in new[] { false, true })
                {
                    var result = await CoreProcessManager.ValidateAsync(Path.Combine(AppContext.BaseDirectory, engine), nativePath, fullTunnel: tun);
                    Check(result.Success, $"native {engine} accepts address overrides without starting VPN; TUN={tun}: {result.Message}");
                }
            Snapshot(first with { Route = "NL-MORPHOCYTE-LONG-SERVER-NAME ← VPN" }, first with { Id = "second", Destination = "new.example:443" }); await Idle();
            window.Width = window.MinWidth; window.Height = window.MinHeight; await Idle();
            ((TextBlock)Field("LastIssueText")).Text = ""; // Hide the intentional save-failure fixture notice in UI previews.
            routeButton = Descendants<Button>(grid).First(value => ReferenceEquals(value.Tag, row));
            Check(routeButton.ActualHeight <= grid.RowHeight && routeButton.ActualWidth <= grid.Columns[4].ActualWidth,
                "two-line pending route fits the fixed table row even at the minimum window size");
            CaptureInteraction(window, Path.Combine(root, "journal-route-draft.png"));
            ThemeManager.Apply("Светлая морфоцитная"); await Idle();
            CaptureInteraction(window, Path.Combine(root, "journal-route-draft-light.png")); ThemeManager.Apply("Аврора");
            ((Button)Field("JournalApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Check(ClashConfigDocument.Load(path).Rules.Count(value => value.AddressOverride) == 2
                && !((Border)Field("JournalDraftPanel")).IsVisible && row.PendingRouteLabel.Length == 0,
                "explicit journal Apply validates and commits all pending rules, then clears draft previews");
            Check(!((CoreProcessManager)Field("_core")).HasTrackedProcess && settings.GetDraft(path) is null,
                "applying rules while disconnected never starts a VPN and clears only the fixture draft");
            var appliedText = File.ReadAllText(path);
            foreach (var closeReason in new[] { "cancel", "hide journal", "virtualized row" })
            {
                Snapshot(new[] { first }.Concat(Enumerable.Range(0, 80).Select(index => first with {
                    Id = "filler" + index, Destination = "filler-" + index + ".example:443" })).ToArray());
                ((ListCollectionView)grid.ItemsSource).MoveCurrentToFirst();
                Descendants<ScrollViewer>(grid).First().ScrollToTop(); await Idle();
                var liveRow = rows.Single(value => value.Destination == first.Destination);
                var liveButton = Descendants<Button>(grid).First(value => ReferenceEquals(value.Tag, liveRow));
                liveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                var openMenu = liveButton.ContextMenu!;
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                openMenu.Closed += (_, _) => closed.TrySetResult();
                Snapshot(first with { Destination = "newest.example:443" }); await Idle();
                Check(openMenu.IsOpen && liveRow.Destination == first.Destination,
                    "incoming removal/host changes cannot retarget the open address menu: " + closeReason);
                if (closeReason == "cancel") openMenu.IsOpen = false;
                else if (closeReason == "hide journal") Invoke("SelectMainPage", false);
                else Descendants<ScrollViewer>(grid).First().ScrollToBottom();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(3)); await Idle();
                Check(Field("_connectionRouteMenu") is null && !(bool)Field("_connectionTableRefreshPending")
                    && rows.Count == 1 && rows[0].Destination == "newest.example:443",
                    "closing the address menu resumes the table with its newest snapshot: " + closeReason);
                Invoke("SelectMainPage", true); await Idle();
            }
            Check(File.ReadAllText(path) == appliedText && settings.GetDraft(path) is null,
                "canceling, hiding or scrolling away a route menu never edits routing rules");
            Check(unexpectedNotice is null, unexpectedNotice?.Message ?? "route UI completes without unexpected error dialogs");
            Console.WriteLine("CONNECTION ROUTE PREVIEW: " + root);
            return count;
        }
        finally
        {
            noticeGuard.Stop();
            typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close(); UiScaleManager.CurrentScale = 1; ThemeManager.Apply(null);
        }
    }
}
