using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Flow.Launcher.Plugin.WebDAVBackup;

public partial class SettingsControl : UserControl
{
    private readonly Settings _settings;
    private readonly Action _saveSettings;
    private readonly IReadOnlyList<string> _availableBackupDirectories;
    private readonly DispatcherTimer _saveDebounceTimer;

    private bool _initialized;
    private bool _isDirty;
    private bool _isSyncingPassword;

    public SettingsControl(Settings settings, Action saveSettings, IReadOnlyList<string> availableBackupDirectories)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
        _availableBackupDirectories = (availableBackupDirectories ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _saveDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _saveDebounceTimer.Tick += SaveDebounceTimer_OnTick;

        InitializeComponent();
        Unloaded += SettingsControl_OnUnloaded;

        ServerUrlTextBox.Text = _settings.ServerUrl;
        UsernameTextBox.Text = _settings.Username;
        PasswordInput.Password = _settings.Password;
        PasswordVisibleTextBox.Text = _settings.Password;
        BackupFilenameTextBox.Text = string.IsNullOrWhiteSpace(_settings.BackupFilename)
            ? Settings.DefaultBackupFilename
            : _settings.BackupFilename;

        LoadBackupDirectoryOptions();
        ApplyPasswordVisibility();

        _initialized = true;
    }

    private void ServerUrlTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!CanUpdateSettings())
        {
            return;
        }

        _settings.ServerUrl = ServerUrlTextBox.Text.Trim();
        MarkDirty();
    }

    private void UsernameTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!CanUpdateSettings())
        {
            return;
        }

        _settings.Username = UsernameTextBox.Text;
        MarkDirty();
    }

    private void PasswordInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!CanUpdateSettings() || _isSyncingPassword)
        {
            return;
        }

        _isSyncingPassword = true;
        PasswordVisibleTextBox.Text = PasswordInput.Password;
        _isSyncingPassword = false;

        _settings.Password = PasswordInput.Password;
        MarkDirty();
    }

    private void PasswordVisibleTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!CanUpdateSettings() || _isSyncingPassword)
        {
            return;
        }

        _isSyncingPassword = true;
        PasswordInput.Password = PasswordVisibleTextBox.Text;
        _isSyncingPassword = false;

        _settings.Password = PasswordVisibleTextBox.Text;
        MarkDirty();
    }

    private void ShowPasswordCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        ApplyPasswordVisibility();
    }

    private void BackupFilenameTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!CanUpdateSettings())
        {
            return;
        }

        _settings.BackupFilename = BackupFilenameTextBox.Text.Trim();
        MarkDirty();
    }

    private void BackupDirectoryCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!CanUpdateSettings())
        {
            return;
        }

        _settings.BackupDirectories = CollectSelectedBackupDirectories();
        MarkDirty();
    }

    private void ApplyPasswordVisibility()
    {
        var showPassword = ShowPasswordCheckBox.IsChecked == true;

        _isSyncingPassword = true;
        if (showPassword)
        {
            PasswordVisibleTextBox.Text = PasswordInput.Password;
            PasswordVisibleTextBox.Visibility = Visibility.Visible;
            PasswordInput.Visibility = Visibility.Collapsed;
        }
        else
        {
            PasswordInput.Password = PasswordVisibleTextBox.Text;
            PasswordInput.Visibility = Visibility.Visible;
            PasswordVisibleTextBox.Visibility = Visibility.Collapsed;
        }
        _isSyncingPassword = false;
    }

    private void LoadBackupDirectoryOptions()
    {
        BackupDirectoriesPanel.Children.Clear();

        if (_availableBackupDirectories.Count == 0)
        {
            BackupDirectoriesPanel.Children.Add(new TextBlock
            {
                Text = "No FlowLauncher subfolders found.",
                Foreground = System.Windows.Media.Brushes.Gray
            });
            return;
        }

        var selected = new HashSet<string>(_settings.BackupDirectories ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var directoryName in _availableBackupDirectories)
        {
            var checkbox = new CheckBox
            {
                Content = directoryName,
                IsChecked = selected.Contains(directoryName),
                Margin = new Thickness(0, 2, 0, 2)
            };
            checkbox.Checked += BackupDirectoryCheckBox_OnChanged;
            checkbox.Unchecked += BackupDirectoryCheckBox_OnChanged;

            BackupDirectoriesPanel.Children.Add(checkbox);
        }
    }

    private List<string> CollectSelectedBackupDirectories()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var child in BackupDirectoriesPanel.Children)
        {
            if (child is not CheckBox checkbox || checkbox.IsChecked != true)
            {
                continue;
            }

            var name = checkbox.Content?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private bool CanUpdateSettings()
    {
        return _initialized;
    }

    private void MarkDirty()
    {
        _isDirty = true;
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    private void SaveDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        PersistSettingsNow();
    }

    private void PersistSettingsNow()
    {
        _saveDebounceTimer.Stop();

        if (!_isDirty)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.BackupFilename))
        {
            _settings.BackupFilename = Settings.DefaultBackupFilename;

            // Keep UI and model consistent after normalization.
            BackupFilenameTextBox.Text = _settings.BackupFilename;
            BackupFilenameTextBox.CaretIndex = BackupFilenameTextBox.Text.Length;
        }

        _settings.BackupDirectories = CollectSelectedBackupDirectories();
        _saveSettings();
        _isDirty = false;
    }

    private void SettingsControl_OnUnloaded(object sender, RoutedEventArgs e)
    {
        PersistSettingsNow();
    }
}
