using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Windows.Controls;
using Flow.Launcher.Plugin;

namespace Flow.Launcher.Plugin.WebDAVBackup;

public class Main : IPlugin, ISettingProvider
{
    private const string FlowRootDirectoryName = "FlowLauncher";
    private const string IconRelativePath = "Images\\app.png";
    private const string RemoteBackupFolderName = "flowlauncher_backup";
    private static readonly string[] BackupSubDirectories = { "Settings", "Plugins", "Themes" };
    private static readonly HttpClient WebDavHttpClient = new();

    private PluginInitContext? _context;
    private Settings _settings = new();

    public void Init(PluginInitContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _settings = context.API.LoadSettingJsonStorage<Settings>() ?? new Settings();

        if (string.IsNullOrWhiteSpace(_settings.BackupFilename))
        {
            _settings.BackupFilename = Settings.DefaultBackupFilename;
            SaveSettings();
        }
    }

    public List<Result> Query(Query query)
    {
        var command = (query.Search ?? string.Empty).Trim().ToLowerInvariant();

        return command switch
        {
            "push" => new List<Result> { CreatePushResult() },
            "pull" => new List<Result> { CreatePullResult() },
            _ => new List<Result> { CreatePushResult(), CreatePullResult() }
        };
    }

    public Control CreateSettingPanel()
    {
        return new SettingsControl(_settings, SaveSettings);
    }

