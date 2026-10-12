using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MorphocyteRouter;

/// <summary>Windowless installer operations. User profiles/settings are never loaded or written.</summary>
internal static class InstallerMaintenance
{
    internal const string MarkerName = ".installer-owned.json";
    internal const string ProductionAppId = "8AC863D3-AC74-44C3-90EC-082E8A9AA951";
    internal const string TestAppId = "40B5D24D-0B16-48E6-85C5-6BE9C471D115";
    private const int MaximumSettingsBytes = 2 * 1024 * 1024;
    private const int MaximumTreeEntries = 20000;
    private const string AppMutex = "Local\\MorphocyteOS.Router.SingleInstance";

    private sealed class OwnershipMarker
    {
        public int SchemaVersion { get; set; }
        public string AppId { get; set; } = "";
        public bool TestMode { get; set; }
    }

    // Exit 0 includes intentionally preserved modified/user files; 1 is an unsafe or failed operation.
    internal static int Run(string mode, string installDirectory, string appId, Action<Exception>? onError = null)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        try
        {
            using var gate = new Mutex(false, AppMutex);
            var ownsGate = false;
            try
            {
                try { ownsGate = gate.WaitOne(0); }
                catch (AbandonedMutexException) { ownsGate = true; }
                if (!ownsGate) throw new IOException("Приложение MorphocyteOS ещё открыто."); // Never close its VPN on the user's behalf.
                var helper = Environment.ProcessPath ?? throw new IOException("Не найден установщик.");
                var settings = SettingsFiles(installDirectory);
                if (mode == "--installer-validate-target")
                {
                    var incomingPath = Path.Combine(Path.GetDirectoryName(helper)!, "installer-payload-manifest.json");
                    ValidateAncestry(incomingPath);
                    if (!File.Exists(incomingPath) || new FileInfo(incomingPath).Length > MaximumSettingsBytes)
                        throw new IOException("Не найден список файлов установщика: " + incomingPath);
                    var incoming = UpdatePackage.ParseManifest(File.ReadAllBytes(incomingPath), false);
                    ValidateTarget(installDirectory, appId, helper, settings, incoming);
                }
                else if (mode == "--installer-cleanup") Cleanup(installDirectory, appId, helper, settings, RemoveOwnedStartupTasks);
                else return 1;
                return 0;
            }
            finally { if (ownsGate) gate.ReleaseMutex(); }
        }
        catch (Exception error) { onError?.Invoke(error); return 1; }
    }

    internal static void ValidateTarget(string installDirectory, string appId, string helperExecutable,
        IEnumerable<string> settingsFiles, ReleaseManifest? incoming = null)
    {
        var root = ValidateRoot(installDirectory, appId, helperExecutable);
        if (!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any()) return;
        var marker = ReadMarker(root, appId);
        var manifest = ReadManifest(root);
        ValidateTree(root);
        var protectedFiles = ReadProtectedFiles(root, settingsFiles, manifest);
        // Repair/reinstall is allowed only for an identified installation and unmodified owned bytes.
        // A user's replacement core must not be overwritten by the setup wizard.
        foreach (var file in manifest.Files)
        {
            var path = UpdatePackage.ResolveFile(root, file.Path);
            if (Directory.Exists(path)) throw new IOException("Путь приложения занят папкой.");
            if (File.Exists(path) && !Matches(path, file))
                throw new IOException("В папке установки изменены файлы приложения; выбери другую папку.");
        }
        if (incoming is not null)
        {
            ReleaseUpdateService.SemanticVersion.TryParse(manifest.Version, out var currentVersion);
            ReleaseUpdateService.SemanticVersion.TryParse(incoming.Version, out var incomingVersion);
            if (incomingVersion is null || incomingVersion.CompareTo(currentVersion) != 0)
                throw new IOException("Для новой версии используй обновление в приложении или выбери пустую папку установки.");
            var incomingPaths = incoming.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (manifest.Files.Any(file => !incomingPaths.Contains(file.Path)))
                throw new IOException("Установщик не содержит все файлы установленного комплекта; используй обновление в приложении.");
            var owned = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var file in incoming.Files)
            {
                var path = UpdatePackage.ResolveFile(root, file.Path);
                if (Directory.Exists(path)) throw new IOException("Путь нового файла занят папкой.");
                if (!File.Exists(path)) continue;
                if (!owned.TryGetValue(file.Path, out var current) || !Matches(path, current))
                    throw new IOException("Новый файл комплекта пересекается с пользовательским файлом.");
                if (protectedFiles.Contains(path))
                    throw new IOException("Новый файл комплекта пересекается с выбранным профилем или ядром.");
            }
        }
        _ = marker;
    }

    // Incremental updates do not rerun Setup. Refresh only an existing, verified installation entry;
    // portable/test folders without a matching marker are never registered automatically.
    internal static bool RefreshRegistrationVersion(string installDirectory, string version)
    {
        if (!OperatingSystem.IsWindows() || !ReleaseUpdateService.SemanticVersion.TryParse(version, out _)) return false;
        try
        {
            var root = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            ValidateAncestry(root);
            var markerPath = Path.Combine(root, MarkerName);
            if (!File.Exists(markerPath) || new FileInfo(markerPath).Length > 4096) return false;
            var raw = ParseMarkerUnchecked(File.ReadAllBytes(markerPath));
            if (!raw.AppId.Equals(ProductionAppId, StringComparison.OrdinalIgnoreCase) &&
                !raw.AppId.Equals(TestAppId, StringComparison.OrdinalIgnoreCase)) return false;
            var marker = ReadMarker(root, raw.AppId);
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var entry = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{" + marker.AppId + "}_is1", writable: true);
            if (entry is null || entry.GetValue("InstallLocation") is not string location ||
                entry.GetValue("DisplayIcon") is not string icon || !Path.IsPathFullyQualified(location) ||
                !Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(root, StringComparison.OrdinalIgnoreCase)) return false;
            var executable = Path.Combine(root, "MorphocyteOS.exe");
            if (!icon.Equals(executable, StringComparison.OrdinalIgnoreCase) &&
                !icon.Equals('"' + executable + '"', StringComparison.OrdinalIgnoreCase) &&
                !icon.Equals(executable + ",0", StringComparison.OrdinalIgnoreCase) &&
                !icon.Equals('"' + executable + "\",0", StringComparison.OrdinalIgnoreCase)) return false;
            _ = UpdatePackage.ResolveFile(root, "MorphocyteOS.exe");
            if (!File.Exists(executable)) return false;
            entry.SetValue("DisplayVersion", version, RegistryValueKind.String);
            return true;
        }
        catch { return false; } // Registry metadata must not prevent a successful VPN/application update.
    }

    internal static void Cleanup(string installDirectory, string appId, string helperExecutable,
        IEnumerable<string> settingsFiles, Action<string> removeStartupTasks, Action<int>? afterRemoval = null)
    {
        var root = ValidateRoot(installDirectory, appId, helperExecutable);
        ValidateTree(root);
        var manifestPath = UpdatePackage.ResolveFile(root, UpdatePackage.ManifestName);
        var markerPath = Path.Combine(root, MarkerName);
        // Keep ownership documents pinned against changes until every managed removal has succeeded.
        using var manifestHandle = OpenDeletionHandle(manifestPath);
        using var markerHandle = OpenDeletionHandle(markerPath);
        _ = ParseMarker(ReadPinned(markerHandle, 4096), appId);
        var manifest = UpdatePackage.ParseManifest(ReadPinned(manifestHandle, MaximumSettingsBytes), false);
        var protectedFiles = ReadProtectedFiles(root, settingsFiles, manifest);
        var removals = new List<(ReleaseFile File, string Path)>();
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var path = UpdatePackage.ResolveFile(root, file.Path);
            if (Directory.Exists(path)) throw new IOException("Путь приложения занят папкой.");
            if (!protectedFiles.Contains(path) && File.Exists(path) && Matches(path, file))
            {
                using var checkedHandle = OpenDeletionHandle(path); // Refuse mapped/locked cores before any mutation.
                removals.Add((file, path));
            }
            for (var parent = Path.GetDirectoryName(path); parent is not null && !parent.Equals(root, StringComparison.OrdinalIgnoreCase);
                parent = Path.GetDirectoryName(parent)) parents.Add(parent);
        }
        // COM errors refuse uninstall before any installed bytes are removed.
        removeStartupTasks(Path.Combine(root, "MorphocyteOS.exe"));
        var removed = 0;
        foreach (var removal in removals.OrderBy(removal => removal.File.Path.Equals("MorphocyteOS.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            _ = UpdatePackage.ResolveFile(root, removal.File.Path);
            // Re-read settings immediately before each removal: an external settings edit cannot
            // turn a newly selected core/profile into an uninstall target.
            if (ReadProtectedFiles(root, settingsFiles, manifest).Contains(removal.Path)) continue;
            using var handle = OpenDeletionHandle(removal.Path);
            using var stream = new FileStream(handle, FileAccess.Read);
            if (stream.Length != removal.File.Size ||
                !Convert.ToHexString(SHA256.HashData(stream)).Equals(removal.File.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            MarkForDeletion(handle);
            afterRemoval?.Invoke(++removed);
        }
        foreach (var directory in parents.OrderByDescending(path => path.Length))
        {
            ValidateAncestry(directory);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory, false);
        }
        // Modified managed files and user files may remain. Never recursively remove the root.
        MarkForDeletion(manifestHandle);
        MarkForDeletion(markerHandle);
    }

    private static IEnumerable<string> SettingsFiles(string installDirectory)
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MorphocyteOS", "Application", "settings.json");
        return new[] { local, local + ".bak", Path.Combine(installDirectory, "settings.json"),
            Path.Combine(installDirectory, "settings.json.bak") };
    }

    private static HashSet<string> ReadProtectedFiles(string root, IEnumerable<string> settingsFiles, ReleaseManifest? manifest = null)
    {
        var protectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in settingsFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ValidateAncestry(path);
            if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length > MaximumSettingsBytes) throw new InvalidDataException("Слишком большой файл настроек.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Повреждён файл настроек.");
            foreach (var field in new[] { "ConfigPath", "CorePath", "DraftConfigPath" })
                if (json.RootElement.TryGetProperty(field, out var value))
                {
                    if (value.ValueKind is JsonValueKind.Null) continue;
                    if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Повреждён выбранный путь.");
                    Protect(value.GetString(), field == "CorePath");
                }
            foreach (var field in new[] { "ProfileDrafts", "ProfileRuleStates", "ProfileSubscriptions", "ProfileFolderOrders" })
                if (json.RootElement.TryGetProperty(field, out var profiles) && profiles.ValueKind != JsonValueKind.Null)
                {
                    if (profiles.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Повреждён список профилей.");
                    foreach (var profile in profiles.EnumerateObject()) Protect(profile.Name);
                }
        }
        return protectedFiles;

        void Protect(string? value, bool core = false)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var path = Path.GetFullPath(Path.IsPathFullyQualified(value) ? value : Path.Combine(root, value));
            if (path.Equals(Path.Combine(root, "MorphocyteOS.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Выбранный пользовательский файл совпадает с приложением.");
            if (core && manifest is not null)
            {
                var bundled = manifest.Files.FirstOrDefault(file => IsBundledCore(file.Path) &&
                    UpdatePackage.ResolveFile(root, file.Path).Equals(path, StringComparison.OrdinalIgnoreCase));
                // Every actual removal is hash-checked again under an exclusive handle. Recognizing
                // the known bundled path here avoids hashing the core once per license/support file.
                if (bundled is not null) return;
            }
            protectedFiles.Add(path);
        }
    }

    private static bool IsBundledCore(string relative) => relative.Equals("mihomo.exe", StringComparison.OrdinalIgnoreCase) ||
        relative.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase) ||
        relative.Equals("ThirdPartySource/Engine/sing-box.exe", StringComparison.OrdinalIgnoreCase);

    private static string ValidateRoot(string installDirectory, string appId, string helperExecutable)
    {
        if (!Guid.TryParseExact(appId, "D", out _) ||
            (!appId.Equals(ProductionAppId, StringComparison.OrdinalIgnoreCase) && !appId.Equals(TestAppId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Неизвестная установка.");
        if (!Path.IsPathFullyQualified(installDirectory)) throw new InvalidDataException("Нужен полный путь установки.");
        var root = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var broad = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "",
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        };
        foreach (var folder in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)) broad.Add(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        if (broad.Contains(root)) throw new InvalidDataException("Приложение должно находиться в отдельной папке.");
        ValidateAncestry(root);
        if (File.Exists(root)) throw new IOException("Папка установки занята файлом.");
        var helper = Path.GetFullPath(helperExecutable);
        if (!File.Exists(helper) || helper.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            helper.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Установщик должен работать из отдельной временной папки.");
        ValidateAncestry(helper);
        return root;
    }

    private static OwnershipMarker ReadMarker(string root, string appId)
    {
        var path = Path.Combine(root, MarkerName);
        ValidateAncestry(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 4096) throw new InvalidDataException("Установка не принадлежит этому установщику.");
        return ParseMarker(File.ReadAllBytes(path), appId);
    }

    private static OwnershipMarker ParseMarker(byte[] bytes, string appId)
    {
        var marker = ParseMarkerUnchecked(bytes);
        if (marker.SchemaVersion != 1 || !marker.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase) ||
            marker.TestMode != appId.Equals(TestAppId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Отметка установки не совпадает.");
        return marker;
    }

    private static OwnershipMarker ParseMarkerUnchecked(byte[] bytes)
    {
        if (bytes.Length > 4096) throw new InvalidDataException("Слишком большая отметка установки.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("SchemaVersion", out var schema) ||
            !schema.TryGetInt32(out var schemaVersion) || !root.TryGetProperty("AppId", out var id) ||
            id.ValueKind != JsonValueKind.String || !root.TryGetProperty("TestMode", out var test) ||
            test.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Повреждена отметка установки.");
        return new OwnershipMarker { SchemaVersion = schemaVersion, AppId = id.GetString()!, TestMode = test.GetBoolean() };
    }

    private static ReleaseManifest ReadManifest(string root)
    {
        var path = UpdatePackage.ResolveFile(root, UpdatePackage.ManifestName);
        if (!File.Exists(path) || new FileInfo(path).Length > MaximumSettingsBytes) throw new InvalidDataException("Нет списка установленных файлов.");
        return UpdatePackage.ParseManifest(File.ReadAllBytes(path), false);
    }

    private static bool Matches(string path, ReleaseFile file) =>
        new FileInfo(path).Length == file.Size && UpdatePackage.Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);

    private static void ValidateAncestry(string path)
    {
        for (var check = Path.GetFullPath(path); check is not null; check = Path.GetDirectoryName(check))
        {
            try
            {
                if ((File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Установка через ссылки или точки подключения запрещена.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void ValidateTree(string root)
    {
        var remaining = MaximumTreeEntries;
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            ValidateAncestry(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (--remaining < 0) throw new IOException("В папке установки слишком много посторонних файлов.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("В папке установки обнаружена ссылка.");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
            }
        }
    }

    private static void RemoveOwnedStartupTasks(string executable)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("Планировщик Windows недоступен.");
        object? scheduler = null, folder = null, tasks = null;
        try
        {
            scheduler = Activator.CreateInstance(type)!;
            ((dynamic)scheduler).Connect();
            folder = ((dynamic)scheduler).GetFolder("\\");
            tasks = ((dynamic)folder).GetTasks(1);
            var count = (int)((dynamic)tasks).Count;
            if (count > 4096) throw new IOException("Слишком много задач автозапуска.");
            var names = new List<string>();
            for (var index = 1; index <= count; index++)
            {
                object? task = null;
                try
                {
                    task = ((dynamic)tasks).Item(index);
                    var name = (string)((dynamic)task).Name;
                    if (name.StartsWith("MorphocyteOS-S-1-", StringComparison.Ordinal) && name.Length <= 200) names.Add(name);
                }
                finally { Release(task); }
            }
            // Enumerate before deleting; the COM collection must not change beneath its index.
            foreach (var name in names)
            {
                object? task = null, definition = null, actions = null, action = null;
                try
                {
                    task = ((dynamic)folder).GetTask(name);
                    definition = ((dynamic)task).Definition;
                    actions = ((dynamic)definition).Actions;
                    if ((int)((dynamic)actions).Count != 1) continue;
                    action = ((dynamic)actions).Item(1);
                    if ((int)((dynamic)action).Type != 0) continue;
                    var path = (string)((dynamic)action).Path;
                    if (!IsOwnedStartupAction(name, path, executable)) continue;
                    ((dynamic)folder).DeleteTask(name, 0);
                }
                finally { Release(action); Release(actions); Release(definition); Release(task); }
            }
        }
        finally { Release(tasks); Release(folder); Release(scheduler); }

        static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    }

    internal static bool IsOwnedStartupAction(string name, string path, string executable)
    {
        if (!name.StartsWith("MorphocyteOS-S-1-", StringComparison.Ordinal) || name.Length > 200) return false;
        try
        {
            _ = new SecurityIdentifier(name["MorphocyteOS-".Length..]);
            return Path.IsPathFullyQualified(path) && Path.GetFullPath(path).Equals(executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    // Delete the verified open file, not a path that could have been replaced after hashing.
    private static SafeFileHandle OpenDeletionHandle(string path)
    {
        ValidateAncestry(path);
        var handle = CreateFile(path, 0x80010000, 0, IntPtr.Zero, 3, 0x00200080, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось открыть файл для удаления."); }
        if (!GetFileInformationByHandle(handle, out var information) ||
            (information.FileAttributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0)
        {
            handle.Dispose();
            throw new IOException("Небезопасный файл установки.");
        }
        var finalPath = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, finalPath, (uint)finalPath.Capacity, 0);
        if (length == 0 || length >= finalPath.Capacity)
        {
            handle.Dispose();
            throw new IOException("Не удалось проверить настоящий путь файла установки.");
        }
        var resolved = finalPath.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        if (!Path.GetFullPath(resolved).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            handle.Dispose();
            throw new IOException("Настоящий путь файла отличается от папки установки.");
        }
        return handle;
    }

    private static void MarkForDeletion(SafeFileHandle handle)
    {
        var disposition = new FileDisposition { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, Marshal.SizeOf<FileDisposition>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось удалить файл приложения.");
    }

    private static byte[] ReadPinned(SafeFileHandle handle, int maximumBytes)
    {
        var length = RandomAccess.GetLength(handle);
        if (length > maximumBytes) throw new InvalidDataException("Слишком большой файл установки.");
        var bytes = new byte[(int)length];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = RandomAccess.Read(handle, bytes.AsSpan(read), read);
            if (count == 0) throw new IOException("Файл установки прочитан не полностью.");
            read += count;
        }
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { public int DeleteFile; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flags, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileDisposition information, int bufferSize);
}
