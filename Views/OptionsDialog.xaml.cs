using System.Windows;
using System.Windows.Controls;
using TabbySSH.Models;
using TabbySSH.Services;

namespace TabbySSH.Views;

public partial class OptionsDialog : Window
{
    public ApplicationSettings? Settings { get; private set; }
    private string _selectedTheme;
    private readonly string _originalTheme;

    public OptionsDialog(ApplicationSettings currentSettings)
    {
        InitializeComponent();
        RestoreSessionsCheckBox.IsChecked = currentSettings.RestoreActiveSessionsOnStartup;
        UseAccentColorForTitleBarCheckBox.IsChecked = currentSettings.UseAccentColorForTitleBar;
        _selectedTheme = currentSettings.Theme ?? "light";
        _originalTheme = _selectedTheme;
        
        var themes = App.ThemeManager.GetAvailableThemes();
        ThemeComboBox.ItemsSource = themes;
        ThemeComboBox.SelectedItem = _selectedTheme;
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is string themeName)
        {
            _selectedTheme = themeName;
            App.ThemeManager.LoadTheme(themeName);
        }
    }

    private void OKButton_Click(object sender, RoutedEventArgs e)
    {
        Settings = new ApplicationSettings
        {
            RestoreActiveSessionsOnStartup = RestoreSessionsCheckBox.IsChecked == true,
            Theme = _selectedTheme,
            UseAccentColorForTitleBar = UseAccentColorForTitleBarCheckBox.IsChecked == true
        };

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        App.ThemeManager.LoadTheme(_originalTheme);
        DialogResult = false;
        Close();
    }
}

