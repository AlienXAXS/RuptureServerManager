using RuptureServerManager.Util;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RuptureServerManager.Server
{
    public class ServerManager
    {
        public static ServerManager Instance => _instance ??= new();
        private static ServerManager? _instance;

        private Process? _serverProcess;
        private Process? _actualServerProcess;
        private readonly string _serverPath;
        private Action<string>? _logger;

        public ServerManager()
        {
            var _folder = Path.Combine(Application.StartupPath, "server");
            if (!Directory.Exists(_folder))
            {
                try
                {
                    Directory.CreateDirectory(_folder);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Unable to create new directory {_folder}, error: {ex.Message}.  Please ensure you have the needed permissions.");
                }
            }
            _serverPath = _folder;
        }

        public string GetServerPath() => _serverPath;
        public bool IsRunning => _serverProcess != null && !_serverProcess.HasExited;

        public void AssignLogger(Action<string> logger)
        {
            _logger = logger;
        }

        public bool IsServerInstalled()
        {
            string serverExe = Path.Combine(_serverPath, "StarRuptureServerEOS.exe");
            return File.Exists(serverExe);
        }

        private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
        private const uint CREATE_NEW_CONSOLE = 0x00000010;

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public uint cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        public static Process StartInNewProcessGroup(string exePath, string args, string workingDir)
        {
            var si = new STARTUPINFO { cb = (uint)Marshal.SizeOf<STARTUPINFO>() };

            // lpCommandLine must be mutable; easiest is exe + args in one string
            string cmdLine = $"\"{exePath}\" {args}";

            if (!CreateProcess(
                    exePath,
                    cmdLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CREATE_NEW_PROCESS_GROUP | CREATE_NEW_CONSOLE,
                    IntPtr.Zero,
                    workingDir,
                    ref si,
                    out var pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed.");
            }

            // We can close handles; Process can re-open by PID
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);

            return Process.GetProcessById((int)pi.dwProcessId);
        }

        public bool StartServer()
        {
            if (IsRunning)
            {
                _logger?.Invoke("Server is already running.");
                return true;
            }

            var port = Util.ConfigManager.Instance.GetConfig().Port;

            string serverExe = Path.Combine(_serverPath, "StarRuptureServerEOS.exe");
            string args = $"-Port={port} -Log -RCWebControlDisable -RCWebInterfaceDisable";

            try
            {
                // IMPORTANT: launch in a new process group
                _serverProcess = StartInNewProcessGroup(
                    serverExe,
                    args,
                    _serverPath
                );

                _serverProcess.EnableRaisingEvents = true;
                _serverProcess.Exited += (s, e) =>
                {
                    _logger?.Invoke("Server process exited.");
                };

                _logger?.Invoke($"Server launcher started (PID {_serverProcess.Id}).");

                try
                {
                    var actual = WaitForActualServerProcess(timeoutMs: 8000);
                    if (actual != null)
                    {
                        Util.ConsoleWindowHider.WaitAndHideConsoleWindowForProcess(actual.Id, 5000);
                        _logger?.Invoke($"Hidden log window for server PID {actual.Id}.");
                    }
                    _actualServerProcess = actual;
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"Failed to hide log window: {ex.Message}");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Failed to start server: {ex.Message}");
                _serverProcess = null;
                return false;
            }
        }


        public bool StartServerOld()
        {
            if (IsRunning)
            {
                _logger?.Invoke("Server is already running.");
                return true;
            }

            var port = Util.ConfigManager.Instance.GetConfig().Port;

            string serverExe = Path.Combine(_serverPath, "StarRuptureServerEOS.exe");
            string args = $"-Port={port} -Log -RCWebControlDisable -RCWebInterfaceDisable";
            var psi = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = args,
                WorkingDirectory = _serverPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                _serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _serverProcess.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) _logger?.Invoke(e.Data!); };
                _serverProcess.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) _logger?.Invoke(e.Data!); };
                _serverProcess.Exited += (s, e) => { _logger?.Invoke("Server process exited."); };
                _serverProcess.Start();
                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();
                _logger?.Invoke("Server started.");
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Failed to start server: {ex.Message}");
                _serverProcess = null;
            }

            try
            {
                var actualServerProcess = WaitForActualServerProcess();
                if (actualServerProcess != null)
                {
                    _actualServerProcess = actualServerProcess;
                    bool hidden = ConsoleWindowHider.WaitAndHideConsoleWindowForProcess(actualServerProcess.Id, timeoutMs: 5000);
                    if (hidden)
                        _logger?.Invoke("Log window hidden.");
                    else
                        _logger?.Invoke("Log window not found (may not be created with current launch settings).");
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Failed to hide log window: {ex.Message}");
            }

            return true;
        }

        private Process? WaitForActualServerProcess(int timeoutMs = 10000, int pollMs = 500)
        {
            if (_serverProcess == null || _serverProcess.HasExited)
                throw new InvalidOperationException("Server process is not running.");

            // Use the *WMI Name* (exe name)
            var processName = "StarRuptureServerEOS-Win64-Shipping.exe";

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT ProcessId FROM Win32_Process " +
                    $"WHERE ParentProcessId = {_serverProcess.Id} AND Name = '{processName}'");

                foreach (ManagementObject mo in searcher.Get())
                {
                    try
                    {
                        int pid = Convert.ToInt32(mo["ProcessId"]);
                        return Process.GetProcessById(pid);
                    }
                    catch
                    {
                        // process exited between query and GetProcessById
                    }
                }

                System.Threading.Thread.Sleep(pollMs);
            }

            return null;
        }

        public void StopServer()
        {
            if (IsRunning)
            {
                _logger?.Invoke("Stopping server...");
                try
                {
                    _serverProcess?.Kill();
                    _serverProcess?.WaitForExit();
                    _logger?.Invoke("Server stopped.");
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"Error stopping server: {ex.Message}");
                }
            }
            else
            {
                _logger?.Invoke("Server is not running.");
            }
        }

        public async Task StopServerAsync()
        {
            if (!IsRunning)
            {
                _logger?.Invoke("Server is not running.");
                return;
            }

            _logger?.Invoke("Requesting server shutdown...");
            try
            {
                if (_actualServerProcess != null && _serverProcess != null && !_actualServerProcess.HasExited)
                {
                    Util.ConsoleCtrl.SendCtrlC(_actualServerProcess.Id, _serverProcess.Id);
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Shutdown command send failed (expected): {ex.Message}");
            }

            _logger?.Invoke("Waiting for server to stop (saving may take time)...");
            bool exited = await Task.Run(() => _serverProcess!.WaitForExit(10000));
            if (exited)
            {
                _logger?.Invoke("Server exited cleanly.");
            }
            else
            {
                _logger?.Invoke("Server did not exit in time. Forcing shutdown...");
                _serverProcess!.Kill(true);
                await _serverProcess.WaitForExitAsync();
                _logger?.Invoke("Server forcefully stopped.");
            }
        }

        public async Task SendServerCommandAsync(string command)
        {
            if (!IsRunning)
            {
                _logger?.Invoke("Server is not running.");
                return;
            }
            try
            {
                _logger?.Invoke($"> {command}");
                await _serverProcess!.StandardInput.WriteLineAsync(command);
                await _serverProcess.StandardInput.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Failed to send command: {ex.Message}");
            }
        }
    }
}
