using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MorphocyteRouter;

/// <summary>
/// Ties the Mihomo child process to this application process. Windows closes the
/// job handle automatically if the UI crashes or is force-closed, which then
/// terminates the core instead of leaving TUN and VPN routes behind.
/// </summary>
internal sealed class ProcessLifetimeJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private IntPtr _handle;

    private ProcessLifetimeJob(IntPtr handle) => _handle = handle;

    internal static ProcessLifetimeJob Attach(Process process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Mihomo lifetime jobs require Windows.");
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a Windows process job.");

        try
        {
            var information = new JobObjectExtendedLimitInformation();
            information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(information, memory, false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, memory, (uint)size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable kill-on-close for the Mihomo job.");
            }
            finally { Marshal.FreeHGlobal(memory); }

            if (!AssignProcessToJobObject(handle, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows refused to attach Mihomo to the application's lifetime job.");
            return new ProcessLifetimeJob(handle);
        }
        catch
        {
            CloseHandle(handle);
            throw;
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
