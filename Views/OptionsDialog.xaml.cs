using System.Windows;
using TabbySSH.Models;

namespace TabbySSH.Views;

public partial class OptionsDialog : Window
{
    public ApplicationSettings? Settings { get; private set; }

    public OptionsDialog(ApplicationSettings currentSettings)
    {
        InitializeComponent();
        RestoreSessionsCheckBox.IsChecked = currentSettings.RestoreActiveSessionsOnStartup;
    }

    private void OKButton_Click(object sender, RoutedEventArgs e)
    {
        Settings = new ApplicationSettings
        {
            RestoreActiveSessionsOnStartup = RestoreSessionsCheckBox.IsChecked == true
        };

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

