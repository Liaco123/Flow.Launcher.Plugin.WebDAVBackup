using System.Windows;
using System.Windows.Controls;

namespace Flow.Launcher.Plugin.WebDAVBackup;

public partial class SettingsControl : UserControl
{
    private readonly Settings _settings;
    private readonly Action _saveSettings;
    private bool _initialized;

    public SettingsControl(Settings settings, Action saveSettings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));

        InitializeComponent();

        ServerUrlTextBox.Text = _settings.ServerUrl;
        UsernameTextBox.Text = _settings.Username;
        PasswordInput.Password = _settings.Password;
        BackupFilenameTextBox.Text = string.IsNullOrWhiteSpace(_settings.BackupFilename)
            ? Settings.DefaultBackupFilename
            : _settings.BackupFilename;

        _initialized = true;
    }

    private void ServerUrlTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!CanUpdateSettings())
        {
            return;
        }

        _settings.ServerUrl = ServerUrlTextBox.Text.Trim();
        PersistSettings();
    }

    private void UsernameTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!CanUpdateSettings())
        {
            return;
        }

        _settings.Username = UsernameTextBox.Text;
        PersistSettings();
    }

    private void PasswordInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!CanUpdateSettings())
        {
            return;
        }

        // PasswordBox does not support two-way binding for Password, so sync manually.
        _settings.Password = PasswordInput.Password;
        PersistSettings();
    }

    private void BackupFilenameTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!CanUpdateSettings())
        {
            return;
        }

        _settings.BackupFilename = BackupFilenameTextBox.Text.Trim();
        PersistSettings();
    }

    private bool CanUpdateSettings()
    {
        return _initialized;
    }

    private void PersistSettings()
    {
        if (string.IsNullOrWhiteSpace(_settings.BackupFilename))
        {
            _settings.BackupFilename = Settings.DefaultBackupFilename;
            BackupFilenameTextBox.Text = _settings.BackupFilename;
            BackupFilenameTextBox.CaretIndex = BackupFilenameTextBox.Text.Length;
        }

        _saveSettings();
    }
}
