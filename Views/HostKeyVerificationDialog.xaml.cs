using System.Windows;
using Terminalis.Services.Connections;

namespace Terminalis.Views;

public partial class HostKeyVerificationDialog : Window
{
    public HostKeyVerificationResult Result { get; private set; } = HostKeyVerificationResult.Cancel;
    public bool IsChanged { get; set; }

    public HostKeyVerificationDialog(string host, int port, string keyAlgorithm, string fingerprint, bool isChanged)
    {
        InitializeComponent();
        HostTextBlock.Text = $"{host}:{port}";
        KeyAlgorithmTextBlock.Text = keyAlgorithm;
        FingerprintTextBlock.Text = FormatFingerprint(fingerprint);
        IsChanged = isChanged;
        
        if (isChanged)
        {
            ChangedWarningTextBlock.Visibility = Visibility.Visible;
        }
    }

    private static string FormatFingerprint(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint))
        {
            return fingerprint;
        }

        var formatted = new System.Text.StringBuilder();
        for (int i = 0; i < fingerprint.Length; i += 2)
        {
            if (i > 0)
            {
                formatted.Append(":");
            }
            if (i + 2 <= fingerprint.Length)
            {
                formatted.Append(fingerprint.Substring(i, 2));
            }
            else
            {
                formatted.Append(fingerprint.Substring(i));
            }
        }
        return formatted.ToString();
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        Result = HostKeyVerificationResult.AcceptAndAdd;
        DialogResult = true;
        Close();
    }

    private void AcceptOnceButton_Click(object sender, RoutedEventArgs e)
    {
        Result = HostKeyVerificationResult.AcceptOnce;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = HostKeyVerificationResult.Cancel;
        DialogResult = false;
        Close();
    }
}