    private Result CreatePushResult()
    {
        return new Result
        {
            Title = "Push settings backup to WebDAV",
            SubTitle = "Zip Settings/Plugins/Themes and upload to flowlauncher_backup.",
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
            Title = "Pull settings backup from WebDAV",
            SubTitle = "Download and restore Settings/Plugins/Themes after restart.",
            IcoPath = GetIconPath(),
            Score = 99,
            Action = actionContext =>
            {
                _ = RunBackgroundOperationAsync(PullAsync);
                return true;
            }
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
        if (!HasAnyBackupDirectory(flowRootPath))
        {
            ShowMessage("WebDAV Backup", "No Settings/Plugins/Themes directory found in FlowLauncher data folder.");
            return;
        }

        var tempDirectory = CreateTempDirectory();
        var zipPath = GetLocalBackupZipPath(tempDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        try
        {
            // Build a single archive that contains Settings, Plugins and Themes.
            CreateBackupArchive(flowRootPath, zipPath);

            var remoteFolderUri = BuildRemoteFolderUri(_settings.ServerUrl, RemoteBackupFolderName);
            var ensureFolderResult = await EnsureRemoteBackupFolderAsync(remoteFolderUri).ConfigureAwait(false);
            if (!ensureFolderResult.Success)
            {
                ShowMessage("WebDAV Backup", ensureFolderResult.Error);
                return;
            }

            var remoteFileUri = BuildRemoteFileUri(_settings.ServerUrl, RemoteBackupFolderName, GetEffectiveBackupFilename());
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

    private async Task PullAsync()
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
            var remoteFileUri = BuildRemoteFileUri(_settings.ServerUrl, RemoteBackupFolderName, GetEffectiveBackupFilename());
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
            var flowExecutablePath = Process.GetCurrentProcess().MainModule?.FileName ?? "Flow.Launcher.exe";

            // Restore must happen out-of-process because files are locked while Flow Launcher is running.
            var scriptContent = BuildRestoreScript(
                zipPath,
                flowRootPath,
                flowExecutablePath,
                GetCurrentPluginFolderName());
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

            ShowMessage("WebDAV Backup", "Restore started. Flow Launcher will close and restart.");
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

    private static string BuildRestoreScript(
        string zipPath,
        string flowRootPath,
        string flowExecutablePath,
        string currentPluginFolderName)
    {
        static string EscapePowerShellLiteral(string value)
        {
            return value.Replace("'", "''");
        }

        var escapedZipPath = EscapePowerShellLiteral(zipPath);
        var escapedFlowRootPath = EscapePowerShellLiteral(flowRootPath);
        var escapedFlowPath = EscapePowerShellLiteral(flowExecutablePath);
        var escapedCurrentPluginFolderName = EscapePowerShellLiteral(currentPluginFolderName);

        var lines = new[]
        {
            "$ErrorActionPreference = 'Stop'",
            "Start-Sleep -Seconds 1",
            string.Empty,
            "$flowProcess = Get-Process -Name 'Flow.Launcher' -ErrorAction SilentlyContinue",
            "if ($flowProcess) {",
            "    $flowProcess | Stop-Process -Force",
            "}",
            string.Empty,
            "Start-Sleep -Seconds 2",
            string.Empty,
            "if (-not (Test-Path -LiteralPath '" + escapedFlowRootPath + "')) {",
            "    New-Item -ItemType Directory -Path '" + escapedFlowRootPath + "' -Force | Out-Null",
            "}",
            string.Empty,
            "$extractRoot = Join-Path (Split-Path -LiteralPath '" + escapedZipPath + "' -Parent) 'extract'",
            "if (Test-Path -LiteralPath $extractRoot) {",
            "    Remove-Item -LiteralPath $extractRoot -Recurse -Force",
            "}",
            "New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null",
            "Expand-Archive -LiteralPath '" + escapedZipPath + "' -DestinationPath $extractRoot -Force",
            string.Empty,
            "$settingsSource = Join-Path $extractRoot 'Settings'",
            "if (Test-Path -LiteralPath $settingsSource) {",
            "    $settingsTarget = Join-Path '" + escapedFlowRootPath + "' 'Settings'",
            "    if (Test-Path -LiteralPath $settingsTarget) {",
            "        Remove-Item -LiteralPath $settingsTarget -Recurse -Force",
            "    }",
            "    Copy-Item -LiteralPath $settingsSource -Destination $settingsTarget -Recurse -Force",
            "}",
            string.Empty,
            "$themesSource = Join-Path $extractRoot 'Themes'",
            "if (Test-Path -LiteralPath $themesSource) {",
            "    $themesTarget = Join-Path '" + escapedFlowRootPath + "' 'Themes'",
            "    if (Test-Path -LiteralPath $themesTarget) {",
            "        Remove-Item -LiteralPath $themesTarget -Recurse -Force",
            "    }",
            "    Copy-Item -LiteralPath $themesSource -Destination $themesTarget -Recurse -Force",
            "}",
            string.Empty,
            "$pluginsSource = Join-Path $extractRoot 'Plugins'",
            "if (Test-Path -LiteralPath $pluginsSource) {",
            "    $pluginsTarget = Join-Path '" + escapedFlowRootPath + "' 'Plugins'",
            "    if (-not (Test-Path -LiteralPath $pluginsTarget)) {",
            "        New-Item -ItemType Directory -Path $pluginsTarget -Force | Out-Null",
            "    }",
            "    Get-ChildItem -LiteralPath $pluginsSource -Directory | Where-Object { $_.Name -ne '" + escapedCurrentPluginFolderName + "' } | ForEach-Object {",
            "        $dest = Join-Path $pluginsTarget $_.Name",
            "        if (Test-Path -LiteralPath $dest) {",
            "            Remove-Item -LiteralPath $dest -Recurse -Force",
            "        }",
            "        Copy-Item -LiteralPath $_.FullName -Destination $dest -Recurse -Force",
            "    }",
            "    Get-ChildItem -LiteralPath $pluginsSource -File | ForEach-Object {",
            "        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $pluginsTarget $_.Name) -Force",
            "    }",
            "}",
            string.Empty,
            "Start-Sleep -Milliseconds 700",
            "Start-Process -FilePath '" + escapedFlowPath + "'",
            string.Empty,
            "if (Test-Path -LiteralPath $extractRoot) {",
            "    Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue",
            "}",
            "Remove-Item -LiteralPath '" + escapedZipPath + "' -Force -ErrorAction SilentlyContinue",
            "Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue"
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static void CreateBackupArchive(string flowRootPath, string zipPath)
    {
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var zipStream = File.Create(zipPath);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        foreach (var folderName in BackupSubDirectories)
        {
            var sourceDirectory = Path.Combine(flowRootPath, folderName);
            if (!Directory.Exists(sourceDirectory))
            {
                continue;
            }

            AddDirectoryToArchive(archive, sourceDirectory, folderName);
        }
    }

    private static void AddDirectoryToArchive(ZipArchive archive, string sourceDirectory, string rootFolderName)
    {
        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            var entryPath = $"{rootFolderName}/{relativePath.Replace('\\', '/')}";
            archive.CreateEntryFromFile(filePath, entryPath, CompressionLevel.Optimal);
        }
    }

    private static bool HasAnyBackupDirectory(string flowRootPath)
    {
        foreach (var folderName in BackupSubDirectories)
        {
            if (Directory.Exists(Path.Combine(flowRootPath, folderName)))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Flow.WebDavBackup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetFlowRootPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FlowRootDirectoryName);
    }

    private string GetLocalBackupZipPath(string tempDirectory)
    {
        // Keep local temp structure aligned with remote target: flowlauncher_backup/FlowBackup.zip
        return Path.Combine(tempDirectory, RemoteBackupFolderName, GetEffectiveBackupFilename());
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
}
