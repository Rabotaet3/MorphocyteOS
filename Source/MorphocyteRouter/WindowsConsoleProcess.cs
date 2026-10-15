using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MorphocyteRouter;

/// <summary>A private, hidden console lets Go handle an interrupt and close its cache normally.</summary>
internal sealed class WindowsConsoleProcess : IDisposable
{
    private IntPtr _thread;
    internal Process Process { get; }
    private readonly StreamReader _output;
    private readonly StreamReader _error;
    private Task? _outputPump;
    private Task? _errorPump;
    private readonly CancellationTokenSource _pumpCancellation = new();
    private int _disposed;

    private WindowsConsoleProcess(Process process, IntPtr thread, StreamReader output, StreamReader error)
        => (Process, _thread, _output, _error) = (process, thread, output, error);

    internal static WindowsConsoleProcess Start(ProcessStartInfo info)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (info.UseShellExecute || !info.RedirectStandardOutput || !info.RedirectStandardError)
            throw new ArgumentException("The private core console requires redirected output and no shell.");
        var executable = Path.GetFullPath(info.FileName);
        using var output = new Pipe();
        using var error = new Pipe();
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        using var input = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
        if (input.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Explicit inheritance restricts this child to its three standard handles.
        using var attributes = new HandleAttributes(input.DangerousGetHandle(), output.Write.DangerousGetHandle(), error.Write.DangerousGetHandle());
        var startup = new StartupInfoEx
        {
            StartupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x101, ShowWindow = 0,
                StandardInput = input.DangerousGetHandle(), StandardOutput = output.Write.DangerousGetHandle(),
                StandardError = error.Write.DangerousGetHandle()
            },
            AttributeList = attributes.List
        };
        var command = new StringBuilder(QuoteArgument(executable));
        if (info.ArgumentList.Count > 0)
            foreach (var argument in info.ArgumentList) command.Append(' ').Append(QuoteArgument(argument));
        else if (info.Arguments.Length > 0) command.Append(' ').Append(info.Arguments);
        var environmentText = string.Join('\0', info.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Where(pair => pair.Value is not null).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        var environment = Marshal.StringToHGlobalUni(environmentText);
        ProcessInformation created = default;
        Process? process = null;
        StreamReader? outputReader = null;
        StreamReader? errorReader = null;
        try
        {
            // NEW_CONSOLE is isolated from the UI/Codex console. SW_HIDE avoids flashing a window.
            // SUSPENDED lets the caller attach its kill-on-close job before the child can run.
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                    0x10 | 0x4 | 0x400 | 0x80000, environment,
                    string.IsNullOrWhiteSpace(info.WorkingDirectory) ? Path.GetDirectoryName(executable) : info.WorkingDirectory,
                    ref startup, out created))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось создать скрытый процесс ядра.");
            process = Process.GetProcessById((int)created.ProcessId);
            _ = process.Handle; // Pin the wrapper before releasing the original creation handle.
            outputReader = new StreamReader(output.TakeRead(), info.StandardOutputEncoding ?? Encoding.UTF8);
            errorReader = new StreamReader(error.TakeRead(), info.StandardErrorEncoding ?? Encoding.UTF8);
            return new WindowsConsoleProcess(process, created.Thread, outputReader, errorReader);
        }
        catch
        {
            if (created.Process != IntPtr.Zero) TerminateProcess(created.Process, 1);
            if (created.Thread != IntPtr.Zero) CloseHandle(created.Thread);
            outputReader?.Dispose(); errorReader?.Dispose(); process?.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
            if (created.Process != IntPtr.Zero) CloseHandle(created.Process);
        }
    }

    internal void Resume(Action<Process, string> output)
    {
        _outputPump = Pump(_output, output);
        _errorPump = Pump(_error, output);
        var thread = Interlocked.Exchange(ref _thread, IntPtr.Zero);
        if (thread == IntPtr.Zero) throw new InvalidOperationException("Core already resumed.");
        try
        {
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { CloseHandle(thread); }
    }

    private async Task Pump(StreamReader reader, Action<Process, string> output)
    {
        try { while (await reader.ReadLineAsync(_pumpCancellation.Token).ConfigureAwait(false) is { } line) output(Process, line); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }

    internal async Task<bool> TryCloseAsync(TimeSpan timeout)
    {
        if (Process.HasExited) return true;
        if (_thread != IntPtr.Zero) return false; // Not resumed: no signal handler can run yet.
        var host = Environment.ProcessPath ?? throw new IOException("Не найден процесс приложения.");
        var info = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Framework-dependent test/CLI host only. A single-file app launches its EXE.
#pragma warning disable IL3000
            info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
#pragma warning restore IL3000
        }
        info.ArgumentList.Add("--signal-owned-core");
        info.ArgumentList.Add(Process.Id.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(Process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(Process.MainModule!.FileName);
        using var helper = Process.Start(info) ?? throw new IOException("Не удалось отправить сигнал ядру.");
        try
        {
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            if (helper.ExitCode != 0) return Process.HasExited;
        }
        catch (TimeoutException)
        {
            if (!helper.HasExited) helper.Kill();
            try { await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { /* Still proceed to the owned core's force-stop fallback. */ }
            return Process.HasExited;
        }
        try { await Process.WaitForExitAsync().WaitAsync(timeout); return true; }
        catch (TimeoutException) { return false; }
    }

    // Run in a short-lived helper: attaching a console never changes the main UI's console.
    internal static int SignalOwnedCore(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 4 || args[0] != "--signal-owned-core"
            || !int.TryParse(args[1], out var id) || id <= 0 || id == Environment.ProcessId
            || !long.TryParse(args[2], out var ticks)) return 1;
        try
        {
            using var target = Process.GetProcessById(id);
            if (target.HasExited) return 0;
            if (target.StartTime.ToUniversalTime().Ticks != ticks
                || !string.Equals(target.MainModule?.FileName, Path.GetFullPath(args[3]), StringComparison.OrdinalIgnoreCase)) return 2;
            // A console test host may inherit its parent's console. Detach only this helper.
            FreeConsole();
            if (!AttachConsole((uint)id)) return target.HasExited ? 0 : 3;
            try
            {
                if (IsWindowVisible(GetConsoleWindow())) return 8; // Never signal a user's visible console.
                var ids = new uint[64];
                var count = GetConsoleProcessList(ids, (uint)ids.Length);
                if (count == 0 || count > ids.Length || !ids.Take((int)count).Contains((uint)id)
                    || ids.Take((int)count).Any(value => value != id && value != Environment.ProcessId)) return 4;
                // No broadcast into a user shell or another core is permitted.
                if (!SetConsoleCtrlHandler(IgnoreControl, true)) return 5;
                if (target.HasExited) return 0;
                if (!GenerateConsoleCtrlEvent(1, 0)) return 6;
                // Dispatch is asynchronous. Stay attached while our ignore handler
                // runs; immediate FreeConsole would reset it and could kill the helper.
                target.WaitForExit(1000);
                return 0;
            }
            finally { FreeConsole(); }
        }
        catch { return 7; }
    }

    private static readonly ConsoleControlHandler IgnoreControl = _ => true;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var thread = Interlocked.Exchange(ref _thread, IntPtr.Zero);
        if (thread != IntPtr.Zero) CloseHandle(thread);
        // A concurrent unrelated child might retain an inherited writer. Cancellation
        // ensures pipe readers never wait for that child's lifetime after our core exits.
        _pumpCancellation.Cancel();
        Task.WhenAll(_outputPump ?? Task.CompletedTask, _errorPump ?? Task.CompletedTask)
            .ContinueWith(_ => { _output.Dispose(); _error.Dispose(); _pumpCancellation.Dispose(); }, TaskScheduler.Default);
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
            result.Append(ch); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private sealed class Pipe : IDisposable
    {
        private NamedPipeServerStream? _read;
        internal SafeFileHandle Write { get; }
        internal Pipe()
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            var name = "morphocyte-core-" + Guid.NewGuid().ToString("N");
            _read = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Write = CreateFile("\\\\.\\pipe\\" + name, 0x40000000, 0, ref security, 3, 0, IntPtr.Zero);
            try
            {
                if (Write.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                _read.WaitForConnection();
            }
            catch { Dispose(); throw; }
        }
        internal Stream TakeRead() => Interlocked.Exchange(ref _read, null) ?? throw new InvalidOperationException();
        public void Dispose() { _read?.Dispose(); Write.Dispose(); }
    }

    private sealed class HandleAttributes : IDisposable
    {
        internal IntPtr List { get; private set; }
        private IntPtr _handles;
        private bool _initialized;
        internal HandleAttributes(params IntPtr[] handles)
        {
            nuint bytes = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            List = Marshal.AllocHGlobal((nint)bytes);
            try
            {
                if (!InitializeProcThreadAttributeList(List, 1, 0, ref bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
                _initialized = true;
                _handles = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
                Marshal.Copy(handles, 0, _handles, handles.Length);
                if (!UpdateProcThreadAttribute(List, 0, (nuint)0x20002, _handles,
                        (nuint)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_initialized) DeleteProcThreadAttributeList(List);
            if (List != IntPtr.Zero) Marshal.FreeHGlobal(List);
            if (_handles != IntPtr.Zero) Marshal.FreeHGlobal(_handles);
            List = _handles = IntPtr.Zero; _initialized = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size; internal string? Reserved; internal string? Desktop; internal string? Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal short ShowWindow, ReservedSize; internal IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { internal StartupInfo StartupInfo; internal IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    private delegate bool ConsoleControlHandler(uint control);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleProcessList([Out] uint[] ids, uint count);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleCtrlHandler(ConsoleControlHandler handler, [MarshalAs(UnmanagedType.Bool)] bool add);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GenerateConsoleCtrlEvent(uint control, uint group);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
}
