using System.Diagnostics;
using System.IO;
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
    private const string OperationStatusFileName = "operation-status.txt";
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

        ShowPendingOperationStatus();
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
        var scriptPath = Path.Combine(tempDirectory, "push-flow-data.ps1");

        try
        {
            var operationStatusPath = GetOperationStatusPath();
            Directory.CreateDirectory(Path.GetDirectoryName(operationStatusPath)!);
            var remoteFilename = BuildTimestampedBackupFilename(GetEffectiveBackupFilename(), DateTimeOffset.Now);
            var flowExecutablePath = Environment.ProcessPath ?? "Flow.Launcher.exe";
            var flowProcessName = Process.GetCurrentProcess().ProcessName;
            var scriptContent = BuildPushScript(
                flowRootPath,
                flowExecutablePath,
                flowProcessName,
                tempDirectory,
                operationStatusPath,
                _settings.ServerUrl,
                _settings.Username,
                _settings.Password,
                GetEffectiveBackupFilename(),
                remoteFilename,
                selectedDirectories,
                RemoteBackupRetentionCount,
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
                ShowMessage("WebDAV Backup", "Backup script failed to start.");
                SafeDeleteFile(scriptPath);
                SafeDeleteDirectory(tempDirectory);
                return;
            }

            ShowMessage("WebDAV Backup", $"Backup started. Flow Launcher will close and restart after upload: {remoteFilename}");
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
            var operationStatusPath = GetOperationStatusPath();
            Directory.CreateDirectory(Path.GetDirectoryName(operationStatusPath)!);

            // Restore must run out-of-process, otherwise FlowLauncher keeps files locked.
            var scriptContent = BuildRestoreScript(
                zipPath,
                flowRootPath,
                flowExecutablePath,
                GetCurrentPluginFolderName(),
                CurrentPluginId,
                flowProcessName,
                tempDirectory,
                operationStatusPath,
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

    private static string BuildPushScript(
        string flowRootPath,
        string flowExecutablePath,
        string flowProcessName,
        string tempDirectory,
        string operationStatusPath,
        string serverUrl,
        string username,
        string password,
        string backupFilename,
        string remoteFilename,
        IReadOnlyList<string> selectedDirectories,
        int retentionCount,
        int restartDelaySeconds)
    {
        static string EscapePowerShellLiteral(string value)
        {
            return value.Replace("'", "''");
        }

        static string ToPowerShellArray(IReadOnlyList<string> values)
        {
            return values.Count == 0
                ? "@()"
                : "@(" + string.Join(", ", values.Select(value => $"'{EscapePowerShellLiteral(value)}'")) + ")";
        }

        var escapedFlowRootPath = EscapePowerShellLiteral(flowRootPath);
        var escapedFlowPath = EscapePowerShellLiteral(flowExecutablePath);
        var escapedFlowProcessName = EscapePowerShellLiteral(flowProcessName);
        var escapedTempDirectory = EscapePowerShellLiteral(tempDirectory);
        var escapedOperationStatusPath = EscapePowerShellLiteral(operationStatusPath);
        var escapedServerUrl = EscapePowerShellLiteral(serverUrl);
        var escapedUsername = EscapePowerShellLiteral(username);
        var escapedPassword = EscapePowerShellLiteral(password);
        var escapedBackupFilename = EscapePowerShellLiteral(backupFilename);
        var escapedRemoteFilename = EscapePowerShellLiteral(remoteFilename);
        var selectedDirectoryArray = ToPowerShellArray(selectedDirectories);
        var safeRetentionCount = Math.Max(1, retentionCount);
        var safeRestartDelaySeconds = Math.Max(1, restartDelaySeconds);

        var lines = new[]
        {
            "$ErrorActionPreference = 'Stop'",
            $"$logPath = Join-Path '{escapedTempDirectory}' 'push_log.txt'",
            $"$statusPath = '{escapedOperationStatusPath}'",
            "$pushSucceeded = $false",
            "$operationError = $null",
            "Add-Content -Path $logPath -Value 'Starting backup push...'",
            string.Empty,
            "function Set-OperationStatus {",
            "    param([string] $Message)",
            "    $statusDirectory = Split-Path -Parent $statusPath",
            "    if (-not (Test-Path -LiteralPath $statusDirectory)) {",
            "        New-Item -ItemType Directory -Path $statusDirectory -Force | Out-Null",
            "    }",
            "    Set-Content -LiteralPath $statusPath -Value $Message -Encoding UTF8",
            "}",
            string.Empty,
            "function Get-BasicAuthHeader {",
            "    param([string] $Username, [string] $Password)",
            "    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Username + ':' + $Password)",
            "    return 'Basic ' + [Convert]::ToBase64String($bytes)",
            "}",
            string.Empty,
            "function Add-TrailingSlash {",
            "    param([string] $Value)",
            "    $trimmed = $Value.Trim()",
            "    if ($trimmed.EndsWith('/')) { return $trimmed }",
            "    return $trimmed + '/'",
            "}",
            string.Empty,
            "function Get-RemoteFolderUri {",
            "    param([string] $ServerUrl, [string] $FolderName)",
            "    $baseUri = [Uri](Add-TrailingSlash $ServerUrl)",
            "    return [Uri]::new($baseUri, [Uri]::EscapeDataString($FolderName.Trim('/')) + '/')",
            "}",
            string.Empty,
            "function Get-RemoteFileUri {",
            "    param([string] $ServerUrl, [string] $FolderName, [string] $FileName)",
            "    $baseUri = [Uri](Add-TrailingSlash $ServerUrl)",
            "    $folder = [Uri]::EscapeDataString($FolderName.Trim('/'))",
            "    $name = [Uri]::EscapeDataString([IO.Path]::GetFileName($FileName.Trim()))",
            "    return [Uri]::new($baseUri, $folder + '/' + $name)",
            "}",
            string.Empty,
            "function Invoke-WebDavRequest {",
            "    param([string] $Method, [Uri] $Uri, [hashtable] $Headers, [string] $InFile)",
            "    $request = [System.Net.HttpWebRequest]::Create($Uri)",
            "    $request.Method = $Method",
            "    foreach ($key in $Headers.Keys) {",
            "        $request.Headers[$key] = $Headers[$key]",
            "    }",
            "    if (-not [string]::IsNullOrWhiteSpace($InFile)) {",
            "        $request.ContentType = 'application/zip'",
            "        $fileStream = [IO.File]::OpenRead($InFile)",
            "        try {",
            "            $requestStream = $request.GetRequestStream()",
            "            try {",
            "                $fileStream.CopyTo($requestStream)",
            "            } finally {",
            "                $requestStream.Dispose()",
            "            }",
            "        } finally {",
            "            $fileStream.Dispose()",
            "        }",
            "    }",
            "    $response = $request.GetResponse()",
            "    try {",
            "        $reader = [IO.StreamReader]::new($response.GetResponseStream())",
            "        try {",
            "            $content = $reader.ReadToEnd()",
            "        } finally {",
            "            $reader.Dispose()",
            "        }",
            "        return [PSCustomObject]@{ StatusCode = [int]$response.StatusCode; Content = $content; ResponseUri = $response.ResponseUri }",
            "    } finally {",
            "        $response.Dispose()",
            "    }",
            "}",
            string.Empty,
            "function Test-IsManagedBackupFile {",
            "    param([string] $FileName, [string] $BaseFileName, [string] $Prefix, [string] $Extension)",
            "    return $FileName.Equals($BaseFileName, [System.StringComparison]::OrdinalIgnoreCase) -or ($FileName.StartsWith($Prefix, [System.StringComparison]::OrdinalIgnoreCase) -and $FileName.EndsWith($Extension, [System.StringComparison]::OrdinalIgnoreCase))",
            "}",
            string.Empty,
            "try {",
            "    Add-Content -Path $logPath -Value 'Stopping Flow.Launcher processes...'",
            $"    $flowProcessName = '{escapedFlowProcessName}'",
            "    $flowProcesses = Get-Process -Name $flowProcessName -ErrorAction SilentlyContinue",
            "    if ($flowProcesses) {",
            "        foreach ($p in $flowProcesses) {",
            "            Add-Content -Path $logPath -Value \"Stopping process ID $($p.Id)\"",
            "            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue",
            "        }",
            "        $timeout = 10",
            "        while ($timeout -gt 0) {",
            "            $running = Get-Process -Name $flowProcessName -ErrorAction SilentlyContinue",
            "            if (-not $running) { break }",
            "            Start-Sleep -Seconds 1",
            "            $timeout--",
            "        }",
            "    }",
            "    if (Get-Process -Name $flowProcessName -ErrorAction SilentlyContinue) {",
            "        throw 'Flow Launcher process did not exit before backup.'",
            "    }",
            string.Empty,
            $"    $flowRootPath = '{escapedFlowRootPath}'",
            $"    $selectedDirectories = {selectedDirectoryArray}",
            "    $sourceDirectories = @()",
            "    foreach ($directoryName in $selectedDirectories) {",
            "        $sourceDirectory = Join-Path $flowRootPath $directoryName",
            "        if (Test-Path -LiteralPath $sourceDirectory -PathType Container) {",
            "            $sourceDirectories += [PSCustomObject]@{ Name = $directoryName; Path = $sourceDirectory }",
            "        }",
            "    }",
            "    if ($sourceDirectories.Count -eq 0) {",
            "        throw 'Selected subfolders were not found under FlowLauncher.'",
            "    }",
            string.Empty,
            $"    $zipRoot = Join-Path '{escapedTempDirectory}' '{RemoteBackupFolderName}'",
            "    New-Item -ItemType Directory -Path $zipRoot -Force | Out-Null",
            $"    $zipPath = Join-Path $zipRoot '{escapedRemoteFilename}'",
            "    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }",
            "    Add-Content -Path $logPath -Value \"Creating backup zip: $zipPath\"",
            "    Add-Type -AssemblyName System.IO.Compression",
            "    Add-Type -AssemblyName System.IO.Compression.FileSystem",
            "    $zipStream = [IO.File]::Create($zipPath)",
            "    try {",
            "        $archive = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create)",
            "        try {",
            "            foreach ($source in $sourceDirectories) {",
            "                foreach ($file in [IO.Directory]::EnumerateFiles($source.Path, '*', [IO.SearchOption]::AllDirectories)) {",
            "                    $relativePath = $file.Substring($source.Path.Length).TrimStart('\\', '/')",
            "                    $entryPath = ($source.Name + '/' + $relativePath.Replace('\\', '/'))",
            "                    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, $entryPath, [IO.Compression.CompressionLevel]::Optimal) | Out-Null",
            "                }",
            "            }",
            "        } finally {",
            "            $archive.Dispose()",
            "        }",
            "    } finally {",
            "        $zipStream.Dispose()",
            "    }",
            string.Empty,
            $"    $serverUrl = '{escapedServerUrl}'",
            $"    $remoteFolderName = '{RemoteBackupFolderName}'",
            $"    $backupFilename = '{escapedBackupFilename}'",
            $"    $remoteFilename = '{escapedRemoteFilename}'",
            $"    $headers = @{{ Authorization = (Get-BasicAuthHeader '{escapedUsername}' '{escapedPassword}') }}",
            "    $remoteFolderUri = Get-RemoteFolderUri $serverUrl $remoteFolderName",
            "    Add-Content -Path $logPath -Value \"Ensuring remote folder: $remoteFolderUri\"",
            "    try {",
            "        Invoke-WebDavRequest -Method 'MKCOL' -Uri $remoteFolderUri -Headers $headers | Out-Null",
            "    } catch {",
            "        $statusCode = [int]$_.Exception.Response.StatusCode",
            "        if ($statusCode -ne 405) { throw }",
            "    }",
            string.Empty,
            "    $remoteFileUri = Get-RemoteFileUri $serverUrl $remoteFolderName $remoteFilename",
            "    Add-Content -Path $logPath -Value \"Uploading backup to: $remoteFileUri\"",
            "    Invoke-WebDavRequest -Method 'PUT' -Uri $remoteFileUri -Headers $headers -InFile $zipPath | Out-Null",
            string.Empty,
            "    Add-Content -Path $logPath -Value 'Pruning old remote backups...'",
            "    $propfindHeaders = $headers.Clone()",
            "    $propfindHeaders['Depth'] = '1'",
            "    $listResponse = Invoke-WebDavRequest -Method 'PROPFIND' -Uri $remoteFolderUri -Headers $propfindHeaders",
            "    [xml]$xml = $listResponse.Content",
            "    $baseFileName = [IO.Path]::GetFileName($backupFilename)",
            "    $extension = [IO.Path]::GetExtension($backupFilename)",
            "    if ([string]::IsNullOrWhiteSpace($extension)) { $extension = '.zip' }",
            "    $prefix = [IO.Path]::GetFileNameWithoutExtension($backupFilename) + '_'",
            "    $backups = @()",
            "    foreach ($node in $xml.SelectNodes(\"//*[local-name()='response']\")) {",
            "        $hrefNode = $node.SelectSingleNode(\".//*[local-name()='href']\")",
            "        if ($null -eq $hrefNode -or [string]::IsNullOrWhiteSpace($hrefNode.InnerText)) { continue }",
            "        $fileName = [Uri]::UnescapeDataString($hrefNode.InnerText.TrimEnd('/').Split('/')[-1])",
            "        if (-not (Test-IsManagedBackupFile $fileName $baseFileName $prefix $extension)) { continue }",
            "        $timestamp = [DateTimeOffset]::MinValue",
            "        if ($fileName.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) -and $fileName.EndsWith($extension, [System.StringComparison]::OrdinalIgnoreCase)) {",
            "            $timestampText = $fileName.Substring($prefix.Length, $fileName.Length - $prefix.Length - $extension.Length)",
            "            [DateTimeOffset]::TryParseExact($timestampText, 'yyyyMMdd_HHmmss', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeLocal, [ref]$timestamp) | Out-Null",
            "        }",
            "        $lastModifiedNode = $node.SelectSingleNode(\".//*[local-name()='getlastmodified']\")",
            "        if ($timestamp -eq [DateTimeOffset]::MinValue -and $null -ne $lastModifiedNode) {",
            "            [DateTimeOffset]::TryParse($lastModifiedNode.InnerText, [ref]$timestamp) | Out-Null",
            "        }",
            "        $backupUri = $null",
            "        if (-not [Uri]::TryCreate($hrefNode.InnerText, [UriKind]::Absolute, [ref]$backupUri)) {",
            "            $backupUri = [Uri]::new($remoteFolderUri, [Uri]::EscapeDataString($fileName))",
            "        }",
            "        $backups += [PSCustomObject]@{ FileName = $fileName; Uri = $backupUri; SortTime = $timestamp }",
            "    }",
            $"    foreach ($backup in ($backups | Sort-Object SortTime, FileName -Descending | Select-Object -Skip {safeRetentionCount})) {{",
            "        Add-Content -Path $logPath -Value \"Deleting old backup: $($backup.FileName)\"",
            "        try {",
            "            Invoke-WebDavRequest -Method 'DELETE' -Uri $backup.Uri -Headers $headers | Out-Null",
            "        } catch {",
            "            $statusCode = [int]$_.Exception.Response.StatusCode",
            "            if ($statusCode -ne 404) { throw }",
            "        }",
            "    }",
            "    $pushSucceeded = $true",
            "    Add-Content -Path $logPath -Value 'Backup push completed successfully.'",
            "}",
            "catch {",
            "    $operationError = $_",
            "    Add-Content -Path $logPath -Value \"ERROR: $_\"",
            "}",
            "finally {",
            "    if ($pushSucceeded) {",
            "        Set-OperationStatus \"Push completed successfully: $remoteFilename\"",
            "    } else {",
            "        Set-OperationStatus \"Push failed: $operationError\"",
            "    }",
            $"    Add-Content -Path $logPath -Value 'Waiting {safeRestartDelaySeconds} seconds before restart.'",
            $"    Start-Sleep -Seconds {safeRestartDelaySeconds}",
            $"    Add-Content -Path $logPath -Value \"Starting Flow.Launcher process from: '{escapedFlowPath}'\"",
            "    try {",
            $"        Start-Process -FilePath '{escapedFlowPath}'",
            "    } catch {",
            "        Add-Content -Path $logPath -Value \"ERROR: Failed to start Flow.Launcher: $_\"",
            "    }",
            "    if ($pushSucceeded) {",
            "        Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue",
            "    }",
            "    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue",
            "}"
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildRestoreScript(
        string zipPath,
        string flowRootPath,
        string flowExecutablePath,
        string currentPluginFolderName,
        string currentPluginId,
        string flowProcessName,
        string tempDirectory,
        string operationStatusPath,
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
        var escapedOperationStatusPath = EscapePowerShellLiteral(operationStatusPath);
        var safeRestartDelaySeconds = Math.Max(1, restartDelaySeconds);

        var lines = new[]
        {
            "$ErrorActionPreference = 'Stop'",
            $"$logPath = Join-Path '{escapedTempDirectory}' 'restore_log.txt'",
            $"$statusPath = '{escapedOperationStatusPath}'",
            "$restoreSucceeded = $false",
            "$operationError = $null",
            "Add-Content -Path $logPath -Value 'Starting restore...'",
            string.Empty,
            "function Set-OperationStatus {",
            "    param([string] $Message)",
            "    $statusDirectory = Split-Path -Parent $statusPath",
            "    if (-not (Test-Path -LiteralPath $statusDirectory)) {",
            "        New-Item -ItemType Directory -Path $statusDirectory -Force | Out-Null",
            "    }",
            "    Set-Content -LiteralPath $statusPath -Value $Message -Encoding UTF8",
            "}",
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
            "    $operationError = $_",
            "    Add-Content -Path $logPath -Value \"ERROR: $_\"",
            "}",
            "finally {",
            "    if ($restoreSucceeded) {",
            "        Set-OperationStatus 'Pull completed successfully.'",
            "    } else {",
            "        Set-OperationStatus \"Pull failed: $operationError\"",
            "    }",
            "    # 4. Restart Flow Launcher",
            "    if (-not $restoreSucceeded) {",
            "        Add-Content -Path $logPath -Value 'Restore failed; Flow Launcher will restart to show status.'",
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

    private void ShowPendingOperationStatus()
    {
        var statusPath = GetOperationStatusPath();
        if (!File.Exists(statusPath))
        {
            return;
        }

        try
        {
            var message = File.ReadAllText(statusPath).Trim();
            SafeDeleteFile(statusPath);
            if (!string.IsNullOrWhiteSpace(message))
            {
                ShowMessage("WebDAV Backup", message);
            }
        }
        catch (Exception ex)
        {
            LogException("Failed to read operation status.", ex);
        }
    }

    private string GetOperationStatusPath()
    {
        return Path.Combine(
            GetFlowRootPath(),
            "Settings",
            "Plugins",
            CurrentPluginId,
            OperationStatusFileName);
    }

    private void LogException(string message, Exception exception)
    {
        _context?.API.LogException(nameof(Main), message, exception, nameof(Main));
    }

    private sealed record RemoteBackupFile(string FileName, Uri Uri, DateTimeOffset SortTime, string DisplayName);
}
