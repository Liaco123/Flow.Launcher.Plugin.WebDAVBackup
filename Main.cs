using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Windows.Controls;
using System.Xml.Linq;
using Flow.Launcher.Plugin;

namespace Flow.Launcher.Plugin.WebDAVBackup;

public class Main : IPlugin, ISettingProvider
{
    private const string FlowRootDirectoryName = "FlowLauncher";
    private const string IconRelativePath = "Images\\app.png";
    private const string RemoteBackupFolderName = "flowlauncher_backup";
    private const string CurrentPluginId = "c5995623-eb2a-467d-b5ff-f92b5a90992b";
    private const int RemoteBackupRetentionCount = 3;
    private const int RestartDelaySeconds = 5;
    private static readonly HttpClient WebDavHttpClient = new();

    private PluginInitContext? _context;
    private Settings _settings = new();

    public void Init(PluginInitContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _settings = context.API.LoadSettingJsonStorage<Settings>() ?? new Settings();
        _settings.BackupDirectories ??= new List<string>();

        var settingsChanged = false;

        if (string.IsNullOrWhiteSpace(_settings.BackupFilename))
        {
            _settings.BackupFilename = Settings.DefaultBackupFilename;
            settingsChanged = true;
        }

        var availableDirectories = GetAvailableFlowSubDirectories();
        var normalizedDirectories = NormalizeBackupDirectories(_settings.BackupDirectories, availableDirectories);
        if (normalizedDirectories.Count == 0 && availableDirectories.Count > 0)
        {
            normalizedDirectories = GetDefaultBackupDirectories(availableDirectories);
        }

        if (!Enumerable.SequenceEqual(_settings.BackupDirectories, normalizedDirectories, StringComparer.OrdinalIgnoreCase))
        {
            _settings.BackupDirectories = normalizedDirectories;
            settingsChanged = true;
        }

        if (settingsChanged)
        {
            SaveSettings();
        }
    }

    public List<Result> Query(Query query)
    {
        var command = NormalizeCommand(query.Search);

        if (command == "push")
        {
            return new List<Result> { CreatePushResult() };
        }

        if (command == "pull" || command.StartsWith("pull ", StringComparison.Ordinal))
        {
            return CreatePullResults();
        }

        return new List<Result> { CreatePushResult(), CreatePullResult() };
    }

    public Control CreateSettingPanel()
    {
        return new SettingsControl(_settings, SaveSettings, TestWebDavConnectionAsync, GetAvailableFlowSubDirectories());
    }

