using RuptureServerManager.Server;
using RuptureServerManager.Util;
using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Threading.Timer;

namespace RuptureServerManager
{
    /// <summary>
    /// Main form for the dedicated server controller. Handles UI events,
    /// persistence of settings, launching/updating external processes, and now
    /// supports an auto-update timer that monitors the configured Steam app ID
    /// for updates. When an update is detected (or on schedule), the server
    /// will be stopped, updated via SteamCMD, and then restarted.
    /// </summary>
    public partial class MainForm : Form
    {
        private ServerUiState _uiState = ServerUiState.Idle;
        private readonly string ServerSettingsFileName = "DSSettings.txt";
        private bool _isUpdating = false;
        private Timer? _autoUpdateTimer;
        private readonly TimeSpan _autoUpdateInterval = TimeSpan.FromMinutes(30);

        public MainForm()
        {
            this.Shown += MainForm_Shown;
            InitializeComponent();
        }

        private async void MainForm_Shown(object? sender, EventArgs e)
        {
            _uiState = ServerUiState.Busy;
            UpdateButtonStates();

            await SteamCmdManager.Instance.CheckAndDownloadSteamCmdAsync();

            _uiState = ServerUiState.Idle;
            UpdateButtonStates();
        }

        /// <summary>
        /// Loads persisted settings and ensures SteamCMD is available when the form loads.
        /// Initializes the auto-update timer.
        /// </summary>
        private async void MainForm_Load(object? sender, EventArgs e)
        {
            ConfigManager.Instance.AssignLogger(AppendConsole);
            ServerManager.Instance.AssignLogger(AppendConsole);
            SteamCmdManager.Instance.AssignLogger(AppendConsole);

            ApplySettingsToUi();
            StartAutoUpdateTimer();
        }

        /// <summary>
        /// Updates the UI fields to reflect the currently loaded settings.
        /// </summary>
        private void ApplySettingsToUi()
        {
            var _settings = ConfigManager.Instance.GetConfig();
            // Ensure values are within allowed ranges
            portNumericUpDown.Value = Math.Clamp(_settings.Port, (int)portNumericUpDown.Minimum, (int)portNumericUpDown.Maximum);
            sessionNameTextBox.Text = _settings.SessionName;
            saveGameIntervalNumericUpDown.Value = Math.Clamp(_settings.SaveGameInterval, (int)saveGameIntervalNumericUpDown.Minimum, (int)saveGameIntervalNumericUpDown.Maximum);
            //startNewGameCheckBox.Checked = _settings.StartNewGame;
            //loadSavedGameCheckBox.Checked = _settings.LoadSavedGame;
            saveGameNameTextBox.Text = _settings.SaveGameName;
            autoUpdateCheckBox.Checked = _settings.UpdateEnabled == 1;
            updateIntervalTextBox.Text = _settings.UpdateInterval.ToString();
        }

