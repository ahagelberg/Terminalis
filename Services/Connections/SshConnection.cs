using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using TabbySSH.Models;

namespace TabbySSH.Services.Connections;

public class SshConnection : ITerminalConnection
{
    private const int DEFAULT_SSH_PORT = 22;
    private const int CONNECTION_TIMEOUT_SECONDS = 30;

    private SshClient? _sshClient;
    private ShellStream? _shellStream;
    private readonly SshSessionConfiguration _config;
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly string _connectionName;

    public bool IsConnected => _sshClient?.IsConnected ?? false;
    public string ConnectionName => _connectionName;

    public event EventHandler<string>? DataReceived;
    public event EventHandler<ConnectionClosedEventArgs>? ConnectionClosed;
    public event EventHandler<string>? ErrorOccurred;

    public SshConnection(SshSessionConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _host = config.Host ?? throw new ArgumentNullException(nameof(config.Host));
        _port = config.Port > 0 ? config.Port : DEFAULT_SSH_PORT;
        _username = config.Username ?? throw new ArgumentNullException(nameof(config.Username));
        _password = config.Password ?? string.Empty;
        _connectionName = config.Name ?? throw new ArgumentNullException(nameof(config.Name));
    }

    public async Task<bool> ConnectAsync()
    {
        try
        {
            AuthenticationMethod authMethod;
            if (_config.UsePasswordAuthentication)
            {
                authMethod = new PasswordAuthenticationMethod(_username, _password);
            }
            else if (!string.IsNullOrEmpty(_config.PrivateKeyPath))
            {
                if (File.Exists(_config.PrivateKeyPath))
                {
                    var keyFile = new PrivateKeyFile(_config.PrivateKeyPath, _config.PrivateKeyPassphrase);
                    authMethod = new PrivateKeyAuthenticationMethod(_username, keyFile);
                }
                else
                {
                    ErrorOccurred?.Invoke(this, "Private key file not found");
                    return false;
                }
            }
            else
            {
                ErrorOccurred?.Invoke(this, "No authentication method specified");
                return false;
            }

            var connectionInfo = new ConnectionInfo(_host, _port, _username, authMethod)
            {
                Timeout = TimeSpan.FromSeconds(_config.ConnectionTimeout > 0 ? _config.ConnectionTimeout : CONNECTION_TIMEOUT_SECONDS)
            };

            if (_config.CompressionEnabled)
            {
                Debug.WriteLine("[SshConnection] Compression requested but not yet implemented in SSH.NET");
                System.Console.WriteLine("[SshConnection] Compression requested but not yet implemented in SSH.NET");
            }

            _sshClient = new SshClient(connectionInfo);
            await Task.Run(() => _sshClient.Connect());

            if (_sshClient.IsConnected)
            {
                if (_config.KeepAliveInterval > 0)
                {
                    _sshClient.KeepAliveInterval = TimeSpan.FromSeconds(_config.KeepAliveInterval);
                }

                var terminalType = "xterm";
                if (_config.X11ForwardingEnabled)
                {
                    terminalType = "xterm";
                }

                _shellStream = _sshClient.CreateShellStream(terminalType, 80, 24, 800, 600, 1024);
                _shellStream.DataReceived += OnShellStreamDataReceived;
                _shellStream.Closed += OnShellStreamClosed;

                await SetupPortForwarding();
                await SetupX11Forwarding();
                await SetupScreenSession();

                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex.Message);
            return false;
        }
    }

    private async Task SetupPortForwarding()
    {
        if (_config.PortForwardingRules == null || _config.PortForwardingRules.Count == 0)
        {
            return;
        }

        await Task.Run(() =>
        {
            foreach (var rule in _config.PortForwardingRules.Where(r => r.Enabled))
            {
                try
                {
                    if (rule.IsLocal)
                    {
                        var port = new ForwardedPortLocal(rule.LocalHost, (uint)rule.LocalPort, rule.RemoteHost, (uint)rule.RemotePort);
                        _sshClient?.AddForwardedPort(port);
                        port.Start();
                    }
                    else
                    {
                        var port = new ForwardedPortRemote(rule.RemoteHost, (uint)rule.RemotePort, rule.LocalHost, (uint)rule.LocalPort);
                        _sshClient?.AddForwardedPort(port);
                        port.Start();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SshConnection] Failed to setup port forwarding {rule.Name}: {ex.Message}");
                    System.Console.WriteLine($"[SshConnection] Failed to setup port forwarding {rule.Name}: {ex.Message}");
                }
            }
        });
    }

