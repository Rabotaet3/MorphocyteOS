using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MorphocyteRouter;

internal static class IncrementalUpdateChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string scratch)
    {
        var root = Path.Combine(scratch, "incremental");
        Directory.CreateDirectory(root);
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
        var old = new Dictionary<string, byte[]>
        {
            ["MorphocyteOS.exe"] = Bytes("old app"), ["mihomo.exe"] = Bytes("unchanged core"),
            ["release-source.json"] = Bytes("{\"githubRepository\":\"sample-org/sample-app\"}"),
            ["Licenses/example.txt"] = Bytes("unchanged license"), ["README.md"] = Bytes("unchanged readme")
        };
        var next = new Dictionary<string, byte[]>(old) { ["MorphocyteOS.exe"] = Bytes("updated app") };
        ReleaseManifest Manifest(string version, Dictionary<string, byte[]> files) => new()
        {
            Version = version, Files = files.Select(pair => new ReleaseFile { Path = pair.Key, Size = pair.Value.Length, Sha256 = Hash(pair.Value) }).ToList()
        };
        string Install(string name, string executableName = "MorphocyteOS.exe")
        {
            var directory = Path.Combine(root, name);
            foreach (var (relative, bytes) in old)
            {
                var path = Path.Combine(directory, relative == "MorphocyteOS.exe" ? executableName : relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
            }
            File.WriteAllBytes(Path.Combine(directory, UpdatePackage.ManifestName), JsonSerializer.SerializeToUtf8Bytes(Manifest("1.0.0", old)));
            return directory;
        }
        byte[] Package(IEnumerable<string> paths, string? extra = null, bool duplicate = false, bool tamper = false)
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                foreach (var path in paths)
                {
                    using (var stream = zip.CreateEntry(path).Open()) stream.Write(tamper ? Bytes("tampered app") : next[path]);
                    if (duplicate) { using var stream = zip.CreateEntry(path).Open(); stream.Write(next[path]); }
                }
                if (extra is not null) { using var stream = zip.CreateEntry(extra).Open(); stream.Write(Bytes("unexpected")); }
            }
            return memory.ToArray();
        }
        var content = new Dictionary<string, byte[]>();
        UpdateComponent Component(string[] files, byte[]? replacement = null)
        {
            var bytes = replacement ?? Package(files);
            var sha = Hash(bytes); var name = $"MorphocyteOS-part-{sha}.zip";
            content[name] = bytes;
            return new UpdateComponent { Name = name, Size = bytes.Length, Sha256 = sha, Files = files.ToList() };
        }
        UpdateIndex Index() => new()
        {
            SchemaVersion = 1, Manifest = Manifest("1.1.0", next), Packages = new()
            {
                Component(new[] { "MorphocyteOS.exe" }), Component(new[] { "mihomo.exe" }),
                Component(next.Keys.Where(path => path is not "MorphocyteOS.exe" and not "mihomo.exe").ToArray())
            }
        };
        UpdateCheckResult Release(UpdateIndex index, bool wrongDigest = false)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(index); var name = "MorphocyteOS-1.1.0-update.json";
            content[name] = bytes;
            return new(true, true, "new", LatestVersion: "1.1.0", Repository: "sample-org/sample-app",
                Asset: new("MorphocyteOS-1.1.0-win-x64.zip", "https://github.com/sample-org/sample-app/releases/download/v1.1.0/MorphocyteOS-1.1.0-win-x64.zip", new string('a', 64), 100))
            {
                IndexAsset = new(name, "https://github.com/sample-org/sample-app/releases/download/v1.1.0/" + name, wrongDigest ? new string('0', 64) : Hash(bytes), bytes.Length)
            };
        }
        var requests = new List<string>();
        using var client = new HttpClient(new Handler((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            var name = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]); requests.Add(name);
            if (!content.TryGetValue(name, out var bytes)) throw new IOException("Unexpected full-archive request: " + name);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }));
        async Task Reject(Func<Task> action, string message)
        {
            try { await action(); }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException) { check(true, message); return; }
            throw new Exception("Accepted unsafe incremental update: " + message);
        }
        void RejectIndex(UpdateIndex index, string message)
        {
            try { IncrementalUpdate.ParseIndex(JsonSerializer.SerializeToUtf8Bytes(index), "1.1.0"); }
            catch (Exception error) when (error is IOException or InvalidDataException) { check(true, message); return; }
            throw new Exception("Accepted unsafe index: " + message);
        }
        UpdatePlan Plan(string installed, string exe = "MorphocyteOS.exe") => new()
        {
            InstallDirectory = installed, ExecutableName = exe, Version = "1.1.0", ParentId = Environment.ProcessId,
            ParentStartTicks = DateTime.UtcNow.Ticks, OriginalExecutableHash = UpdatePackage.Hash(Path.Combine(installed, exe))
        };
        var cache = Path.Combine(root, ".update-cache");
        var installed = Install("installed", "MorphocyteOS-test.exe");
        File.WriteAllText(Path.Combine(installed, "private.yaml"), "private profile");
        var index = Index();
        var prepared = await UpdateDownloader.PrepareAsync(client, Release(index), cache, null, default, installed, "MorphocyteOS-test.exe");
        check(requests.Count == 2 && requests[1] == index.Packages[0].Name, "incremental: downloads index and app only, skips unchanged core and licenses");
        check(Directory.GetFiles(prepared.Directory, "*.zip", SearchOption.AllDirectories).Length == 0 &&
            !File.Exists(Path.Combine(prepared.Directory, "payload", "mihomo.exe")), "incremental: no compressed archives or duplicated unchanged binaries retained");
        check(IncrementalUpdate.ReadReused(Path.Combine(prepared.Directory, "payload"), prepared.Manifest).Count == next.Count - 1,
            "incremental: unchanged files explicitly marked for verified reuse");
        UpdateInstaller.Apply(Path.Combine(prepared.Directory, "payload"), Plan(installed, "MorphocyteOS-test.exe"), Path.Combine(root, "rollback-success"));
        check(File.ReadAllBytes(Path.Combine(installed, "MorphocyteOS-test.exe")).SequenceEqual(next["MorphocyteOS.exe"]) &&
            File.ReadAllText(Path.Combine(installed, "private.yaml")) == "private profile", "incremental: sparse payload installs renamed EXE and preserves private profile");
        UpdateDownloader.DeleteOperation(cache, prepared.Directory);

        requests.Clear(); installed = Install("missing-core"); File.Delete(Path.Combine(installed, "mihomo.exe"));
        prepared = await UpdateDownloader.PrepareAsync(client, Release(Index()), cache, null, default, installed);
        check(requests.Count == 3 && File.Exists(Path.Combine(prepared.Directory, "payload", "mihomo.exe")), "incremental: missing managed core is downloaded and restored");
        UpdateInstaller.Apply(Path.Combine(prepared.Directory, "payload"), Plan(installed), Path.Combine(root, "rollback-missing"));
        check(File.Exists(Path.Combine(installed, "mihomo.exe")), "incremental: sparse installation restores missing file");
        UpdateDownloader.DeleteOperation(cache, prepared.Directory);

        requests.Clear(); installed = Install("changed-after-download");
        prepared = await UpdateDownloader.PrepareAsync(client, Release(Index()), cache, null, default, installed);
        File.WriteAllText(Path.Combine(installed, "mihomo.exe"), "external modification");
        await Reject(() => Task.Run(() => UpdateInstaller.Apply(Path.Combine(prepared.Directory, "payload"), Plan(installed), Path.Combine(root, "rollback-race"))),
            "incremental: changed reused file aborts before installation");
        check(File.ReadAllBytes(Path.Combine(installed, "MorphocyteOS.exe")).SequenceEqual(old["MorphocyteOS.exe"]), "incremental: race rejection leaves old app untouched");
        UpdateDownloader.DeleteOperation(cache, prepared.Directory);

        installed = Install("changed-during-install");
        prepared = await UpdateDownloader.PrepareAsync(client, Release(Index()), cache, null, default, installed);
        await Reject(() => Task.Run(() => UpdateInstaller.Apply(Path.Combine(prepared.Directory, "payload"), Plan(installed), Path.Combine(root, "rollback-mid-install"),
            count => { if (count == 1) File.WriteAllText(Path.Combine(installed, "README.md"), "external edit during install"); })),
            "incremental: reused file changed during transaction prevents manifest commit");
        check(File.ReadAllBytes(Path.Combine(installed, "MorphocyteOS.exe")).SequenceEqual(old["MorphocyteOS.exe"]) &&
            File.ReadAllText(Path.Combine(installed, "README.md")) == "external edit during install" &&
            UpdatePackage.ParseManifest(File.ReadAllBytes(Path.Combine(installed, UpdatePackage.ManifestName))).Version == "1.0.0",
            "incremental: race rollback restores app/version and preserves external edit");
        UpdateDownloader.DeleteOperation(cache, prepared.Directory);

        installed = Install("errors");
        await Reject(() => UpdateDownloader.PrepareAsync(client, Release(Index(), true), cache, null, default, installed), "incremental: index GitHub digest mismatch rejected");
        index = Index(); index.Manifest.Version = "1.2.0";
        await Reject(() => UpdateDownloader.PrepareAsync(client, Release(index), cache, null, default, installed), "incremental: wrong index version rejected");
        index = Index(); index.Packages[0].Sha256 = new string('0', 64); index.Packages[0].Name = $"MorphocyteOS-part-{index.Packages[0].Sha256}.zip";
        content[index.Packages[0].Name] = Package(new[] { "MorphocyteOS.exe" });
        await Reject(() => UpdateDownloader.PrepareAsync(client, Release(index), cache, null, default, installed), "incremental: component digest mismatch rejected, no full fallback");
        foreach (var extra in new[] { "../outside.txt", "settings.json", "Licenses/unlisted.txt" })
        {
            index = Index(); index.Packages[0] = Component(new[] { "MorphocyteOS.exe" }, Package(new[] { "MorphocyteOS.exe" }, extra));
            await Reject(() => UpdateDownloader.PrepareAsync(client, Release(index), cache, null, default, installed), "incremental: unexpected zip entry rejected: " + extra);
        }
        index = Index(); index.Packages[0] = Component(new[] { "MorphocyteOS.exe" }, Package(new[] { "MorphocyteOS.exe" }, duplicate: true));
        await Reject(() => UpdateDownloader.PrepareAsync(client, Release(index), cache, null, default, installed), "incremental: duplicate zip entry rejected");
        index = Index(); index.Packages[0] = Component(new[] { "MorphocyteOS.exe" }, Package(new[] { "MorphocyteOS.exe" }, tamper: true));
        await Reject(() => UpdateDownloader.PrepareAsync(client, Release(index), cache, null, default, installed), "incremental: file checksum/size mismatch rejected inside hashed component");
        index = Index(); index.Packages[0].Files.Add("README.md"); RejectIndex(index, "incremental: duplicate package ownership rejected");
        index = Index(); index.Packages.RemoveAt(1); RejectIndex(index, "incremental: missing package coverage rejected");
        index = Index(); index.Packages[0].Files[0] = "../outside.exe"; RejectIndex(index, "incremental: unknown package path rejected");
        index = Index(); index.Packages[0].Name = "../evil.zip"; RejectIndex(index, "incremental: unsafe package name rejected");
        index = Index(); index.Packages[0].Size = 536870913; RejectIndex(index, "incremental: oversized package rejected");
        requests.Clear();
        var unsafeRelease = Release(Index()); unsafeRelease = unsafeRelease with { IndexAsset = unsafeRelease.IndexAsset! with { Url = "https://github.com/other/repo/releases/download/v1.1.0/MorphocyteOS-1.1.0-update.json" } };
        await Reject(() => UpdateDownloader.PrepareAsync(client, unsafeRelease, cache, null, default, installed), "incremental: other-repository index rejected before any request");
        check(requests.Count == 0, "incremental: untrusted index makes no network request");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            await Reject(() => UpdateDownloader.PrepareAsync(client, Release(Index()), cache, null, cancel.Token, installed), "incremental: caller cancellation propagated");
        }
        check(!Directory.EnumerateDirectories(cache).Any(), "incremental: every failed/cancelled operation cleaned");

        // The full index can change version without changing any payload bytes.
        installed = Install("manifest-only"); File.WriteAllBytes(Path.Combine(installed, "MorphocyteOS.exe"), next["MorphocyteOS.exe"]);
        File.WriteAllBytes(Path.Combine(installed, UpdatePackage.ManifestName), JsonSerializer.SerializeToUtf8Bytes(Manifest("1.0.0", next)));
        requests.Clear(); prepared = await UpdateDownloader.PrepareAsync(client, Release(Index()), cache, null, default, installed);
        check(requests.Count == 1, "incremental: manifest-only upgrade downloads no components");
        UpdateInstaller.Apply(Path.Combine(prepared.Directory, "payload"), Plan(installed), Path.Combine(root, "rollback-manifest-only"));
        check(UpdatePackage.ParseManifest(File.ReadAllBytes(Path.Combine(installed, UpdatePackage.ManifestName))).Version == "1.1.0",
            "incremental: manifest-only transaction advances version safely");
        UpdateDownloader.DeleteOperation(cache, prepared.Directory);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
