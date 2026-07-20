using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Windows Job Object wrapper that binds child processes to the Unity process lifecycle.
    /// When Unity exits (even hard crash), all assigned processes are killed by the OS kernel.
    /// This is more reliable than any managed cleanup code because it operates at the NT kernel level.
    /// </summary>
    public static class WindowsJobObject
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr a, string lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public Int64 PerProcessUserTimeLimit;
            public Int64 PerJobUserTimeLimit;
            public UInt32 LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public UInt32 ActiveProcessLimit;
            public UIntPtr Affinity;
            public UInt32 PriorityClass;
            public UInt32 SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public UInt64 ReadOperationCount;
            public UInt64 WriteOperationCount;
            public UInt64 OtherOperationCount;
            public UInt64 ReadTransferCount;
            public UInt64 WriteTransferCount;
            public UInt64 OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private static IntPtr _jobHandle;

        static WindowsJobObject()
        {
            // Create the job object once per Unity lifecycle.
            // When this handle is closed (Unity exits), all assigned processes are killed by the kernel.
            _jobHandle = CreateJobObject(IntPtr.Zero, null);

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);
            Marshal.StructureToPtr(info, extendedInfoPtr, false);

            SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, extendedInfoPtr, (uint)length);
            Marshal.FreeHGlobal(extendedInfoPtr);
        }

        public static void AssignProcess(Process process)
        {
            if (process == null || process.HasExited) return;
            try
            {
                bool ok = AssignProcessToJobObject(_jobHandle, process.Handle);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    Log.Warning($"WindowsJobObject: Failed to assign PID {process.Id} to job object (error {err})");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"WindowsJobObject: Exception assigning process to job: {ex.Message}");
            }
        }
    }
}
