using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

internal partial class Program
{
    private static async Task<int> RunImprovementChecks(string scratch, string fakeExe)
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        const string yaml = "# retained header\nmixed-port: 7890\nproxies:\n  - {name: Alpha, type: socks5, server: 192.0.2.20, port: 1080, password: fixture-secret}\n  - {name: Beta, type: socks5, server: 192.0.2.21, port: 1080}\nproxy-groups:\n  - {name: VPN, type: select, proxies: [Alpha, Beta]}\ntun:\n  enable: true\ndns:\n  enable: true\nrules:\n  - DOMAIN-SUFFIX,first.example,VPN\n  - DOMAIN-SUFFIX,second.example,DIRECT\n  - MATCH,DIRECT\n";
        const string fresh = "proxies:\n  - {name: Beta, type: socks5, server: 192.0.2.22, port: 1080}\n  - {name: Gamma, type: socks5, server: 192.0.2.23, port: 1080}\nproxy-groups:\n  - {name: VPN, type: select, proxies: [Beta, Gamma]}\nrules:\n  - MATCH,DIRECT\n";
        var path = Path.Combine(scratch, "improvements.yaml"); File.WriteAllText(path, yaml);
        var imported = SubscriptionImporter.ParseContent(fresh);
        var merged = SubscriptionRefresh.Merge(path, yaml, imported, "Beta");
        Check(merged.Added == 1 && merged.Removed == 1 && !merged.SelectionRemoved, "subscription merge adds and removes servers while retaining an available selection");
        var parsed = new YamlStream(); parsed.Load(new StringReader(merged.Yaml));
        var root = (YamlMappingNode)parsed.Documents[0].RootNode;
        var group = (YamlMappingNode)((YamlSequenceNode)root.Children[new YamlScalarNode("proxy-groups")]).Children[0];
        Check(((YamlScalarNode)((YamlSequenceNode)group.Children[new YamlScalarNode("proxies")]).Children[0]).Value == "Beta",
            "subscription refresh retains the chosen server as the group default");
        Check(merged.Yaml.StartsWith("# retained header\nmixed-port: 7890\n")
            && merged.Yaml.EndsWith(yaml[yaml.IndexOf("tun:", StringComparison.Ordinal)..]), "subscription refresh preserves non-server settings and routing source text");
        Check(SubscriptionRefresh.Merge(path, yaml, imported, "Alpha").SelectionRemoved, "removed selected server is detected before installation");
        try { SubscriptionRefresh.Merge(path, yaml.Replace("first.example,VPN", "first.example,Alpha"), imported, "Beta"); throw new Exception("Orphaned route accepted"); }
        catch (InvalidDataException) { Check(true, "subscription refresh refuses orphaned explicit server routes rather than dropping rules"); }
        var secretUrl = "https://subscription.example/account/private-token";
        var protectedSource = SubscriptionSource.Create(secretUrl);
        protectedSource.SelectedServer = "Beta";
        Check(protectedSource.ReadUrl() == secretUrl && !protectedSource.ProtectedUrl.Contains("private-token"), "subscription URL survives Windows protection without plaintext in local metadata");
        var settings = AppSettings.Load(Path.Combine(scratch, "improvements-settings.json"));
        settings.ConfigPath = path; settings.CorePath = fakeExe; settings.AutoCheckUpdates = false; settings.AutoReconnect = true;
        settings.ProfileSubscriptions[path] = protectedSource; settings.Save();
        Check(!File.ReadAllText(Path.Combine(scratch, "improvements-settings.json")).Contains("private-token")
            && AppSettings.Load(Path.Combine(scratch, "improvements-settings.json")).ProfileSubscriptions[path].ReadUrl() == secretUrl,
            "subscription source persists encrypted and can be loaded by the same Windows user");

