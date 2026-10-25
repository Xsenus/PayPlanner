using System.Runtime.InteropServices;

namespace PayPlanner.TestShards;

/// <summary>
/// On Windows, the supervisor joins its own unnamed job before starting children.
/// CreateProcess descendants inherit membership; no breakaway flag is enabled.
/// The sole, non-inheritable handle intentionally lives until process teardown:
/// closing it in Dispose/finalization would also terminate this supervisor and
/// replace its native exit code before Main returns.
/// </summary>
internal static class OwnedProcessTree
{
    private const uint KillOnJobClose = 0x00002000;
    private const int ExtendedLimitInformation = 9;
    private static nint processLifetimeJob;

    internal static void InitializeForCurrentProcess()
    {
        if (!OperatingSystem.IsWindows() || processLifetimeJob != nint.Zero) return;

        // Null SECURITY_ATTRIBUTES means that descendants cannot inherit the
        // handle and keep the job alive after this process is terminated.
        // https://learn.microsoft.com/windows/win32/procthread/job-objects
        var job = CreateJobObjectW(nint.Zero, null);
        if (job == nint.Zero) throw Failure("CreateJobObject");
        try
        {
            var limits = new JobExtendedLimits
            {
                BasicLimitInformation = new JobBasicLimits { LimitFlags = KillOnJobClose },
            };
            if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobExtendedLimits>()))
                throw Failure("SetInformationJobObject");
            // Assign ourselves, never a caller's PID. This happens before any
            // child is spawned, avoiding the race with a quickly spawned grandchild.
            // Existing runner jobs remain intact; Windows supports nested jobs.
            if (!AssignProcessToJobObject(job, GetCurrentProcess()))
                throw Failure("AssignProcessToJobObject");
        }
        catch
        {
            _ = CloseHandle(job); // Assignment failed; this job owns no processes.
            throw;
        }
        processLifetimeJob = job;
        // No SafeHandle, ProcessExit handler or finalizer: Windows closes the
        // retained raw handle after preserving this process's real exit status.
    }

    private static InvalidDataException Failure(string operation)
        => new($"Cannot establish owned Windows process tree: {operation} failed with Win32 error {Marshal.GetLastWin32Error()}.");

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
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
    private struct JobExtendedLimits
    {
        internal JobBasicLimits BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint securityAttributes, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int informationClass, ref JobExtendedLimits information, uint length);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
