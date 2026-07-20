using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Starts a child process with handle inheritance DISABLED. Unity Mono's Process.Start
    /// inherits parent handles even with redirects off, so sidecars spawned after the game
    /// binds its server port (26900) inherit that socket and strand the port on
    /// quit-to-menu -> re-host. CreateProcess(bInheritHandles=FALSE) guarantees no sidecar
    /// ever inherits the game's sockets.
    /// </summary>
    public static class NoInheritProcess
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(
            string lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
            string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            ref SECURITY_ATTRIBUTES lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint CREATE_ALWAYS = 2;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const int INVALID_HANDLE_VALUE = -1;
        private const uint STARTF_USESTDHANDLES = 0x00000100;

        /// <summary>Start exePath with args in workingDir, inheritance OFF by default.
        /// If stdOutErrLogPath is provided, captures stdout/stderr to that file for diagnostics.
        /// Child inherits the parent's environment (set ggml vars on the parent before calling).
        /// Returns a managed Process for Kill/HasExited/Id, or null on failure.</summary>
        public static Process Start(string exePath, string args, string workingDir, string stdOutErrLogPath = null)
        {
            var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si);
            var cmd = new StringBuilder().Append('"').Append(exePath).Append('"');
            if (!string.IsNullOrEmpty(args)) cmd.Append(' ').Append(args);

            IntPtr logHandle = IntPtr.Zero;
            if (!string.IsNullOrEmpty(stdOutErrLogPath))
            {
                var sa = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                    lpSecurityDescriptor = IntPtr.Zero,
                    bInheritHandle = true
                };
                logHandle = CreateFileW(stdOutErrLogPath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    ref sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                if (logHandle.ToInt64() != INVALID_HANDLE_VALUE)
                {
                    si.hStdOutput = logHandle;
                    si.hStdError = logHandle;
                    si.dwFlags |= (int)STARTF_USESTDHANDLES;
                }
                else
                {
                    Log.Warning($"NoInheritProcess: failed to create log file at {stdOutErrLogPath} (win32 {Marshal.GetLastWin32Error()}) - continuing without stdout/stderr capture");
                    logHandle = IntPtr.Zero;
                }
            }

            bool inheritHandles = logHandle != IntPtr.Zero;
            if (!CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero,
                    inheritHandles, CREATE_NO_WINDOW, IntPtr.Zero,
                    workingDir, ref si, out PROCESS_INFORMATION pi))
            {
                Log.Error($"NoInheritProcess: CreateProcess failed for {exePath} (win32 {Marshal.GetLastWin32Error()})");
                if (logHandle != IntPtr.Zero) CloseHandle(logHandle);
                return null;
            }

            if (logHandle != IntPtr.Zero) CloseHandle(logHandle);  // child has its own inherited copy now

            try
            {
                var proc = Process.GetProcessById(pi.dwProcessId);
                WindowsJobObject.AssignProcess(proc);   // dies with Unity
                return proc;
            }
            catch (Exception ex)
            {
                Log.Warning($"NoInheritProcess: started PID {pi.dwProcessId} but couldn't attach: {ex.Message}");
                return null;
            }
            finally { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
        }

        /// <summary>Read back a captured stdout/stderr log file. Returns null if the file doesn't
        /// exist, is empty, or can't be read.</summary>
        public static string ReadCapturedLog(string logPath)
        {
            try
            {
                if (string.IsNullOrEmpty(logPath) || !System.IO.File.Exists(logPath)) return null;
                string content = System.IO.File.ReadAllText(logPath);
                return string.IsNullOrWhiteSpace(content) ? null : content;
            }
            catch (Exception ex)
            {
                Log.Debug(() => $"NoInheritProcess: couldn't read captured log at {logPath}: {ex.Message}");
                return null;
            }
        }
    }
}
