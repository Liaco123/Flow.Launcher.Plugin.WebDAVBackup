using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        return new SettingsControl(_settings, SaveSettings, GetAvailableFlowSubDirectories());
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
            SubTitle = "Download backup, stop Flow Launcher, restore files, then restart.",
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

            // Restore must run out-of-process, otherwise FlowLauncher keeps files locked.
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
            "$currentPluginFolderName = '" + escapedCurrentPluginFolderName + "'",
            "Get-ChildItem -LiteralPath $extractRoot -Directory | ForEach-Object {",
            "    $folderName = $_.Name",
            "    $source = $_.FullName",
            "    $target = Join-Path '" + escapedFlowRootPath + "' $folderName",
            string.Empty,
            "    if ($folderName -ieq 'Plugins') {",
            "        if (-not (Test-Path -LiteralPath $target)) {",
            "            New-Item -ItemType Directory -Path $target -Force | Out-Null",
            "        }",
            "        Get-ChildItem -LiteralPath $source -Directory | ForEach-Object {",
            "            if (-not [string]::IsNullOrWhiteSpace($currentPluginFolderName) -and $_.Name -ieq $currentPluginFolderName) {",
            "                return",
            "            }",
            "            $pluginTarget = Join-Path $target $_.Name",
            "            if (Test-Path -LiteralPath $pluginTarget) {",
            "                Remove-Item -LiteralPath $pluginTarget -Recurse -Force",
            "            }",
            "            Copy-Item -LiteralPath $_.FullName -Destination $pluginTarget -Recurse -Force",
            "        }",
            "        Get-ChildItem -LiteralPath $source -File | ForEach-Object {",
            "            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $target $_.Name) -Force",
            "        }",
            "        return",
            "    }",
            string.Empty,
            "    if (Test-Path -LiteralPath $target) {",
            "        Remove-Item -LiteralPath $target -Recurse -Force",
            "    }",
            "    Copy-Item -LiteralPath $source -Destination $target -Recurse -Force",
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
        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            var entryPath = $"{rootFolderName}/{relativePath.Replace('\\', '/')}";
            archive.CreateEntryFromFile(filePath, entryPath, CompressionLevel.Optimal);
        }
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

    private static IReadOnlyList<string> GetAvailableFlowSubDirectories()
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
