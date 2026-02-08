namespace Flow.Launcher.Plugin.WebDAVBackup;

public class Settings
{
    public const string DefaultBackupFilename = "FlowBackup.zip";

    public string ServerUrl { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string BackupFilename { get; set; } = DefaultBackupFilename;
}
