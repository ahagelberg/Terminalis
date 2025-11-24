namespace TabbySSH.Models;

public class PortForwardingRule
{
    public string Name { get; set; } = string.Empty;
    public bool IsLocal { get; set; } = true;
    public string LocalHost { get; set; } = "localhost";
    public int LocalPort { get; set; }
    public string RemoteHost { get; set; } = "localhost";
    public int RemotePort { get; set; }
    public bool Enabled { get; set; } = true;

    public string TypeDisplay => IsLocal ? "Local" : "Remote";
}

public class SshSessionConfiguration : SessionConfiguration
{
    private const int DEFAULT_SSH_PORT = 22;
    private const int DEFAULT_KEEPALIVE_INTERVAL = 30;
    private const int DEFAULT_CONNECTION_TIMEOUT = 30;

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = DEFAULT_SSH_PORT;
    public string Username { get; set; } = string.Empty;
    public string? Password { get; set; }
    public string? PrivateKeyPath { get; set; }
    public string? PrivateKeyPassphrase { get; set; }
    public bool UsePasswordAuthentication { get; set; } = true;

    public int KeepAliveInterval { get; set; } = DEFAULT_KEEPALIVE_INTERVAL;
    public int ConnectionTimeout { get; set; } = DEFAULT_CONNECTION_TIMEOUT;
    public bool CompressionEnabled { get; set; } = true;
    public bool X11ForwardingEnabled { get; set; } = false;
    public string BellNotification { get; set; } = "Line Flash";
    public List<PortForwardingRule> PortForwardingRules { get; set; } = new();
    
    public string FontFamily { get; set; } = "Consolas";
    public double FontSize { get; set; } = 12.0;
    public string? ForegroundColor { get; set; }
    public string? BackgroundColor { get; set; }
    public string TerminalResizeMethod { get; set; } = "SSH";
    public bool ResetScrollOnUserInput { get; set; } = true;
    public bool ResetScrollOnServerOutput { get; set; } = false;
    public string? ScreenSessionName { get; set; }

    public SshSessionConfiguration()
    {
        ConnectionType = "SSH";
    }
}

