using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RuptureServerManager.Util
{
    internal class SteamCmdManager
    {
        public static SteamCmdManager Instance => _instance ??= new();
        private static SteamCmdManager? _instance;

        private readonly string _steamCmdDir;
        private readonly int _steamAppId = 3809400;
        private readonly string _serverPath;
        private Action<string> _logger;

        public SteamCmdManager()
        {
            var _folder = Path.Combine(Application.StartupPath, "steamcmd");
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

            _serverPath = Path.Combine(Application.StartupPath, "server");

            _steamCmdDir = _folder;
        }

        public void AssignLogger(Action<string> logger)
        {
            _logger = logger;
        }

        public string GetSteamCmdDir() => _steamCmdDir;
        public string GetAppId() => _steamAppId.ToString();

        private bool IsSteamCmdInstalled()
        {
            string exePath = Path.Combine(_steamCmdDir, "steamcmd.exe");
            return File.Exists(exePath);
        }

        public async Task CheckAndDownloadSteamCmdAsync()
        {
            string exePath = Path.Combine(_steamCmdDir, "steamcmd.exe");
            if (!File.Exists(exePath))
            {
                await InstallSteamCmd();
            }
        }

        internal async Task<string> InvokeSteamCmdAsync(string arguments, Action<string>? liveLog = null, CancellationToken ct = default)
        {
            var steamCmdExe = Path.Combine(_steamCmdDir, "steamcmd.exe");

            _logger?.Invoke("SteamCMD is running, you may not see progress until it completes. Please be patient");

            if (!IsSteamCmdInstalled())
                await InstallSteamCmd();

            _logger?.Invoke($"Invoking SteamCMD with arguments: `{arguments}`");

            var psi = new ProcessStartInfo
            {
                FileName = steamCmdExe,
                Arguments = arguments,
                WorkingDirectory = _steamCmdDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var output = new StringBuilder(capacity: 16 * 1024);
            object gate = new(); // StringBuilder isn't thread-safe

            process.Start();

            // Pump both streams concurrently as *raw chunks* (captures \r progress)
            Task pumpOut = PumpAsync(process.StandardOutput, "[SteamCMD] ", output, gate, liveLog, ct);
            Task pumpErr = PumpAsync(process.StandardError, "[SteamCMD][ERR] ", output, gate, liveLog, ct);

            await Task.WhenAll(pumpOut, pumpErr, process.WaitForExitAsync(ct));

            // Keep your original behavior: non-zero exit => throw
            if (process.ExitCode != 0)
                throw new Exception($"SteamCMD exited with code {process.ExitCode}\n\n{output}");

            return output.ToString();
        }

        private async Task PumpAsync(
            StreamReader reader,
            string prefix,
            StringBuilder sink,
            object gate,
            Action<string>? liveLog,
            CancellationToken ct)
        {
            char[] buffer = new char[2048];
            int read;

            while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                var chunk = new string(buffer, 0, read);

                // 1) store combined output
                lock (gate)
                    sink.Append(prefix).Append(chunk);

                // 2) optional live UI logging
                liveLog?.Invoke(prefix + chunk);
            }

            liveLog?.Invoke("Live Log Ended\n");
        }


    public async Task InstallSteamCmd()
        {

            if (IsSteamCmdInstalled())
            {
                _logger?.Invoke("SteamCMD is installed.");
                return;
            }

            try
            {
                _logger?.Invoke("Downloading SteamCMD...");

                string zipUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
                string tempZipPath = Path.Combine(_steamCmdDir, "steamcmd.zip");
                string steamCmdExe = Path.Combine(_steamCmdDir, "steamcmd.exe");

                Directory.CreateDirectory(_steamCmdDir);

                using HttpClient client = new();
                using HttpResponseMessage response = await client.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                await using (FileStream fs = new(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs);
                }

                _logger?.Invoke("Download complete. Extracting SteamCMD...");
                System.IO.Compression.ZipFile.ExtractToDirectory(tempZipPath, _steamCmdDir, overwriteFiles: true);
                File.Delete(tempZipPath);

                _logger?.Invoke("SteamCMD extracted. Running first-time update...");
                await RunSteamCmdSelfUpdateAsync(steamCmdExe);
                _logger?.Invoke("SteamCMD update completed successfully.");
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error downloading or updating SteamCMD: {ex.Message}");
            }
        }

        private async Task RunSteamCmdSelfUpdateAsync(string steamCmdExe)
        {
            await InstallSteamCmd();
            await InvokeSteamCmdAsync("+quit", logLine =>
            {
                _logger?.Invoke(logLine);
            });
        }

        public async Task UpdateServerAsync()
        {
            string steamCmdExe = Path.Combine(_steamCmdDir, "steamcmd.exe");
            if (!File.Exists(steamCmdExe))
            {
                _logger?.Invoke("SteamCMD executable not found. Please download SteamCMD first.");
                return;
            }

            string arguments = $"+force_install_dir \"{_serverPath}\" +login anonymous +app_update {_steamAppId} validate +quit";

            await InvokeSteamCmdAsync(arguments, logLine =>
            {
                _logger?.Invoke(logLine);
            });

            _logger?.Invoke("SteamCMD update completed.");
        }

        public int GetLocalBuildId()
        {
            try
            {
                string manifestPath = Path.Combine(_serverPath, "steamapps", $"appmanifest_{_steamAppId}.acf");
                if (!File.Exists(manifestPath))
                    return 0;
                foreach (string line in File.ReadLines(manifestPath))
                {
                    if (line.Trim().StartsWith("\"buildid\""))
                    {
                        string value = line.Split('"', StringSplitOptions.RemoveEmptyEntries)[3];
                        return int.TryParse(value, out int build) ? build : 0;
                    }
                }
            }
            catch { }
            return 0;
        }

        public async Task<int> GetRemoteBuildIdAsync()
        {
            string steamCmdExe = Path.Combine(_steamCmdDir, "steamcmd.exe");
            if (!File.Exists(steamCmdExe))
                return 0;

            var psi = new ProcessStartInfo
            {
                FileName = steamCmdExe,
                Arguments = $"+login anonymous +app_info_update 1 +app_info_print {_steamAppId} +quit",
                WorkingDirectory = _steamCmdDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return 0;

            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            foreach (string line in output.Split('\n'))
            {
                if (line.Contains("\"buildid\""))
                {
                    string value = line.Split('"', StringSplitOptions.RemoveEmptyEntries)[3];
                    return int.TryParse(value, out int build) ? build : 0;
                }
            }
            return 0;
        }
    }
}