    private async Task<(bool Success, string Message)> TestWebDavConnectionAsync()
    {
        if (!ValidateSettings(out var validationError))
        {
            return (false, validationError);
        }

        try
        {
            var serverUri = new Uri(AppendTrailingSlash(_settings.ServerUrl), UriKind.Absolute);
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), serverUri);
            request.Headers.Authorization = CreateBasicAuthHeader(_settings.Username, _settings.Password);
            request.Headers.TryAddWithoutValidation("Depth", "0");

            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await WebDavHttpClient.SendAsync(request, cancellationTokenSource.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MultiStatus)
            {
                return (true, "Connection successful.");
            }

            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var message = string.IsNullOrWhiteSpace(responseBody)
                ? $"Connection failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                : $"Connection failed ({(int)response.StatusCode} {response.ReasonPhrase}): {responseBody}";
            return (false, message);
        }
        catch (TaskCanceledException)
        {
            return (false, "Connection test timed out after 15 seconds.");
        }
        catch (Exception ex)
        {
            LogException("WebDAV connection test failed.", ex);
            return (false, $"Connection test failed: {ex.Message}");
        }
    }

    private Result CreatePushResult()
    {
        return new Result
        {
            Title = "Push backup to WebDAV",
            SubTitle = "Zip selected FlowLauncher subfolders and upload to flowlauncher_backup.",
            IcoPath = GetIconPath(),
            Score = 100,
            Action = actionContext =>
            {
                _ = RunBackgroundOperationAsync(PushAsync);
                return true;
            }
        };
    }

    private Result CreatePullResult()
    {
        return new Result
        {
            Title = "Pull backup from WebDAV",
            SubTitle = "Type 'pull' to choose a timestamped backup.",
            IcoPath = GetIconPath(),
            Score = 99,
        };
    }

    private async Task RunBackgroundOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogException("Unhandled plugin operation exception.", ex);
            ShowMessage("WebDAV Backup", $"Operation failed: {ex.Message}");
        }
    }

    private async Task PushAsync()
    {
        if (!ValidateSettings(out var validationError))
        {
            ShowMessage("WebDAV Backup", validationError);
            return;
        }

        var flowRootPath = GetFlowRootPath();
        if (!Directory.Exists(flowRootPath))
        {
            ShowMessage("WebDAV Backup", $"FlowLauncher folder not found: {flowRootPath}");
            return;
        }

        var selectedDirectories = GetEffectiveBackupDirectories();
        if (selectedDirectories.Count == 0)
        {
            ShowMessage("WebDAV Backup", "No backup subfolder selected in plugin settings.");
            return;
        }

        var tempDirectory = CreateTempDirectory();
        var zipPath = GetLocalBackupZipPath(tempDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        try
        {
            var addedDirectoryCount = CreateBackupArchive(flowRootPath, zipPath, selectedDirectories);
            if (addedDirectoryCount == 0)
            {
                ShowMessage("WebDAV Backup", "Selected subfolders were not found under FlowLauncher.");
                return;
            }

            var remoteFolderUri = BuildRemoteFolderUri(_settings.ServerUrl, RemoteBackupFolderName);
            var ensureFolderResult = await EnsureRemoteBackupFolderAsync(remoteFolderUri).ConfigureAwait(false);
            if (!ensureFolderResult.Success)
            {
                ShowMessage("WebDAV Backup", ensureFolderResult.Error);
                return;
            }

            var remoteFilename = BuildTimestampedBackupFilename(GetEffectiveBackupFilename(), DateTimeOffset.Now);
            var remoteFileUri = BuildRemoteFileUri(_settings.ServerUrl, RemoteBackupFolderName, remoteFilename);
            using var fileStream = File.OpenRead(zipPath);
            using var content = new StreamContent(fileStream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

            using var request = new HttpRequestMessage(HttpMethod.Put, remoteFileUri)
            {
                Content = content
            };
            request.Headers.Authorization = CreateBasicAuthHeader(_settings.Username, _settings.Password);

            using var response = await WebDavHttpClient.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    await PruneRemoteBackupsAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogException("Backup uploaded, but old remote backup cleanup failed.", ex);
                    ShowMessage("WebDAV Backup", $"Backup uploaded successfully to {remoteFileUri}, but old backup cleanup failed: {ex.Message}");
                    return;
                }

                ShowMessage("WebDAV Backup", $"Backup uploaded successfully to {remoteFileUri}.");
                return;
            }

            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            ShowMessage("WebDAV Backup", $"Upload failed ({(int)response.StatusCode}): {responseBody}");
        }
        finally
        {
            SafeDeleteDirectory(tempDirectory);
        }
    }

    private List<Result> CreatePullResults()
    {
        if (!ValidateSettings(out var validationError))
        {
            return new List<Result> { CreateDisabledResult("Cannot pull backup", validationError) };
        }

        try
        {
            var backups = ListRemoteBackupsAsync().GetAwaiter().GetResult();
            if (backups.Count == 0)
            {
                return new List<Result>
                {
                    CreateDisabledResult("No WebDAV backups found", $"No matching {GetEffectiveBackupFilename()} backups under {RemoteBackupFolderName}.")
                };
            }

            return backups
                .Select((backup, index) => CreatePullResult(backup, 100 - index))
                .ToList();
        }
        catch (Exception ex)
        {
            LogException("Failed to list remote backups.", ex);
            return new List<Result> { CreateDisabledResult("Cannot list WebDAV backups", ex.Message) };
        }
    }

    private Result CreatePullResult(RemoteBackupFile backup, int score)
    {
        return new Result
        {
            Title = $"Pull backup: {backup.DisplayName}",
            SubTitle = $"Restore {backup.FileName}. Flow Launcher will close and restart.",
            IcoPath = GetIconPath(),
            Score = score,
            Action = actionContext =>
            {
                _ = RunBackgroundOperationAsync(() => PullAsync(backup.Uri, backup.FileName));
                return true;
            }
        };
    }

    private Result CreateDisabledResult(string title, string subTitle)
    {
        return new Result
        {
            Title = title,
            SubTitle = subTitle,
            IcoPath = GetIconPath(),
            Score = 1
        };
    }

    private async Task PullAsync(Uri remoteFileUri, string remoteFileName)
    {
        if (!ValidateSettings(out var validationError))
        {
            ShowMessage("WebDAV Backup", validationError);
            return;
        }

        var tempDirectory = CreateTempDirectory();
        var zipPath = GetLocalBackupZipPath(tempDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        var scriptPath = Path.Combine(tempDirectory, "restore-flow-data.ps1");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, remoteFileUri);
            request.Headers.Authorization = CreateBasicAuthHeader(_settings.Username, _settings.Password);

            using var response = await WebDavHttpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                ShowMessage("WebDAV Backup", $"Download failed ({(int)response.StatusCode}): {responseBody}");
                return;
            }

            await using (var remoteZipStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var zipFileStream = File.Create(zipPath))
            {
                await remoteZipStream.CopyToAsync(zipFileStream).ConfigureAwait(false);
            }

            var flowRootPath = GetFlowRootPath();
            var flowExecutablePath = Environment.ProcessPath ?? "Flow.Launcher.exe";
            var flowProcessName = Process.GetCurrentProcess().ProcessName;

            // Restore must run out-of-process, otherwise FlowLauncher keeps files locked.
            var scriptContent = BuildRestoreScript(
                zipPath,
                flowRootPath,
                flowExecutablePath,
                GetCurrentPluginFolderName(),
                CurrentPluginId,
                flowProcessName,
                tempDirectory,
                RestartDelaySeconds);
            await File.WriteAllTextAsync(scriptPath, scriptContent, new UTF8Encoding(false)).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                WorkingDirectory = tempDirectory,
                CreateNoWindow = true,
                UseShellExecute = false
            };

            var scriptProcess = Process.Start(startInfo);
            if (scriptProcess == null)
            {
                ShowMessage("WebDAV Backup", "Restore script failed to start.");
                return;
            }

            ShowMessage("WebDAV Backup", $"Restore started from {remoteFileName}. Flow Launcher will close and restart.");
            await Task.Delay(800).ConfigureAwait(false);
            Environment.Exit(0);
        }
        catch
        {
            SafeDeleteFile(scriptPath);
            SafeDeleteDirectory(tempDirectory);
            throw;
        }
    }

    private async Task<(bool Success, string Error)> EnsureRemoteBackupFolderAsync(Uri remoteFolderUri)
    {
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), remoteFolderUri);
        request.Headers.Authorization = CreateBasicAuthHeader(_settings.Username, _settings.Password);

        using var response = await WebDavHttpClient.SendAsync(request).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            return (true, string.Empty);
        }

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return (false, $"Cannot create/verify remote folder '{RemoteBackupFolderName}' ({(int)response.StatusCode}): {body}");
    }

    private async Task PruneRemoteBackupsAsync()
    {
        var backups = await ListRemoteBackupsAsync().ConfigureAwait(false);
        foreach (var backup in backups.Skip(RemoteBackupRetentionCount))
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, backup.Uri);
            request.Headers.Authorization = CreateBasicAuthHeader(_settings.Username, _settings.Password);
            using var response = await WebDavHttpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                LogException(
                    $"Failed to delete old remote backup '{backup.FileName}' ({(int)response.StatusCode}): {body}",
                    new InvalidOperationException(response.ReasonPhrase));
            }
        }
    }

    private async Task<List<RemoteBackupFile>> ListRemoteBackupsAsync()
    {
        var remoteFolderUri = BuildRemoteFolderUri(_settings.ServerUrl, RemoteBackupFolderName);
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), remoteFolderUri);
        request.Headers.Authorization = CreateBasicAuthHeader(_settings.Username, _settings.Password);
        request.Headers.TryAddWithoutValidation("Depth", "1");

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await WebDavHttpClient.SendAsync(request, cancellationTokenSource.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.MultiStatus)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Remote backup list failed ({(int)response.StatusCode}): {body}");
        }

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ParseRemoteBackups(responseBody, remoteFolderUri, GetEffectiveBackupFilename())
            .OrderByDescending(backup => backup.SortTime)
            .ThenByDescending(backup => backup.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void SaveSettings()
    {
        _context?.API.SaveSettingJsonStorage<Settings>();
    }

    private bool ValidateSettings(out string error)
    {
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl))
        {
            error = "Server URL is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_settings.Username))
        {
            error = "Username is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_settings.Password))
        {
            error = "Password is required.";
            return false;
        }

        if (!Uri.TryCreate(AppendTrailingSlash(_settings.ServerUrl), UriKind.Absolute, out _))
        {
            error = "Server URL is not valid.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private IReadOnlyList<string> GetEffectiveBackupDirectories()
    {
        var availableDirectories = GetAvailableFlowSubDirectories();
        var selectedDirectories = NormalizeBackupDirectories(_settings.BackupDirectories, availableDirectories);

        if (selectedDirectories.Count == 0 && availableDirectories.Count > 0)
        {
            return GetDefaultBackupDirectories(availableDirectories);
        }

        return selectedDirectories;
    }

    private static AuthenticationHeaderValue CreateBasicAuthHeader(string username, string password)
    {
        var bytes = Encoding.UTF8.GetBytes($"{username}:{password}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
    }

    private static Uri BuildRemoteFolderUri(string serverUrl, string folderName)
    {
        var normalizedServerUrl = AppendTrailingSlash(serverUrl);
        var baseUri = new Uri(normalizedServerUrl, UriKind.Absolute);
        var encodedFolder = Uri.EscapeDataString(folderName.Trim('/'));
        return new Uri(baseUri, $"{encodedFolder}/");
    }

    private static Uri BuildRemoteFileUri(string serverUrl, string folderName, string backupFilename)
    {
        var normalizedServerUrl = AppendTrailingSlash(serverUrl);
        var baseUri = new Uri(normalizedServerUrl, UriKind.Absolute);
        var encodedFolder = Uri.EscapeDataString(folderName.Trim('/'));
        var encodedFilename = Uri.EscapeDataString(Path.GetFileName(backupFilename.Trim()));
        return new Uri(baseUri, $"{encodedFolder}/{encodedFilename}");
    }

    private static string AppendTrailingSlash(string value)
    {
        var trimmed = value.Trim();
        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : $"{trimmed}/";
    }

    private static string NormalizeCommand(string? search)
    {
        var command = (search ?? string.Empty).Trim();
        if (command.StartsWith("wd:", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("wd：", StringComparison.OrdinalIgnoreCase))
        {
            command = command.Substring(3).Trim();
        }
        else if (command.StartsWith("wd ", StringComparison.OrdinalIgnoreCase))
        {
            command = command.Substring(3).Trim();
        }

        return command.TrimStart(':', '：').ToLowerInvariant();
    }

    private static string BuildRestoreScript(
        string zipPath,
        string flowRootPath,
        string flowExecutablePath,
        string currentPluginFolderName,
        string currentPluginId,
        string flowProcessName,
        string tempDirectory,
        int restartDelaySeconds)
    {
        static string EscapePowerShellLiteral(string value)
        {
            return value.Replace("'", "''");
        }

        var escapedZipPath = EscapePowerShellLiteral(zipPath);
        var escapedFlowRootPath = EscapePowerShellLiteral(flowRootPath);
        var escapedFlowPath = EscapePowerShellLiteral(flowExecutablePath);
        var escapedCurrentPluginFolderName = EscapePowerShellLiteral(currentPluginFolderName);
        var escapedCurrentPluginId = EscapePowerShellLiteral(currentPluginId);
        var escapedFlowProcessName = EscapePowerShellLiteral(flowProcessName);
        var escapedTempDirectory = EscapePowerShellLiteral(tempDirectory);
        var safeRestartDelaySeconds = Math.Max(1, restartDelaySeconds);

        var lines = new[]
        {
            "$ErrorActionPreference = 'Stop'",
            $"$logPath = Join-Path '{escapedTempDirectory}' 'restore_log.txt'",
            "$restoreSucceeded = $false",
            "Add-Content -Path $logPath -Value 'Starting restore...'",
            string.Empty,
            "try {",
            "    # 1. Stop Flow Launcher processes",
            "    Add-Content -Path $logPath -Value 'Stopping Flow.Launcher processes...'",
            $"    $flowProcessName = '{escapedFlowProcessName}'",
            "    $flowProcesses = Get-Process -Name $flowProcessName -ErrorAction SilentlyContinue",
            "    if ($flowProcesses) {",
            "        foreach ($p in $flowProcesses) {",
            "            Add-Content -Path $logPath -Value \"Stopping process ID $($p.Id)\"",
            "            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue",
            "        }",
            "        # Wait up to 5 seconds for processes to exit",
            "        $timeout = 5",
            "        while ($timeout -gt 0) {",
            "            $running = Get-Process -Name $flowProcessName -ErrorAction SilentlyContinue",
            "            if (-not $running) {",
            "                break",
            "            }",
            "            Start-Sleep -Seconds 1",
            "            $timeout--",
            "        }",
            "    }",
            "    $running = Get-Process -Name $flowProcessName -ErrorAction SilentlyContinue",
            "    if ($running) {",
            "        throw \"Flow Launcher process did not exit before restore.\"",
            "    }",
            string.Empty,
            "    # 2. Extract Archive",
            "    Add-Content -Path $logPath -Value 'Extracting backup zip...'",
            $"    $extractRoot = Join-Path '{escapedTempDirectory}' 'extract'",
            "    if (Test-Path -LiteralPath $extractRoot) {",
            "        Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue",
            "    }",
            "    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null",
            $"    Expand-Archive -LiteralPath '{escapedZipPath}' -DestinationPath $extractRoot -Force",
            string.Empty,
            "    # 3. Restore files",
            "    Add-Content -Path $logPath -Value 'Restoring files...'",
            $"    $currentPluginFolderName = '{escapedCurrentPluginFolderName}'",
            $"    $currentPluginId = '{escapedCurrentPluginId}'",
            "    $currentPluginConfigMarkers = @($currentPluginFolderName, $currentPluginId) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }",
            "    function Test-IsCurrentPluginConfigPath {",
            "        param([string] $Path)",
            "        if (-not $currentPluginConfigMarkers -or [string]::IsNullOrWhiteSpace($Path)) {",
            "            return $false",
            "        }",
            "        foreach ($marker in $currentPluginConfigMarkers) {",
            "            if ($Path.IndexOf($marker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {",
            "                return $true",
            "            }",
            "        }",
            "        return $false",
            "    }",
            "    function Wait-FileUnlocked {",
            "        param([string] $Path)",
            "        if (-not (Test-Path -LiteralPath $Path)) {",
            "            return",
            "        }",
            "        $retry = 20",
            "        while ($retry -gt 0) {",
            "            try {",
            "                $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)",
            "                $stream.Dispose()",
            "                return",
            "            } catch {",
            "                Add-Content -Path $logPath -Value \"Waiting for file lock to release: $Path\"",
            "                Start-Sleep -Milliseconds 500",
            "                $retry--",
            "            }",
            "        }",
            "        throw \"File is still locked after restore: $Path\"",
            "    }",
            "    Get-ChildItem -LiteralPath $extractRoot -Directory | ForEach-Object {",
            "        $folderName = $_.Name",
            "        $source = $_.FullName",
            $"        $target = Join-Path '{escapedFlowRootPath}' $folderName",
            string.Empty,
            "        Add-Content -Path $logPath -Value \"Processing folder: $folderName\"",
            string.Empty,
            "        if ($folderName -ieq 'Plugins') {",
            "            if (-not (Test-Path -LiteralPath $target)) {",
            "                New-Item -ItemType Directory -Path $target -Force | Out-Null",
            "            }",
            "            Get-ChildItem -LiteralPath $source -Directory | ForEach-Object {",
            "                if (-not [string]::IsNullOrWhiteSpace($currentPluginFolderName) -and $_.Name -ieq $currentPluginFolderName) {",
            "                    Add-Content -Path $logPath -Value \"Skipping current plugin folder: $($_.Name)\"",
            "                    return",
            "                }",
            "                $pluginTarget = Join-Path $target $_.Name",
            "                Add-Content -Path $logPath -Value \"Restoring plugin: $($_.Name) -> $pluginTarget\"",
            "                if (Test-Path -LiteralPath $pluginTarget) {",
            "                    # Retry delete if locked",
            "                    $retry = 3",
            "                    while ($retry -gt 0) {",
            "                        try {",
            "                            Remove-Item -LiteralPath $pluginTarget -Recurse -Force",
            "                            break",
            "                        } catch {",
            "                            Add-Content -Path $logPath -Value \"Warning: Failed to delete $pluginTarget, retrying...\"",
            "                            Start-Sleep -Seconds 1",
            "                            $retry--",
            "                        }",
            "                    }",
            "                }",
            "                Copy-Item -LiteralPath $_.FullName -Destination $pluginTarget -Recurse -Force",
            "            }",
            "            Get-ChildItem -LiteralPath $source -File | ForEach-Object {",
            "                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $target $_.Name) -Force",
            "            }",
            "            return",
            "        }",
            string.Empty,
            "        if ($folderName -ieq 'Settings') {",
            "            if (-not (Test-Path -LiteralPath $target)) {",
            "                New-Item -ItemType Directory -Path $target -Force | Out-Null",
            "            }",
            "            Get-ChildItem -LiteralPath $source -Recurse -File | ForEach-Object {",
            "                $relativePath = $_.FullName.Substring($source.Length).TrimStart('\\', '/')",
            "                if (Test-IsCurrentPluginConfigPath $relativePath) {",
            "                    Add-Content -Path $logPath -Value \"Skipping current plugin settings file: $relativePath\"",
            "                    return",
            "                }",
            "                $destination = Join-Path $target $relativePath",
            "                $destinationDirectory = Split-Path -Parent $destination",
            "                if (-not (Test-Path -LiteralPath $destinationDirectory)) {",
            "                    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null",
            "                }",
            "                Copy-Item -LiteralPath $_.FullName -Destination $destination -Force",
            "            }",
            "            return",
            "        }",
            string.Empty,
            "        if (Test-Path -LiteralPath $target) {",
            "            $retry = 3",
            "            while ($retry -gt 0) {",
            "                try {",
            "                    Remove-Item -LiteralPath $target -Recurse -Force",
            "                    break",
            "                } catch {",
            "                    Add-Content -Path $logPath -Value \"Warning: Failed to delete $target, retrying...\"",
            "                    Start-Sleep -Seconds 1",
            "                    $retry--",
            "                }",
            "            }",
            "        }",
            "        Copy-Item -LiteralPath $source -Destination $target -Recurse -Force",
            "    }",
            $"    $settingsJsonPath = Join-Path '{escapedFlowRootPath}' 'Settings\\Settings.json'",
            "    Wait-FileUnlocked $settingsJsonPath",
            "    Add-Content -Path $logPath -Value 'Restore files completed successfully.'",
            "    $restoreSucceeded = $true",
            "}",
            "catch {",
            "    Add-Content -Path $logPath -Value \"ERROR: $_\"",
            "}",
            "finally {",
            "    # 4. Restart Flow Launcher",
            "    if (-not $restoreSucceeded) {",
            "        Add-Content -Path $logPath -Value 'Restore failed; Flow Launcher restart skipped.'",
            "        return",
            "    }",
            $"    Add-Content -Path $logPath -Value 'Waiting {safeRestartDelaySeconds} seconds before restart.'",
            $"    Start-Sleep -Seconds {safeRestartDelaySeconds}",
            $"    Add-Content -Path $logPath -Value \"Starting Flow.Launcher process from: '{escapedFlowPath}'\"",
            "    try {",
            $"        Start-Process -FilePath '{escapedFlowPath}'",
            "        Start-Sleep -Seconds 2",
            "        $runningAfterStart = Get-Process -Name $flowProcessName -ErrorAction SilentlyContinue",
            "        if ($runningAfterStart) {",
            "            Add-Content -Path $logPath -Value 'Flow.Launcher process started.'",
            "        } else {",
            "            Add-Content -Path $logPath -Value 'Warning: Flow.Launcher process was not detected after start.'",
            "        }",
            "    }",
            "    catch {",
            "        Add-Content -Path $logPath -Value \"ERROR: Failed to start Flow.Launcher: $_\"",
            "    }",
            string.Empty,
            "    # 5. Cleanup temp files",
            "    if (Test-Path -LiteralPath $extractRoot) {",
            "        Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue",
            "    }",
            $"    Remove-Item -LiteralPath '{escapedZipPath}' -Force -ErrorAction SilentlyContinue",
            "    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue",
            "}"
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static int CreateBackupArchive(string flowRootPath, string zipPath, IReadOnlyList<string> selectedDirectories)
    {
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var zipStream = File.Create(zipPath);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        var addedDirectoryCount = 0;
        foreach (var folderName in selectedDirectories)
        {
            var normalizedFolderName = NormalizeDirectoryName(folderName);
            if (string.IsNullOrWhiteSpace(normalizedFolderName))
            {
                continue;
            }

            var sourceDirectory = Path.Combine(flowRootPath, normalizedFolderName);
            if (!Directory.Exists(sourceDirectory))
            {
                continue;
            }

            AddDirectoryToArchive(archive, sourceDirectory, normalizedFolderName);
            addedDirectoryCount++;
        }

        return addedDirectoryCount;
    }

    private static void AddDirectoryToArchive(ZipArchive archive, string sourceDirectory, string rootFolderName)
    {
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", enumerationOptions))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            var entryPath = $"{rootFolderName}/{relativePath.Replace('\\', '/')}";
            try
            {
                archive.CreateEntryFromFile(filePath, entryPath, CompressionLevel.Optimal);
            }
            catch (IOException)
            {
                // Flow Launcher keeps current log files open; skip locked files and keep backing up.
            }
            catch (UnauthorizedAccessException)
            {
                // Skip files that cannot be read by the current process.
            }
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Flow.WebDavBackup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private string GetFlowRootPath()
    {
        var pluginDirectory = _context?.CurrentPluginMetadata?.PluginDirectory;
        if (!string.IsNullOrWhiteSpace(pluginDirectory))
        {
            var pluginsDir = Path.GetDirectoryName(pluginDirectory);
            if (!string.IsNullOrWhiteSpace(pluginsDir))
            {
                var flowRoot = Path.GetDirectoryName(pluginsDir);
                if (!string.IsNullOrWhiteSpace(flowRoot))
                {
                    return flowRoot;
                }
            }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FlowRootDirectoryName);
    }

    private IReadOnlyList<string> GetAvailableFlowSubDirectories()
    {
        var flowRootPath = GetFlowRootPath();
        if (!Directory.Exists(flowRootPath))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateDirectories(flowRootPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    private static List<string> NormalizeBackupDirectories(IEnumerable<string>? sourceDirectories, IReadOnlyList<string> availableDirectories)
    {
        var result = new List<string>();
        if (sourceDirectories == null)
        {
            return result;
        }

        var availableSet = new HashSet<string>(availableDirectories, StringComparer.OrdinalIgnoreCase);
        var dedupeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceDirectory in sourceDirectories)
        {
            var normalizedName = NormalizeDirectoryName(sourceDirectory);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                continue;
            }

            if (!availableSet.Contains(normalizedName))
            {
                continue;
            }

            if (dedupeSet.Add(normalizedName))
            {
                result.Add(normalizedName);
            }
        }

        return result;
    }

    private static List<string> GetDefaultBackupDirectories(IReadOnlyList<string> availableDirectories)
    {
        var result = new List<string>();
        var availableSet = new HashSet<string>(availableDirectories, StringComparer.OrdinalIgnoreCase);

        foreach (var preferred in Settings.PreferredDefaultBackupDirectories)
        {
            if (availableSet.Contains(preferred))
            {
                result.Add(preferred);
            }
        }

        if (result.Count > 0)
        {
            return result;
        }

        return availableDirectories.ToList();
    }

    private static string NormalizeDirectoryName(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return string.Empty;
        }

        var normalized = folderName
            .Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Trim(Path.DirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return Path.GetFileName(normalized);
    }

    private string GetLocalBackupZipPath(string tempDirectory)
    {
        return Path.Combine(tempDirectory, RemoteBackupFolderName, GetEffectiveBackupFilename());
    }

    private static string BuildTimestampedBackupFilename(string backupFilename, DateTimeOffset timestamp)
    {
        var fileName = Path.GetFileName(backupFilename.Trim());
        var extension = Path.GetExtension(fileName);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".zip";
        }

        if (string.IsNullOrWhiteSpace(nameWithoutExtension))
        {
            nameWithoutExtension = Path.GetFileNameWithoutExtension(Settings.DefaultBackupFilename);
        }

        return $"{nameWithoutExtension}_{timestamp:yyyyMMdd_HHmmss}{extension}";
    }

    private static List<RemoteBackupFile> ParseRemoteBackups(string responseBody, Uri remoteFolderUri, string backupFilename)
    {
        var result = new List<RemoteBackupFile>();
        var expectedBaseFileName = Path.GetFileName(backupFilename);
        var expectedExtension = Path.GetExtension(backupFilename);
        var expectedPrefix = Path.GetFileNameWithoutExtension(backupFilename) + "_";

        if (string.IsNullOrWhiteSpace(expectedExtension))
        {
            expectedExtension = ".zip";
        }

        var document = XDocument.Parse(responseBody);
        foreach (var responseElement in document.Descendants().Where(element => element.Name.LocalName == "response"))
        {
            var hrefValue = responseElement
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "href")
                ?.Value;
            if (string.IsNullOrWhiteSpace(hrefValue))
            {
                continue;
            }

            var fileName = Uri.UnescapeDataString(hrefValue.TrimEnd('/').Split('/').Last());
            if (!IsManagedBackupFilename(fileName, expectedBaseFileName, expectedPrefix, expectedExtension))
            {
                continue;
            }

            var lastModifiedText = responseElement
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "getlastmodified")
                ?.Value;
            var lastModified = DateTimeOffset.TryParse(lastModifiedText, out var parsedLastModified)
                ? parsedLastModified
                : DateTimeOffset.MinValue;
            var timestamp = TryParseBackupTimestamp(fileName, expectedPrefix, expectedExtension) ?? lastModified;
            var uri = Uri.TryCreate(hrefValue, UriKind.Absolute, out var absoluteUri)
                ? absoluteUri
                : new Uri(remoteFolderUri, Uri.EscapeDataString(fileName));

            result.Add(new RemoteBackupFile(fileName, uri, timestamp, FormatBackupDisplayName(fileName, timestamp)));
        }

        return result;
    }

    private static bool IsManagedBackupFilename(
        string fileName,
        string expectedBaseFileName,
        string expectedPrefix,
        string expectedExtension)
    {
        return fileName.Equals(expectedBaseFileName, StringComparison.OrdinalIgnoreCase)
            || (fileName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase));
    }

    private static DateTimeOffset? TryParseBackupTimestamp(string fileName, string expectedPrefix, string expectedExtension)
    {
        if (!fileName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var timestampText = fileName.Substring(expectedPrefix.Length, fileName.Length - expectedPrefix.Length - expectedExtension.Length);
        return DateTimeOffset.TryParseExact(
            timestampText,
            "yyyyMMdd_HHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal,
            out var timestamp)
            ? timestamp
            : null;
    }

    private static string FormatBackupDisplayName(string fileName, DateTimeOffset timestamp)
    {
        return timestamp == DateTimeOffset.MinValue
            ? fileName
            : $"{timestamp:yyyy-MM-dd HH:mm:ss} ({fileName})";
    }

    private string GetEffectiveBackupFilename()
    {
        var backupFilename = _settings.BackupFilename.Trim();
        if (string.IsNullOrWhiteSpace(backupFilename))
        {
            backupFilename = Settings.DefaultBackupFilename;
        }

        return Path.GetFileName(backupFilename);
    }

    private string GetIconPath()
    {
        var pluginDirectory = _context?.CurrentPluginMetadata?.PluginDirectory;
        return string.IsNullOrWhiteSpace(pluginDirectory)
            ? IconRelativePath
            : Path.Combine(pluginDirectory, IconRelativePath);
    }

    private string GetCurrentPluginFolderName()
    {
        var pluginDirectory = _context?.CurrentPluginMetadata?.PluginDirectory;
        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            return string.Empty;
        }

        return new DirectoryInfo(pluginDirectory).Name;
    }

    private static void SafeDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private static void SafeDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, true);
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private void ShowMessage(string title, string subTitle)
    {
        _context?.API.ShowMsg(title, subTitle, GetIconPath());
    }

    private void LogException(string message, Exception exception)
    {
        _context?.API.LogException(nameof(Main), message, exception, nameof(Main));
    }

    private sealed record RemoteBackupFile(string FileName, Uri Uri, DateTimeOffset SortTime, string DisplayName);
}