    private async Task SetupScreenSession()
    {
        if (string.IsNullOrWhiteSpace(_config.ScreenSessionName) || _sshClient == null || !_sshClient.IsConnected || _shellStream == null)
        {
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                var sessionName = _config.ScreenSessionName.Trim();
                
                var checkScreenCommand = _sshClient.CreateCommand("which screen 2>/dev/null || command -v screen 2>/dev/null || echo ''");
                checkScreenCommand.Execute();
                
                if (string.IsNullOrWhiteSpace(checkScreenCommand.Result.Trim()))
                {
                    ErrorOccurred?.Invoke(this, "Screen is not installed on the server. Please install screen or leave the screen session name empty.");
                    return;
                }
                
                var listSessionsCommand = _sshClient.CreateCommand("screen -list 2>/dev/null || echo ''");
                listSessionsCommand.Execute();
                var sessionListOutput = listSessionsCommand.Result ?? string.Empty;
                
                bool sessionExists = false;
                bool sessionAttached = false;
                
                if (!string.IsNullOrWhiteSpace(sessionListOutput))
                {
                    var lines = sessionListOutput.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.Contains(sessionName))
                        {
                            sessionExists = true;
                            sessionAttached = line.Contains("(Attached)") || line.Contains("(Multi");
                            break;
                        }
                    }
                }
                
                System.Threading.Thread.Sleep(500);
                
