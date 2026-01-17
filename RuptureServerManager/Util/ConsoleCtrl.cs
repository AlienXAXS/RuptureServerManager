using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RuptureServerManager.Util
{
    using System;
    using System.ComponentModel;
    using System.Runtime.InteropServices;
    using System.Threading;

    public static class ConsoleCtrl
    {
        private const uint CTRL_C_EVENT = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handlerRoutine, bool add);

        private delegate bool ConsoleCtrlDelegate(uint ctrlType);

        /// <summary>
        /// Sends a CTRL+C to the console that the target process is attached to.
        /// If processGroupId is 0, it goes to ALL processes attached to that console.
        /// To target a specific group, you must have started the process tree with CREATE_NEW_PROCESS_GROUP.
        /// </summary>
        public static void SendCtrlC(int targetPid, int processGroupId = 0)
        {
            // Detach from our current console (if any), so AttachConsole works reliably.
            FreeConsole();

            if (!AttachConsole((uint)targetPid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"AttachConsole({targetPid}) failed.");

            // Prevent THIS process from receiving CTRL+C.
            // (Ignore Ctrl+C while we generate it.)
            if (!SetConsoleCtrlHandler(null, true))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetConsoleCtrlHandler failed.");

            try
            {
                if (!GenerateConsoleCtrlEvent(CTRL_C_EVENT, (uint)processGroupId))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GenerateConsoleCtrlEvent failed.");
            }
            finally
            {
                // Re-enable Ctrl+C handling for this process.
                SetConsoleCtrlHandler(null, false);
                FreeConsole();
            }
        }
    }

    public static class ConsoleWindowHider
    {
        private const int SW_HIDE = 0;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public static bool WaitAndHideConsoleWindowForProcess(int pid, int timeoutMs = 10000, int pollMs = 500)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (TryHideConsoleWindowForProcess(pid))
                    return true;

                Thread.Sleep(pollMs);
            }

            return false;
        }

        private static bool TryHideConsoleWindowForProcess(int pid)
        {
            IntPtr found = IntPtr.Zero;

            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint windowPid);
                if (windowPid != (uint)pid) return true;

                var cls = new StringBuilder(256);
                GetClassName(hWnd, cls, cls.Capacity);

                if (cls.ToString() == "ConsoleWindowClass")
                {
                    found = hWnd;
                    return false; // stop enumeration
                }

                return true;
            }, IntPtr.Zero);

            if (found == IntPtr.Zero)
                return false;

            ShowWindow(found, SW_HIDE);
            return true;
        }
    }

}