        var window = new MainWindow(settings, Path.Combine(scratch, "improvements.log")) { ShowInTaskbar = false };
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Task Call(string name, params object[] values) => (Task)typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, values)!;
        ObservableCollection<DomainRule> Rules() => (ObservableCollection<DomainRule>)Field("_rules");
        var core = (CoreProcessManager)Field("_core");
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = (SemaphoreSlim)Field("_operations"); await gate.WaitAsync(); gate.Release();
            await window.ChangeRuleRouteAsync(Rules()[0], "REJECT");
            Check(((Button)Field("UndoButton")).IsEnabled && ((TextBlock)Field("ChangesSummaryText")).Text.Contains("Изменено: 1"),
                "rule changes enable history and show an accurate pending-change summary");
            await window.UndoRuleEditAsync();
            Check(Rules()[0].Route == "VPN" && settings.GetDraft(path) is null && !(bool)Field("_dirty") && File.ReadAllText(path) == yaml,
                "undo restores the clean editor state without writing YAML or retaining a phantom draft");
            await window.UndoRuleEditAsync(redo: true);
            Check(Rules()[0].Route == "REJECT" && settings.GetDraft(path)!.Rules[0].Route == "REJECT", "redo restores the edited route and its persisted draft");
            Rules()[0].Folder = "Работа"; Rules()[1].Folder = "Архив"; await Call("RecordEditAsync");
            var beforeOrder = ((List<string>)Field("_folderOrder")).ToArray();
            await Call("ReorderFolderGroupAsync", beforeOrder[0], beforeOrder[1], true);
            Check(!beforeOrder.SequenceEqual((List<string>)Field("_folderOrder")), "folder order mutation participates in editor history");
            await window.UndoRuleEditAsync();
            Check(beforeOrder.SequenceEqual((List<string>)Field("_folderOrder")), "undo restores folder order as well as rule contents");
            await window.UndoRuleEditAsync();
            Check(Rules().All(rule => rule.Folder.Length == 0), "undo restores folder assignments for several rules together");
            var second = Rules()[1]; Rules().Remove(second); await Call("RecordEditAsync");
            await window.UndoRuleEditAsync();
            Check(Rules().Count == 2 && Rules()[1].Value == "second.example", "deleting a rule can be undone after earlier edits");
            await window.ChangeRuleRouteAsync(Rules()[0], "DIRECT");
            Check(((ICollection)Field("_redoRules")).Count == 0, "editing after undo discards the obsolete redo branch");
            var undoCount = ((ICollection)Field("_undoRules")).Count;
            using var client = new HttpClient(new DiagnosticHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(fresh, Encoding.UTF8) }));
            Check(await window.RefreshSubscriptionAsync(default, client), "actual subscription update validates and commits an injected download without external requests");
            Check(settings.GetDraft(path) is { } draft && draft.SourceHash == ClashConfigDocument.Load(path).SourceHash && draft.Rules[0].Route == "DIRECT"
                && Rules()[0].Route == "DIRECT" && File.ReadAllText(path).Contains("first.example,VPN"),
                "subscription refresh rebinds draft hash while preserving unapplied routing and saved YAML rules");
            Check(((ICollection)Field("_undoRules")).Count == undoCount && settings.ProfileSubscriptions[path].UpdatedAt is not null,
                "subscription refresh retains editor history and records successful refresh time");
            var package = await window.CaptureConfigurationAsync(new SettingsDialogSelection(path, fakeExe, settings.Theme, 1, false, AutoReconnect: false));
            Check(package.SubscriptionUrl == secretUrl && !package.Options.AutoReconnect, "explicit portable export contains the refresh URL and chosen recovery preference");
            var cancelledText = File.ReadAllText(path);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Check(!await window.RefreshSubscriptionAsync(cancellation.Token, client) && File.ReadAllText(path) == cancelledText,
                "cancelled subscription refresh leaves the profile unchanged");
            await Call("ApplyChangesAsync", false);
            Check(((ICollection)Field("_undoRules")).Count == 0 && ((TextBlock)Field("ChangesSummaryText")).Visibility == Visibility.Collapsed,
                "applying rules starts a new clean history boundary and hides the pending summary");
            await window.RunConnectionDiagnosticAsync();
            Check(window.DiagnosticSteps.Single().Result.Contains("VPN выключен") && window.CanCopyDiagnostic,
                "diagnostics reports an explicitly stopped VPN without making network requests or consuming journal space");
            CaptureInteraction(window, Path.Combine(scratch, "improvements-main-window.png"));
            await Call("StartCoreAsync", false);
            Check(window.CanReconnect, "recovery becomes eligible only after VPN has been started");
            typeof(MainWindow).GetMethod("CancelConnectionIntent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Check(!window.CanReconnect, "manual stop intent prevents subsequent network signals from reconnecting VPN");
        }
        finally
        {
            await core.StopAsync();
            typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
        }

        var endpointChecked = false;
        using var api = new CoreDiagnostics(new DiagnosticHandler(request =>
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/version" => "{\"version\":\"fixture\"}",
                "/proxies/VPN" => "{\"now\":\"Alpha\"}",
                "/proxies/Alpha" => "{}",
                "/dns/query" => "{\"Status\":0,\"Answer\":[{\"type\":1,\"data\":\"192.0.2.40\"}]}",
                "/proxies/VPN/delay" => "{\"delay\":42}",
                _ => throw new Exception("Unexpected diagnostic endpoint")
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        File.WriteAllText(path, yaml);
        var results = await ConnectionDiagnostic.RunAsync(true, ClashConfigDocument.Load(path), api, default,
            connect: (host, port, token) => { endpointChecked = host == "192.0.2.20" && port == 1080; return Task.CompletedTask; });
        Check(endpointChecked && results.Count == 5 && results[^1].Result.Contains("42 мс"), "staged diagnostics checks the selected server, core DNS and HTTPS through VPN");
        var report = ConnectionDiagnostic.Report(results, true);
        Check(!report.Contains("fixture-secret") && !report.Contains(secretUrl) && !report.Contains("192.0.2.20") && !report.Contains("Alpha"),
            "copied diagnostic report excludes credentials, subscription URLs, server addresses and labels");
        using var disabledDns = new CoreDiagnostics(new DiagnosticHandler(_ => new(HttpStatusCode.InternalServerError)
            { Content = new StringContent("{\"message\":\"DNS section is disabled\"}") }));
        Check(!await disabledDns.CheckDnsAsync(default), "disabled core DNS is reported as skipped rather than a network failure");
        using var badDns = new CoreDiagnostics(new DiagnosticHandler(_ => new(HttpStatusCode.OK)
            { Content = new StringContent("{\"Status\":3}") }));
        try { await badDns.CheckDnsAsync(default); throw new Exception("NXDOMAIN accepted"); }
        catch (IOException) { Check(true, "diagnostic DNS check refuses NXDOMAIN or empty answers"); }
        var attempts = 0;
        Check(!await ConnectionRecovery.RunAsync(_ => { attempts++; return Task.FromResult(RecoveryAttemptResult.Retry); }, () => true,
            default, (_, _) => Task.CompletedTask) && attempts == 3, "automatic recovery stops after three failed attempts");
        attempts = 0;
        Check(await ConnectionRecovery.RunAsync(_ => { attempts++; return Task.FromResult(RecoveryAttemptResult.Connected); }, () => true,
            default) && attempts == 1, "healthy connection prevents unnecessary core restarts");
        attempts = 0;
        Check(!await ConnectionRecovery.RunAsync(_ => { attempts++; return Task.FromResult(RecoveryAttemptResult.Connected); }, () => false,
            default) && attempts == 0, "recovery cannot override a manually cleared connection request");
        attempts = 0;
        Check(!await ConnectionRecovery.RunAsync(_ => { attempts++; return Task.FromResult(RecoveryAttemptResult.Stop); }, () => true,
            default) && attempts == 1, "permanent recovery condition stops retries immediately");
        using var stop = new CancellationTokenSource();
        try
        {
            await ConnectionRecovery.RunAsync(_ => Task.FromResult(RecoveryAttemptResult.Retry), () => true, stop.Token,
                (_, _) => { stop.Cancel(); return Task.CompletedTask; });
            throw new Exception("Cancelled recovery accepted");
        }
        catch (OperationCanceledException) { Check(true, "manual cancellation interrupts recovery between attempts"); }
        return count + await RunImprovementEdgeChecks(scratch, fakeExe);
    }
}
