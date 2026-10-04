using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;

namespace MorphocyteRouter;

internal static class TaskbarIconIntegration
{
    private static readonly Guid AppModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private static readonly Guid StoreId = typeof(IPropertyStore).GUID;
    private static readonly Guid ShellLinkId = new("00021401-0000-0000-C000-000000000046");

    internal static string ApplicationId(string executable) => "MorphocyteOS.Router." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executable).ToUpperInvariant())))[..20];

    internal static string EnsureIcon(string directory)
    {
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Icon directory is a link.");
        var bytes = ThemeIcons.CurrentIconBytes();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        // Content-addressed paths avoid Explorer reusing a cached green image.
        // Files remain available after exit for pinned shortcuts.
        var path = Path.Combine(directory, "morphocyte-" + hash[..24] + ".ico");
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                !UpdatePackage.Hash(path).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Stored icon changed.");
        }
        else
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
        }
        return path;
    }

    internal static void Apply(IntPtr window, string executable, string directory)
    {
        var icon = EnsureIcon(directory);
        var id = ApplicationId(executable);
        // Publish the current window icon before notifying Explorer about pins.
        ApplyWindow(window, executable, icon, id);
        var pins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned");
        UpdatePinnedShortcuts(pins, executable, icon, id);
    }

    internal static int UpdatePinnedShortcuts(string root, string executable, string icon, string id)
    {
        static bool SafeDirectory(string path) => Directory.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        root = Path.GetFullPath(root);
        if (!SafeDirectory(root)) return 0;
        var directories = new List<string> { Path.Combine(root, "TaskBar") };
        // Windows also stores pins generated from running windows in per-app
        // folders here. They are not present in the ordinary TaskBar directory.
        var implicitPins = Path.Combine(root, "ImplicitAppShortcuts");
        if (SafeDirectory(implicitPins))
        {
            directories.Add(implicitPins);
            directories.AddRange(Directory.EnumerateDirectories(implicitPins, "*", SearchOption.TopDirectoryOnly).Where(SafeDirectory));
        }
        var updated = 0;
        Exception? failure = null;
        foreach (var directory in directories.Where(SafeDirectory))
            foreach (var link in Directory.EnumerateFiles(directory, "*.lnk", SearchOption.TopDirectoryOnly))
            {
                var matching = false;
                try
                {
                    if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) continue;
                    matching = MatchesExecutable(ReadShortcut(link).Target, executable);
                    if (matching && UpdatePinnedShortcut(link, executable, icon, id)) updated++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException or ArgumentException)
                {
                    // Unrelated/broken shortcuts must not prevent this app's
                    // update. A failure on a confirmed own pin must be retried.
                    if (matching) failure = error;
                }
            }
        if (failure is not null) throw new IOException("A matching pinned shortcut could not be synchronized.", failure);
        return updated;
    }

    private static bool MatchesExecutable(string target, string executable)
    {
        target = Environment.ExpandEnvironmentVariables(target);
        return target.Length != 0 && Path.IsPathFullyQualified(target) &&
            Path.GetFullPath(target).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
    }

    internal static void ApplyWindow(IntPtr window, string executable, string icon, string id)
    {
        var guid = StoreId;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref guid, out var store));
        try
        {
            Set(store, 2, "\"" + Path.GetFullPath(executable) + "\"");
            Set(store, 4, "MorphocyteOS");
            Set(store, 3, icon + ",0");
            // Set ID last: it notifies the taskbar to refresh the other properties.
            Set(store, 5, id);
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    internal static string WindowProperty(IntPtr window, uint property)
    {
        var guid = StoreId;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref guid, out var store));
        try { return Get(store, property); } finally { Marshal.FinalReleaseComObject(store); }
    }

    internal static void ClearWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        var guid = StoreId;
        if (SHGetPropertyStoreForWindow(window, ref guid, out var store) < 0) return;
        try
        {
            foreach (uint property in new uint[] { 5, 2, 3, 4 })
            {
                var key = new PropertyKey(AppModel, property); var empty = new PropVariant();
                store.SetValue(ref key, ref empty); // VT_EMPTY releases window-owned values.
            }
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    internal sealed record ShortcutInfo(string Target, string Arguments, string WorkingDirectory, string Icon, int IconIndex, string Id);

    internal static ShortcutInfo ReadShortcut(string path)
    {
        var link = (IShellLink)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkId)!)!;
        try { ((IPersistFile)link).Load(path, 0); return Read(link); }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    private static ShortcutInfo Read(IShellLink link)
    {
        var target = new StringBuilder(32768); var arguments = new StringBuilder(32768);
        var working = new StringBuilder(32768); var icon = new StringBuilder(32768);
        // Never Resolve: a shortcut to another app must not trigger searches or network access.
        link.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
        link.GetArguments(arguments, arguments.Capacity); link.GetWorkingDirectory(working, working.Capacity);
        link.GetIconLocation(icon, icon.Capacity, out var index);
        return new(target.ToString(), arguments.ToString(), working.ToString(), icon.ToString(), index, Get((IPropertyStore)link, 5));
    }

    internal static bool UpdatePinnedShortcut(string path, string executable, string icon, string id)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 1_048_576) return false;
        var link = (IShellLink)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkId)!)!;
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var before = Read(link);
            if (!MatchesExecutable(before.Target, executable)) return false;
            if (before.Icon.Equals(icon, StringComparison.OrdinalIgnoreCase) && before.IconIndex == 0 && before.Id == id)
            {
                NotifyShortcutChanged(path, before, -1);
                return false;
            }
            // Capture the OLD image-list entry before replacing the shortcut.
            // UPDATEITEM alone can leave a pinned taskbar group's bitmap stale.
            var previousImage = ShellImageIndex(path);
            var original = File.ReadAllBytes(path);
            ((IPersistFile)link).Load(path, 2); // STGM_READWRITE, only after matching the target.
            if (Read(link) != before) return false;
            link.SetIconLocation(icon, 0);
            var store = (IPropertyStore)link;
            Set(store, 5, id); Marshal.ThrowExceptionForHR(store.Commit());
            // Only this exact matching shortcut is saved; target, arguments,
            // working directory, elevation and other flags are left intact.
            var temporary = path + ".morphocyte-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ((IPersistFile)link).Save(temporary, false);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    !File.ReadAllBytes(path).SequenceEqual(original)) throw new IOException("Shortcut changed during theme update.");
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            NotifyShortcutChanged(path, before, previousImage);
            return true;
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    private static int ShellImageIndex(string path) =>
        SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x4000) != IntPtr.Zero ? info.Index : -1;

    private static void NotifyShortcutChanged(string path, ShortcutInfo previous, int previousImage)
    {
        // Invalidate only this shortcut's former image, never the global icon
        // cache or associations. The themed ICO files themselves are immutable.
        if (previousImage >= 0 && previous.IconIndex == 0 &&
            Path.GetFileName(previous.Icon).StartsWith("morphocyte-", StringComparison.OrdinalIgnoreCase) &&
            previous.Icon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(previous.Icon))
            SHUpdateImage(previous.Icon, previous.IconIndex, 0, previousImage);
        SHChangeNotify(0x2000, 0x2005, path, IntPtr.Zero); // UPDATEITEM | PATHW | FLUSHNOWAIT
        SHChangeNotify(0x1000, 0x2005, Path.GetDirectoryName(path)!, IntPtr.Zero); // UPDATEDIR
    }

    private static void Set(IPropertyStore store, uint property, string text)
    {
        var key = new PropertyKey(AppModel, property);
        var value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(text) }; // VT_LPWSTR
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value)); }
        finally { PropVariantClear(ref value); }
    }

    private static string Get(IPropertyStore store, uint property)
    {
        var key = new PropertyKey(AppModel, property);
        Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
        try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "" : ""; }
        finally { PropVariantClear(ref value); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey(Guid format, uint id) { internal Guid Format = format; internal uint Id = id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant
    { [FieldOffset(0)] internal ushort Type; [FieldOffset(8)] internal IntPtr Pointer; }
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(int events, uint flags, string item, IntPtr unused);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHUpdateImage(string path, int index, uint flags, int imageIndex);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ShellFileInfo
    {
        internal IntPtr Icon;
        internal int Index;
        internal uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] internal string TypeName;
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr data, uint flags);
        void GetIDList(out IntPtr list); void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey); void SetHotkey(short hotkey);
        void GetShowCmd(out int command); void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
