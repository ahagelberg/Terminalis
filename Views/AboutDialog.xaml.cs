using System.Windows;

namespace TabbySSH.Views;

public partial class AboutDialog : Window
{
    public string ProgramName { get; }
    public string Description { get; }
    public string VersionText { get; }
    public string CreatorText { get; }
    public string CopyrightText { get; }

    public AboutDialog()
    {
        InitializeComponent();
        
        ProgramName = VersionInfo.ProgramName;
        Description = VersionInfo.Description;
        VersionText = $"Version {VersionInfo.Version}";
        CreatorText = $"Created by {VersionInfo.Creator}";
        CopyrightText = VersionInfo.Copyright;
        
        DataContext = this;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}


