using System.Windows;

namespace Terminalis.Views;

public partial class AboutDialog : Window
{
    public string ProgramName { get; }
    public string Description { get; }
    public string VersionText { get; }
    public string CreatorText { get; }
    public string CopyrightText { get; }
    public string LicenseText { get; }

    public AboutDialog()
    {
        InitializeComponent();
        
        ProgramName = VersionInfo.ProgramName;
        Description = VersionInfo.Description;
        VersionText = $"Version {VersionInfo.Version}";
        CreatorText = $"Created by {VersionInfo.Creator}";
        CopyrightText = VersionInfo.Copyright;
        LicenseText = "Copyright © 2025 Andreas Hagelberg\n\n" +
                      "Terminalis is made available under the MIT License.\n\n" +
                      "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the \"Software\"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:\n\n" +
                      "The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.\n\n" +
                      "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE." +
                      "\n\nThird-Party Software\n\n" +
                      "Terminalis includes or interacts with third-party libraries that have their own licenses.";
        
        DataContext = this;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}