        /// <summary>
        /// Writes the current settings object to disk as JSON. The port value is
        /// updated from the UI before saving.
        /// </summary>
        private void SaveSettingsToFile()
        {
            var _settings = ConfigManager.Instance.GetConfig();

            UpdateSettingsFromUi();
            ConfigManager.Instance.Save();

            // Save server settings (excluding port)
            try
            {
                var serverSettings = new
                {
                    _settings.SessionName,
                    _settings.SaveGameInterval,
                    _settings.StartNewGame,
                    _settings.LoadSavedGame,
                    _settings.SaveGameName,
                };

                var serverJson = JsonSerializer.Serialize(serverSettings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(ServerManager.Instance.GetServerPath(), ServerSettingsFileName), serverJson);
            }
            catch (Exception ex)
            {
                AppendConsole($"Error saving server settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the internal settings object with values from UI controls.
        /// </summary>
        private void UpdateSettingsFromUi()
        {
            var _settings = ConfigManager.Instance.GetConfig();
            _settings.Port = (int)portNumericUpDown.Value;
            _settings.SessionName = sessionNameTextBox.Text.Trim();
            _settings.SaveGameInterval = (int)saveGameIntervalNumericUpDown.Value;
            _settings.StartNewGame = false;
            _settings.LoadSavedGame = true;
            _settings.SaveGameName = saveGameNameTextBox.Text.Trim();
            _settings.UpdateInterval = int.TryParse(updateIntervalTextBox.Text.Trim(), out int interval) ? interval : 30;
            _settings.UpdateEnabled = autoUpdateCheckBox.Checked ? 1 : 0;

            StartAutoUpdateTimer();
        }

        /// <summary>
        /// Disable controls when the server is starting so the user cannot
        /// modify settings or start the server again. Only the stop button
        /// remains enabled to allow for graceful shutdown.
        /// </summary>
        private void DisableButtonsOnStart()
        {
            saveSettingsButton.Enabled = false;
            //downloadSteamCmdButton.Enabled = false;
            startButton.Enabled = false;
            updateButton.Enabled = false;
            stopButton.Enabled = true;
        }

        private enum ServerUiState
        {
            Idle,
            ServerRunning,
            Busy
        }

        private void UpdateButtonStates()
        {
            if (InvokeRequired)
            {
                Invoke(UpdateButtonStates);
                return;
            }

            switch (_uiState)
            {
                case ServerUiState.Idle:
                    saveSettingsButton.Enabled = true;
                    startButton.Enabled = true;
                    updateButton.Enabled = true;
                    stopButton.Enabled = false;
                    break;
                case ServerUiState.ServerRunning:
                    saveSettingsButton.Enabled = true;
                    startButton.Enabled = false;
                    updateButton.Enabled = true;
                    stopButton.Enabled = true;
                    break;
                case ServerUiState.Busy:
                    saveSettingsButton.Enabled = false;
                    startButton.Enabled = false;
                    updateButton.Enabled = false;
                    stopButton.Enabled = ServerManager.Instance.IsRunning;
                    break;
            }
        }


        private void SetButtonsEnabled(bool enabled)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<bool>(SetButtonsEnabled), enabled);
                return;
            }
            //downloadSteamCmdButton.Enabled = enabled;
            startButton.Enabled = enabled;
            updateButton.Enabled = enabled;
            stopButton.Enabled = enabled;
        }

