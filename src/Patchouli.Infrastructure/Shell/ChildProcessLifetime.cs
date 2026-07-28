using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Patchouli.Infrastructure.Shell;

/// <summary>
/// Keeps child processes tied to the current process lifetime.
/// Windows uses a Job Object with KILL_ON_JOB_CLOSE. Unix hosts rely on the
/// sidecar parent-PID watchdog, stdin EOF, and explicit kill on dispose/exit.
/// </summary>
public sealed class ChildProcessLifetime : IDisposable
{
    private readonly object _gate = new();
    private bool _disposed;
    private IntPtr _jobHandle = IntPtr.Zero;

    public ChildProcessLifetime()
    {
        if (OperatingSystem.IsWindows())
        {
            _jobHandle = WindowsJob.CreateKillOnCloseJob();
        }
    }

    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!OperatingSystem.IsWindows() || _jobHandle == IntPtr.Zero || process.HasExited)
            {
                return;
            }

            if (!WindowsJob.AssignProcessToJobObject(_jobHandle, process.Handle))
            {
                int error = Marshal.GetLastPInvokeError();
                // ERROR_ACCESS_DENIED (5): process already in a non-breakaway job.
                // Keep running and rely on explicit kill + parent-PID watchdog.
                if (error is not 5)
                {
                    throw new InvalidOperationException(
                        $"Failed to assign sidecar to job object (Win32 {error}).");
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (OperatingSystem.IsWindows() && _jobHandle != IntPtr.Zero)
            {
                WindowsJob.CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static class WindowsJob
    {
        private const int JobObjectInfoClassExtendedLimit = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        public static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Failed to create job object (Win32 {Marshal.GetLastPInvokeError()}).");
            }

            JobObjectExtendedLimitInformation info = new()
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };

            int length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            IntPtr infoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                if (!SetInformationJobObject(job, JobObjectInfoClassExtendedLimit, infoPtr, (uint)length))
                {
                    int error = Marshal.GetLastPInvokeError();
                    CloseHandle(job);
                    throw new InvalidOperationException(
                        $"Failed to configure job object (Win32 {error}).");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }

            return job;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob,
            int jobObjectInfoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
