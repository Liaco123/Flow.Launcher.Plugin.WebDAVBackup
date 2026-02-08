namespace Flow.Launcher.Plugin.WebDAVBackup;

public class Settings
{
    public const string DefaultBackupFilename = "FlowBackup.zip";
    public static readonly string[] PreferredDefaultBackupDirectories = { "Settings", "Plugins", "Themes" };

    public string ServerUrl { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string BackupFilename { get; set; } = DefaultBackupFilename;

    public List<string> BackupDirectories { get; set; } = new();
}