        /// <summary>
        /// Sets whether the application is performing a long-running operation.
        /// Stop remains available if the server is running.
        /// Thread-safe.
        /// </summary>
        private void SetBusyState(bool busy)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<bool>(SetBusyState), busy);
                return;
            }
            _uiState = busy
                ? ServerUiState.Busy
                : (ServerManager.Instance != null && ServerManager.Instance.IsRunning
                    ? ServerUiState.ServerRunning
                    : ServerUiState.Idle);
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            UpdateButtonStates();
        }



        /// <summary>
        /// Starts the external server process using the configured settings. The
        /// process is launched with redirected standard output and error streams so
        /// that output can be streamed to the consoleTextBox.
        /// </summary>
        private async void StartServer()
        {
            DisableButtonsOnStart();
            SaveSettingsToFile();
            try
            {
                SaveManager.Instance.EnsureSaveGameExists();
                if (!ServerManager.Instance.IsServerInstalled())
                {
                    await SteamCmdManager.Instance.UpdateServerAsync();
                }
            }
            catch (Exception ex)
            {
                AppendConsole($"SaveGame preparation failed: {ex.Message}");
                _uiState = ServerUiState.Idle;
                UpdateButtonStates();
                return;
            }

            if (ServerManager.Instance.StartServer())
            {
                _uiState = ServerUiState.ServerRunning;
                UpdateButtonStates();
            }
            else
            {
                _uiState = ServerUiState.Idle;
                UpdateButtonStates();
            }
        }


        /// <summary>
        /// Stops the server process if it is currently running.
        /// </summary>
        private void StopServer()
        {
            ServerManager.Instance.StopServer();
            if (!_isUpdating)
            {
                _uiState = ServerUiState.Idle;
                UpdateButtonStates();
            }
        }

        /// <summary>
        /// Gracefully stops the Unreal / Satisfactory server using STDIN.
        /// Falls back to Kill() if it does not exit in time.
        /// </summary>
        private async Task StopServerAsync()
        {
            await ServerManager.Instance.StopServerAsync();
            _uiState = ServerUiState.Idle;
            UpdateButtonStates();
        }

        /// <summary>
        /// Runs a SteamCMD update. Stops the server before executing SteamCMD and
        /// streams all output to the console. The update command is a placeholder
        /// and should be tailored to the specific application and app ID.
        /// </summary>
        private async Task UpdateServerAsync()
        {
            // Mark update in progress and disable all buttons
            _isUpdating = true;
            _uiState = ServerUiState.Busy;
            UpdateButtonStates();


            if ( ServerManager.Instance.IsRunning && MessageBox.Show("Are you sure you want to update the server now, doing so will shutdown your server.", "Confirm Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                AppendConsole("Update cancelled by user.");
                _isUpdating = false;
                _uiState = ServerUiState.Idle;
                UpdateButtonStates();
                return;
            }

            // Stop any running server before updating
            StopServer();

            // Example SteamCMD command: login anonymously and quit. Replace with
            // actual commands to update your server (e.g., app_update <id> validate).
            string arguments = $"+force_install_dir \"{ServerManager.Instance.GetServerPath()}\" +login anonymous +app_update 3809400 validate +quit";
            await SteamCmdManager.Instance.InvokeSteamCmdAsync(arguments, logLine =>
            {
                AppendConsole(logLine);
            });

            AppendConsole("SteamCMD update completed.");

            // Update done; re-enable buttons
            _isUpdating = false;
            _uiState = ServerUiState.Idle;
            UpdateButtonStates();
        }

        /// <summary>
        /// Starts the auto-update timer. The timer runs asynchronously at the
        /// configured interval, checking for updates and restarting the server if needed.
        /// </summary>
        private void StartAutoUpdateTimer()
        {
            var timespan = TimeSpan.FromMinutes(30);
            // Dispose any existing timer to avoid multiple instances
            _autoUpdateTimer?.Dispose();
            _autoUpdateTimer = new Timer(async _ => await AutoUpdateAsync(), null, timespan, timespan);
            AppendConsole($"Auto-update timer started. Interval: {ConfigManager.Instance.GetConfig().UpdateInterval} minutes.");
        }

        /// <summary>
        /// Called by the auto-update timer. Performs an update via SteamCMD and
        /// restarts the server if it was running prior to the update.
        /// </summary>
        private async Task AutoUpdateAsync()
        {
            var _serverManager = ServerManager.Instance;

            // Use the renamed autoUpdateCheckBox instead of the default-named checkBox1
            if (autoUpdateCheckBox.Checked == false)
            {
                AppendConsole("Auto-update Disabled.");
                return;
            }

            // Prevent overlap
            if (_isUpdating)
                return;

            _isUpdating = true;

            try
            {
                bool serverWasRunning = _serverManager != null && _serverManager.IsRunning;

                await InvokeOnUiAsync(() =>
                {
                    AppendConsole("Auto-update check triggered.");
                    _uiState = ServerUiState.Busy;
                    UpdateButtonStates();
                });

                // 👉 here is where you check IF an update exists
                bool updateAvailable = await IsUpdateAvailableAsync();

                if (!updateAvailable)
                {
                    await InvokeOnUiAsync(() =>
                        AppendConsole("No update available.")
                    );
                    return;
                }

                await InvokeOnUiAsync(() =>
                    AppendConsole("Update available – applying...")
                );

                if (serverWasRunning)
                    await StopServerAsync();

                await UpdateServerAsync();

                if (serverWasRunning)
                    await InvokeOnUiAsync(StartServer);
            }
            finally
            {
                _isUpdating = false;

                _uiState = ServerUiState.Idle;
                UpdateButtonStates();
            }
        }

        private async Task<bool> IsUpdateAvailableAsync()
        {
            try
            {
                int localBuild = GetLocalBuildId();
                int remoteBuild = await GetRemoteBuildIdAsync();

                if (localBuild == 0 || remoteBuild == 0)
                {
                    AppendConsole("Unable to determine build IDs – skipping update.");
                    return false;
                }

                AppendConsole($"Local build: {localBuild}, Remote build: {remoteBuild}");

                return remoteBuild > localBuild;
            }
            catch (Exception ex)
            {
                AppendConsole($"Update check failed: {ex.Message}");
                return false;
            }
        }

        private int GetLocalBuildId()
        {
            try
            {
                var _steamAppId = SteamCmdManager.Instance.GetAppId();

                string manifestPath = Path.Combine(
                    ServerManager.Instance.GetServerPath(),
                    "steamapps",
                    $"appmanifest_{_steamAppId}.acf");

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

        private async Task<int> GetRemoteBuildIdAsync()
        {
            string arguments = $"+login anonymous +app_info_update 1 +app_info_print {SteamCmdManager.Instance.GetAppId()} +quit";

            int result = 0;
            string output = await SteamCmdManager.Instance.InvokeSteamCmdAsync(arguments, resultStr =>
            {
                foreach (string line in resultStr.Split('\n'))
                {
                    if (line.Contains("\"buildid\""))
                    {
                        string value = line.Split('"', StringSplitOptions.RemoveEmptyEntries)[3];
                        if (int.TryParse(value, out int build))
                        {
                            result = build;
                        }
                    }
                }
            });

            return result;
        }

        /// <summary>
        /// Executes an action on the UI thread asynchronously.
        /// Safe to call from timers, background threads, and async tasks.
        /// </summary>
        private Task InvokeOnUiAsync(Action action)
        {
            if (IsDisposed || !IsHandleCreated)
                return Task.CompletedTask;

            var tcs = new TaskCompletionSource();

            BeginInvoke(new Action(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }));

            return tcs.Task;
        }

        /// <summary>
        /// Appends a message to the consoleTextBox with a timestamp. Ensures that
        /// cross-thread calls are marshaled onto the UI thread when invoked from
        /// background operations.
        /// </summary>
        /// <param name="message">Message to append.</param>
        // Assumes you replaced consoleTextBox with a RichTextBox named consoleRichTextBox
        private void AppendConsole(string message)
        {
            if (rtbConsoleLog.InvokeRequired)
            {
                rtbConsoleLog.Invoke(new Action<string>(AppendConsole), message);
                return;
            }

            // Build the final line once
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";

            // Decide "level" based on content
            string lower = message?.ToLowerInvariant() ?? string.Empty;

            // Pick a color (tweak to taste)
            System.Drawing.Color color;
            string level;
            if (lower.Contains("error"))
            {
                level = "error";
                color = System.Drawing.Color.IndianRed;
            }
            else if (lower.Contains("warning"))
            {
                level = "warning";
                color = System.Drawing.Color.Goldenrod;
            }
            else
            {
                level = "info";
                color = System.Drawing.Color.BlueViolet;
            }

            // Append colored text without affecting existing content
            int start = rtbConsoleLog.TextLength;
            rtbConsoleLog.SelectionStart = start;
            rtbConsoleLog.SelectionLength = 0;
            rtbConsoleLog.SelectionColor = color;
            rtbConsoleLog.AppendText(line);

            // Reset selection color so future UI typing (or other appends) won't inherit it
            rtbConsoleLog.SelectionColor = rtbConsoleLog.ForeColor;

            // Scroll to end
            rtbConsoleLog.SelectionStart = rtbConsoleLog.TextLength;
            rtbConsoleLog.SelectionLength = 0;
            rtbConsoleLog.ScrollToCaret();

            Logger.Log($"[{level.ToUpperInvariant()}] {message}");
        }


        // Method removed; replaced by Logger.Log(message)

        #region Event Handlers

        private void SaveSettingsButton_Click(object? sender, EventArgs e)
        {
            SaveSettingsToFile();
        }

        private async void DownloadSteamCmdButton_Click(object? sender, EventArgs e)
        {
            await SteamCmdManager.Instance.CheckAndDownloadSteamCmdAsync();
        }

        private void StartButton_Click(object? sender, EventArgs e)
        {
            StartServer();
        }

        private async void StopButton_Click(object? sender, EventArgs e)
        {
            await StopServerAsync();
        }

        private async void UpdateButton_Click(object? sender, EventArgs e)
        {
            await UpdateServerAsync();
        }

        #endregion

        private void UpdateInterval_KeyPress(object sender, KeyPressEventArgs e)
        {
            // Allow control keys (Backspace, Delete, Ctrl+C/V, etc.)
            if (char.IsControl(e.KeyChar))
                return;

            // Allow digits only
            if (!char.IsDigit(e.KeyChar))
                e.Handled = true;
        }

        private async Task SendServerCommandAsync(string command)
        {
            await ServerManager.Instance.SendServerCommandAsync(command);
        }

        /// <summary>
        /// Handles click events for both the admin and player password set buttons.
        /// Invokes the asynchronous update routine to post the current password
        /// values and write the encrypted results to disk.
        /// </summary>
        private async void SetPasswordButton_Click(object? sender, EventArgs e)
        {
            await OnSetPasswordsAsync();
        }

        /// <summary>
        /// Reads the current values from the admin and player password text boxes and
        /// sends them to the StarRupture password API.  Any exceptions are logged
        /// to the console.  A message will be appended to the console on success.
        /// </summary>
        private async Task OnSetPasswordsAsync()
        {
            try
            {
                // Read masked password text from the UI controls. Null‑coalesce to empty strings.
                string adminPassword = adminPasswordTextBox?.Text ?? string.Empty;
                string playerPassword = playerPasswordTextBox?.Text ?? string.Empty;

                if (adminPassword.Length < 6)
                {
                    AppendConsole("Administrator password must be at least 6 characters long.");
                    return;
                }

                await UpdatePasswordsAsync(adminPassword, playerPassword);
                AppendConsole("Passwords updated successfully.");
            }
            catch (Exception ex)
            {
                AppendConsole($"Failed to update passwords: {ex.Message}");
            }
        }

        /// <summary>
        /// Calls the password encryption API using HTTP POST form data and persists
        /// the returned encrypted strings to files.  The administrator password
        /// is stored in <c>Password.json</c> and the player password in
        /// <c>ServerPassword.json</c> under the server path.
        /// </summary>
        /// <param name="adminPassword">Clear‑text administrator password entered by the user.</param>
        /// <param name="playerPassword">Clear‑text player password entered by the user.</param>
        private async Task UpdatePasswordsAsync(string adminPassword, string playerPassword)
        {
            using var httpClient = new HttpClient();
            using var content = new MultipartFormDataContent();
            // Always send both values, even if blank, to match the API contract.
            content.Add(new StringContent(adminPassword ?? string.Empty), "adminpassword");
            content.Add(new StringContent(playerPassword ?? string.Empty), "playerpassword");

            // Post the form data to the API endpoint.  Throws on non‑success status codes.
            using HttpResponseMessage response = await httpClient.PostAsync("https://starrupture.agngaming.com/passwords/", content);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync();

            // Parse the returned JSON and write each value to its respective file.
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("adminpassword", out JsonElement adminElement))
            {
                string encrypted = adminElement.GetString() ?? string.Empty;
                WritePasswordFile("Password.json", $"{{\"password\":\"{encrypted}\"}}");
            }
            if (document.RootElement.TryGetProperty("playerpassword", out JsonElement playerElement))
            {
                string encrypted = playerElement.GetString() ?? string.Empty;
                WritePasswordFile("PlayerPassword.json", $"{{\"password\":\"{encrypted}\"}}");
            }
        }

        /// <summary>
        /// Writes the supplied encrypted password value to a file in the server path.
        /// The directories are created if they do not already exist.
        /// </summary>
        /// <param name="fileName">The filename (e.g. Password.json or ServerPassword.json)</param>
        /// <param name="encryptedValue">The encrypted string returned by the API.</param>
        private void WritePasswordFile(string fileName, string encryptedValue)
        {
            try
            {
                if (string.IsNullOrEmpty(encryptedValue))
                    return;

                var _serverPath = ServerManager.Instance.GetServerPath();

                Directory.CreateDirectory(_serverPath);
                var fullPath = Path.Combine(_serverPath, fileName);
                File.WriteAllText(fullPath, encryptedValue);
            }
            catch (Exception ex)
            {
                AppendConsole($"Failed to write password file {fileName}: {ex.Message}");
            }
        }
    }
}