                var escapedSessionName = sessionName.Replace("'", "'\"'\"'");
                if (sessionExists)
                {
                    if (sessionAttached)
                    {
                        _shellStream.WriteLine($"screen -x '{escapedSessionName}'");
                    }
                    else
                    {
                        _shellStream.WriteLine($"screen -r '{escapedSessionName}'");
                    }
                }
                else
                {
                    _shellStream.WriteLine($"screen -S '{escapedSessionName}'");
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to setup screen session: {ex.Message}");
            }
        });
    }

    private async Task SetupX11Forwarding()
    {
        if (!_config.X11ForwardingEnabled || _sshClient == null || !_sshClient.IsConnected)
        {
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                var x11Display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0.0";
                var x11Host = "localhost";
                var x11Port = 6000;

                if (x11Display.StartsWith(":"))
                {
                    var parts = x11Display.Substring(1).Split('.');
                    if (parts.Length > 0 && int.TryParse(parts[0], out int displayNum))
                    {
                        x11Port = 6000 + displayNum;
                    }
                }
                else if (x11Display.Contains(":"))
                {
                    var parts = x11Display.Split(':');
                    if (parts.Length > 0)
                    {
                        x11Host = parts[0];
                        if (parts.Length > 1)
                        {
                            var displayParts = parts[1].Split('.');
                            if (displayParts.Length > 0 && int.TryParse(displayParts[0], out int displayNum))
                            {
                                x11Port = 6000 + displayNum;
                            }
                        }
                    }
                }

                var x11PortForward = new ForwardedPortLocal(x11Host, (uint)x11Port, "localhost", (uint)x11Port);
                _sshClient.AddForwardedPort(x11PortForward);
                x11PortForward.Start();

                Debug.WriteLine($"[SshConnection] X11 forwarding enabled: {x11Host}:{x11Port}");
                System.Console.WriteLine($"[SshConnection] X11 forwarding enabled: {x11Host}:{x11Port}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SshConnection] Failed to setup X11 forwarding: {ex.Message}");
                System.Console.WriteLine($"[SshConnection] Failed to setup X11 forwarding: {ex.Message}");
            }
        });
    }

    public async Task DisconnectAsync()
    {
        await Task.Run(() =>
        {
            _shellStream?.Close();
            _sshClient?.Disconnect();
        });

        _shellStream?.Dispose();
        _sshClient?.Dispose();
        _shellStream = null;
        _sshClient = null;
    }

    public async Task WriteAsync(string data)
    {
        if (data == null)
        {
            return;
        }

        System.Console.WriteLine($"[Client] Raw data sent: {EscapeString(data)}");

        if (_shellStream == null || !IsConnected)
        {
            throw new InvalidOperationException("Connection is not established");
        }

        await Task.Run(() =>
        {
            try
            {
                _shellStream.Write(data);
            }
            catch
            {
            }
        });
    }

    public async Task WriteAsync(byte[] data)
    {
        if (data == null)
        {
            return;
        }

        var dataString = Encoding.UTF8.GetString(data);
        System.Console.WriteLine($"[Client] Raw data sent (bytes): {EscapeString(dataString)}");

        if (_shellStream == null || !IsConnected)
        {
            throw new InvalidOperationException("Connection is not established");
        }

        await Task.Run(() =>
        {
            try
            {
                _shellStream.Write(data, 0, data.Length);
            }
            catch
            {
            }
        });
    }

    public void ResizeTerminal(int cols, int rows)
    {
        if (_shellStream == null || !IsConnected)
        {
            return;
        }

        var method = _config.TerminalResizeMethod ?? "SSH";
        
        try
        {
            switch (method)
            {
                case "SSH":
                    try
                    {
                        var shellStreamType = typeof(ShellStream);
                        var channelField = shellStreamType.GetField("_channel", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (channelField != null)
                        {
                            var channel = channelField.GetValue(_shellStream);
                            if (channel != null)
                            {
                                var channelType = channel.GetType();
                                var sendMethod = channelType.GetMethod("SendWindowChangeRequest", BindingFlags.Public | BindingFlags.Instance);
                                if (sendMethod != null)
                                {
                                    System.Console.WriteLine($"[Client] Raw data sent (resize SSH channel request): cols={cols}, rows={rows}");
                                    uint colsUint = (uint)cols;
                                    uint rowsUint = (uint)rows;
                                    uint widthPixels = 0;
                                    uint heightPixels = 0;
                                    var result = sendMethod.Invoke(channel, new object[] { colsUint, rowsUint, widthPixels, heightPixels });
                                    System.Console.WriteLine($"[Client] SendWindowChangeRequest returned: {result}");
                                    return;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Console.WriteLine($"[Client] Exception in SSH channel request: {ex.GetType().Name}: {ex.Message}");
                        System.Console.WriteLine($"[Client] Stack trace: {ex.StackTrace}");
                    }
                    System.Console.WriteLine($"[Client] ERROR: SSH channel request failed - no resize sent");
                    break;
                    
                case "ANSI":
                    var ansiCommand2 = $"\x1B[8;{rows};{cols}t";
                    System.Console.WriteLine($"[Client] Raw data sent (resize ANSI): {EscapeString(ansiCommand2)}");
                    _shellStream.Write(ansiCommand2);
                    break;
                    
                case "STTY":
                    var lineEnding = _config.LineEnding ?? "\n";
                    var sttyCommand = $"stty cols {cols} rows {rows}{lineEnding}";
                    System.Console.WriteLine($"[Client] Raw data sent (resize STTY): {EscapeString(sttyCommand)}");
                    _shellStream.Write(sttyCommand);
                    break;
                    
                case "XTERM":
                    var xtermCommand = $"\x1B[18t";
                    System.Console.WriteLine($"[Client] Raw data sent (resize XTERM query): {EscapeString(xtermCommand)}");
                    _shellStream.Write(xtermCommand);
                    Task.Delay(50).ContinueWith(_ =>
                    {
                        if (_shellStream != null && IsConnected)
                        {
                            var resizeCommand = $"\x1B[8;{rows};{cols}t";
                            System.Console.WriteLine($"[Client] Raw data sent (resize XTERM): {EscapeString(resizeCommand)}");
                            _shellStream.Write(resizeCommand);
                        }
                    });
                    break;
                    
                case "NONE":
                    break;
                    
                default:
                    Debug.WriteLine($"[SshConnection] Unknown resize method: {method}, using SSH");
                    try
                    {
                        var channelProperty = typeof(ShellStream).GetProperty("Channel", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (channelProperty != null)
                        {
                            var channel = channelProperty.GetValue(_shellStream);
                            if (channel != null)
                            {
                                var sendMethod = channel.GetType().GetMethod("SendWindowChangeRequest", BindingFlags.Public | BindingFlags.Instance);
                                if (sendMethod != null)
                                {
                                    System.Console.WriteLine($"[Client] Raw data sent (resize SSH channel request default): cols={cols}, rows={rows}");
                                    uint colsUint = (uint)cols;
                                    uint rowsUint = (uint)rows;
                                    uint widthPixels = 0;
                                    uint heightPixels = 0;
                                    sendMethod.Invoke(channel, new object[] { colsUint, rowsUint, widthPixels, heightPixels });
                                    return;
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                    var defaultCommand = $"\x1B[8;{rows};{cols}t";
                    System.Console.WriteLine($"[Client] Raw data sent (resize default): {EscapeString(defaultCommand)}");
                    _shellStream.Write(defaultCommand);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SshConnection] Failed to resize terminal using method {method}: {ex.Message}");
        }
    }

    private void OnShellStreamDataReceived(object? sender, ShellDataEventArgs e)
    {
        var data = Encoding.UTF8.GetString(e.Data);
        DataReceived?.Invoke(this, data);
    }

    private static string EscapeString(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            switch (c)
            {
                case '\x1B':
                    sb.Append("\\x1B");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\x07':
                    sb.Append("\\a");
                    break;
                default:
                    if (c >= 32 && c < 127)
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        sb.Append($"\\u{(int)c:X4}");
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private void OnShellStreamClosed(object? sender, EventArgs e)
    {
        bool isNormalExit = _sshClient?.IsConnected ?? false;
        ConnectionClosed?.Invoke(this, new ConnectionClosedEventArgs(isNormalExit));
    }

    public void Dispose()
    {
        _shellStream?.Dispose();
        _sshClient?.Dispose();
    }
}

