using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunShellThemeChecks(string scratch)
    {
        var root = Path.Combine(scratch, "shell-theme"); Directory.CreateDirectory(root);
        var settings = AppSettings.Load(Path.Combine(root, "settings.json")); settings.AutoCheckUpdates = false;
        var window = new MainWindow(settings, Path.Combine(root, "test.log")) { ShowInTaskbar = false };
        var count = 0;
        void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL: " + text); count++; Console.WriteLine("PASS: " + text); }
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            typeof(MainWindow).GetMethod("InitializeDesktopIntegration", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var menu = (ContextMenu)Field("_trayMenu"); menu.Placement = PlacementMode.AbsolutePoint; menu.HorizontalOffset = 200; menu.VerticalOffset = 150;
            // Materialize the template BEFORE changing themes: this was missing
            // from the former tests and exposed the stale detached popup palette.
            menu.IsOpen = true; await Task.Delay(130); menu.IsOpen = false;
            window.HideToTray();
            foreach (var theme in ThemeManager.ThemeNames)
            {
                ThemeManager.Apply(theme); menu.IsOpen = true; await Task.Delay(130); menu.UpdateLayout();
                var border = Descendants<Border>(menu).First();
                Check(ReferenceEquals(border.Background, ThemeManager.GetBrush("ThemeTrayBackground")), "previously opened hidden-owner tray background updates: " + theme);
                Check(ReferenceEquals(border.BorderBrush, ThemeManager.GetBrush("ThemeAppOutline")) && menu.Items.OfType<MenuItem>().Where(item => !Equals(item.Header, "Выход")).All(item => ReferenceEquals(item.Foreground, ThemeManager.GetBrush("ThemeText"))), "tray outline and menu text update together: " + theme);
                if (theme is "Аметист" or "Океан" or "Светлая морфоцитная") CaptureInteraction(menu, Path.Combine(root, "tray-" + theme + ".png"));
                menu.IsOpen = false;
            }
            menu.IsOpen = true; ThemeManager.Apply("Аметист"); await Task.Delay(130);
            Check(ReferenceEquals(Descendants<Border>(menu).First().Background, ThemeManager.GetBrush("ThemeTrayBackground")), "an already visible tray popup also changes theme without recreation");
            menu.IsOpen = false; window.RestoreFromTray();

            var dialog = DialogChrome.CreateWindow(window, "НАСТРОЙКИ", 420, 160);
            dialog.Content = DialogChrome.BuildFrame(dialog, "НАСТРОЙКИ", new Grid());
            try
            {
                dialog.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                foreach (var scale in new[] { .8, 1d, 1.5 })
                {
                    UiScaleManager.Apply(dialog, (FrameworkElement)dialog.Content, scale); dialog.UpdateLayout();
                    var close = Descendants<Button>(dialog).Single(button => button.Name == "DialogCloseButton");
                    var glyph = (System.Windows.Shapes.Path)close.Content;
                    var border = (Border)close.Template.FindName("B", close);
                    var bounds = glyph.TransformToAncestor(border).TransformBounds(new Rect(glyph.RenderSize));
                    Check(Math.Abs(bounds.X + bounds.Width / 2 - border.ActualWidth / 2) < .5 && Math.Abs(bounds.Y + bounds.Height / 2 - border.ActualHeight / 2) < .5, "vector close glyph is centered at interface scale " + scale);
                    CaptureInteraction(dialog, Path.Combine(root, "close-" + (int)(scale * 100) + ".png"));
                }
                Descendants<Button>(dialog).Single(button => button.Name == "DialogCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!dialog.IsVisible, "centered close button retains its normal close action");
            }
            finally { if (dialog.IsVisible) dialog.Close(); }

            ThemeManager.Apply("Аметист"); var icons = Path.Combine(root, "ShellIcons");
            var amethyst = TaskbarIconIntegration.EnsureIcon(icons);
            ThemeManager.Apply("Океан"); var ocean = TaskbarIconIntegration.EnsureIcon(icons);
            Check(amethyst != ocean && File.Exists(amethyst) && File.Exists(ocean), "different themed shell icons have durable distinct cache-safe paths");
            ThemeManager.Apply("Аметист"); Check(TaskbarIconIntegration.EnsureIcon(icons) == amethyst && Directory.GetFiles(icons).Length == 2, "repeated theme choice reuses icons without creating endless copies");
            var exe = Path.Combine(root, "Папка с пробелами", "MorphocyteOS-test.exe"); Directory.CreateDirectory(Path.GetDirectoryName(exe)!); File.WriteAllText(exe, "fixture only");
            var otherExe = Path.Combine(root, "different", "MorphocyteOS-test.exe"); Directory.CreateDirectory(Path.GetDirectoryName(otherExe)!); File.WriteAllText(otherExe, "other fixture");
            var id = TaskbarIconIntegration.ApplicationId(exe);
            Check(id == TaskbarIconIntegration.ApplicationId(exe.ToUpperInvariant()) && id != TaskbarIconIntegration.ApplicationId(otherExe), "taskbar identity is stable across versions and isolated per exact executable path");
            var handle = new WindowInteropHelper(window).Handle;
            TaskbarIconIntegration.ApplyWindow(handle, exe, amethyst, id);
            Check(TaskbarIconIntegration.WindowProperty(handle, 3) == amethyst + ",0" && TaskbarIconIntegration.WindowProperty(handle, 5) == id, "new pins receive the actual theme icon and explicit application identity");
            Check(TaskbarIconIntegration.WindowProperty(handle, 2) == "\"" + exe + "\"" && TaskbarIconIntegration.WindowProperty(handle, 4) == "MorphocyteOS", "pin relaunch preserves Unicode and space-containing executable path");
            var linkPath = Path.Combine(root, "own.lnk"); var otherLink = Path.Combine(root, "other.lnk");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            try
            {
                foreach (var pair in new[] { (linkPath, exe), (otherLink, otherExe) })
                {
                    dynamic shortcut = shell.CreateShortcut(pair.Item1);
                    try { shortcut.TargetPath = pair.Item2; shortcut.Arguments = "--startup"; shortcut.WorkingDirectory = root; shortcut.IconLocation = amethyst + ",0"; shortcut.Save(); }
                    finally { Marshal.FinalReleaseComObject(shortcut); }
                }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
            var before = TaskbarIconIntegration.ReadShortcut(linkPath); var otherBytes = File.ReadAllBytes(otherLink);
            Check(TaskbarIconIntegration.UpdatePinnedShortcut(linkPath, exe, ocean, id), "existing matching pin updates instead of reverting to the embedded green icon");
            var after = TaskbarIconIntegration.ReadShortcut(linkPath);
            Check(after.Icon == ocean && after.IconIndex == 0 && after.Id == id && after.Target == before.Target && after.Arguments == before.Arguments && after.WorkingDirectory == before.WorkingDirectory, "pin theme change preserves executable, launch arguments and working directory");
            Check(!TaskbarIconIntegration.UpdatePinnedShortcut(otherLink, exe, ocean, id) && File.ReadAllBytes(otherLink).SequenceEqual(otherBytes), "same-named executable in another folder and its shortcut are never changed");
            Check(!TaskbarIconIntegration.UpdatePinnedShortcut(linkPath, exe, ocean, id), "already synchronized pin does not rewrite its shortcut unnecessarily");
            var pins = Path.Combine(root, "User Pinned");
            var ordinary = Path.Combine(pins, "TaskBar");
            var implicitPins = Path.Combine(pins, "ImplicitAppShortcuts", "0123456789abcdef");
            Directory.CreateDirectory(ordinary); Directory.CreateDirectory(implicitPins);
            var ordinaryLink = Path.Combine(ordinary, "MorphocyteOS.lnk");
            var implicitLink = Path.Combine(implicitPins, "MorphocyteOS.lnk");
            var unrelatedLink = Path.Combine(implicitPins, "Other.lnk");
            File.Copy(linkPath, ordinaryLink); File.Copy(linkPath, implicitLink); File.Copy(otherLink, unrelatedLink);
            Check(TaskbarIconIntegration.UpdatePinnedShortcuts(pins, exe, amethyst, id) == 2,
                "theme updates both ordinary pins and Windows per-app ImplicitAppShortcuts pins");
            Check(TaskbarIconIntegration.ReadShortcut(implicitLink).Icon == amethyst && TaskbarIconIntegration.ReadShortcut(ordinaryLink).Icon == amethyst,
                "implicitly stored shortcut no longer retains its first pinned theme");
            Check(File.ReadAllBytes(unrelatedLink).SequenceEqual(otherBytes), "searching both pin stores still leaves other applications untouched");
            Check(TaskbarIconIntegration.UpdatePinnedShortcuts(pins, exe, ocean, id) == 2 && TaskbarIconIntegration.ReadShortcut(implicitLink).Icon == ocean,
                "repeated theme changes refresh a previously updated implicit pin");
            Check(TaskbarIconIntegration.UpdatePinnedShortcuts(pins, exe, ocean, id) == 0,
                "unchanged theme does not rewrite either pin store");
            TaskbarIconIntegration.ApplyWindow(handle, exe, ocean, id);
            Check(TaskbarIconIntegration.WindowProperty(handle, 3) == ocean + ",0", "window relaunch icon switches to the new theme too");
            var retainedIcon = Field("_smallWindowIcon");
            var retainedHandle = (IntPtr)retainedIcon.GetType().GetProperty("Handle")!.GetValue(retainedIcon)!;
            foreach (var theme in ThemeManager.ThemeNames.Concat(ThemeManager.ThemeNames.Reverse()))
            {
                ThemeManager.Apply(theme);
                var currentIcon = TaskbarIconIntegration.EnsureIcon(icons);
                TaskbarIconIntegration.UpdatePinnedShortcuts(pins, exe, currentIcon, id);
                TaskbarIconIntegration.ApplyWindow(handle, exe, currentIcon, id);
                var reference = Path.Combine(root, "reference.lnk");
                File.Copy(implicitLink, reference, true);
                // Compare images actually extracted by Windows, not just the
                // saved ICO path; the latter missed stale taskbar images.
                Check(ShortcutImageSignature(implicitLink) == ShortcutImageSignature(reference), "Windows shell extracts current icon after repeated theme switch: " + theme);
                Check(TaskbarIconIntegration.ReadShortcut(implicitLink).Icon == currentIcon && TaskbarIconIntegration.WindowProperty(handle, 3) == currentIcon + ",0", "pinned shortcut and native window stay synchronized: " + theme);
            }
            var finalPin = File.ReadAllBytes(implicitLink);
            TaskbarIconIntegration.UpdatePinnedShortcuts(pins, exe, TaskbarIconIntegration.EnsureIcon(icons), id);
            Check(File.ReadAllBytes(implicitLink).SequenceEqual(finalPin), "shell re-notification does not rewrite an already correct pinned shortcut");
            Check(Imaging.CreateBitmapSourceFromHIcon(retainedHandle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()).PixelWidth > 0,
                "previous theme icon handle remains valid while Explorer may still be processing its update");
            ThemeManager.Apply("Океан"); var cachedIcon = Field("_smallWindowIcon");
            ThemeManager.Apply("Аметист"); ThemeManager.Apply("Океан");
            Check(ReferenceEquals(cachedIcon, Field("_smallWindowIcon")), "returning to a theme reuses a stable native icon instead of creating and destroying handles");
            var desired = TaskbarIconIntegration.EnsureIcon(icons);
            using (var refresh = new ShellThemeRefresh())
            {
                var failures = 0;
                var oldPin = File.ReadAllBytes(implicitLink);
                refresh.Request(() => TaskbarIconIntegration.UpdatePinnedShortcuts(pins, exe, desired, id), _ => failures++);
                await Task.Delay(70);
                File.WriteAllBytes(implicitLink, oldPin); // Emulate a delayed old Shell write.
                await refresh.Completion;
                Check(TaskbarIconIntegration.ReadShortcut(implicitLink).Icon == desired && failures == 0,
                    "delayed old shortcut write is corrected by the current-theme synchronization");
            }
            count += await RunShellRefreshChecks();
            Check(!((CoreProcessManager)Field("_core")).IsRunning, "shell-theme checks never start a live or fake VPN core");
        }
        finally
        {
            TaskbarIconIntegration.ClearWindow(new WindowInteropHelper(window).Handle);
            typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close(); UiScaleManager.CurrentScale = 1; ThemeManager.Apply(null);
        }
        return count;
    }

    private static string ShortcutImageSignature(string path)
    {
        if (ShellCheckGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<ShellCheckFileInfo>(), 0x100) == IntPtr.Zero || info.Icon == IntPtr.Zero)
            throw new Exception("Windows did not extract the fixture shortcut icon.");
        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var pixels = new byte[bitmap.PixelHeight * ((bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8)];
            bitmap.CopyPixels(pixels, pixels.Length / bitmap.PixelHeight, 0);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
        }
        finally { ShellCheckDestroyIcon(info.Icon); }
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)] private static extern IntPtr ShellCheckGetFileInfo(string path, uint attributes, out ShellCheckFileInfo info, uint size, uint flags);
    [DllImport("user32.dll", EntryPoint = "DestroyIcon")] private static extern bool ShellCheckDestroyIcon(IntPtr icon);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ShellCheckFileInfo
    {
        internal IntPtr Icon; internal int Index; internal uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] internal string TypeName;
    }
}
