using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;
using TabbySSH.Models;
using TabbySSH.Services;

namespace TabbySSH.Services.Connections;

public delegate HostKeyVerificationResult HostKeyVerificationCallback(string host, int port, string keyAlgorithm, string fingerprint, bool isChanged);

public enum HostKeyVerificationResult
{
    Cancel,
    AcceptOnce,
    AcceptAndAdd
}

public class SshConnection : ITerminalConnection
{
    private const int DEFAULT_SSH_PORT = 22;
    private const int CONNECTION_TIMEOUT_SECONDS = 30;

    private SshClient? _sshClient;
    private ShellStream? _shellStream;
    private SshClient? _gatewayClient;
    private ForwardedPortLocal? _gatewayForward;
    private int _gatewayLocalPort;
    private readonly SshSessionConfiguration _config;
    private readonly SshSessionConfiguration? _gatewayConfig;
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly string _connectionName;
    private readonly KnownHostsManager? _knownHostsManager;
    private readonly HostKeyVerificationCallback? _hostKeyVerificationCallback;

    public bool IsConnected
    {
        get
        {
            try
            {
                return _sshClient?.IsConnected ?? false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }
    public string ConnectionName => _connectionName;

    public event EventHandler<string>? DataReceived;
    public event EventHandler<ConnectionClosedEventArgs>? ConnectionClosed;
    public event EventHandler<string>? ErrorOccurred;

    public SshConnection(SshSessionConfiguration config, KnownHostsManager? knownHostsManager = null, HostKeyVerificationCallback? hostKeyVerificationCallback = null, SshSessionConfiguration? gatewayConfig = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _gatewayConfig = gatewayConfig;
        _host = config.Host ?? throw new ArgumentNullException(nameof(config.Host));
        _port = config.Port > 0 ? config.Port : DEFAULT_SSH_PORT;
        _username = config.Username ?? throw new ArgumentNullException(nameof(config.Username));
        _password = config.Password ?? string.Empty;
        _connectionName = config.Name ?? throw new ArgumentNullException(nameof(config.Name));
        _knownHostsManager = knownHostsManager;
        _hostKeyVerificationCallback = hostKeyVerificationCallback;
    }

    public async Task<bool> ConnectAsync()
    {
        try
        {
            string targetHost = _host;
            int targetPort = _port;

            if (_gatewayConfig != null)
            {
                if (!await ConnectToGatewayAsync())
                {
                    return false;
                }
                targetHost = "localhost";
                targetPort = _gatewayLocalPort;
            }

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

            var connectionInfo = new ConnectionInfo(targetHost, targetPort, _username, authMethod)
            {
                Timeout = TimeSpan.FromSeconds(_config.ConnectionTimeout > 0 ? _config.ConnectionTimeout : CONNECTION_TIMEOUT_SECONDS)
            };

            if (_config.CompressionEnabled)
            {
                Debug.WriteLine("[SshConnection] Compression requested but not yet implemented in SSH.NET");
                System.Console.WriteLine("[SshConnection] Compression requested but not yet implemented in SSH.NET");
            }

            bool hostKeyAccepted = false;
            string? receivedFingerprint = null;
            string? receivedKeyAlgorithm = null;
            Exception? hostKeyException = null;

            _sshClient = new SshClient(connectionInfo);
            
            ((BaseClient)_sshClient).HostKeyReceived += (sender, e) =>
            {
                receivedFingerprint = string.Join("", e.FingerPrint.Select(b => b.ToString("x2")));
                receivedKeyAlgorithm = DetermineKeyAlgorithm(e.FingerPrint);

                if (_knownHostsManager != null)
                {
                    var knownHost = _knownHostsManager.GetKnownHost(_host, _port);
                    if (knownHost != null)
                    {
                        if (knownHost.Fingerprint == receivedFingerprint)
                        {
                            e.CanTrust = true;
                            hostKeyAccepted = true;
                            return;
                        }
                        else
                        {
                            e.CanTrust = false;
                            if (_hostKeyVerificationCallback != null)
                            {
                                var result = _hostKeyVerificationCallback(_host, _port, receivedKeyAlgorithm, receivedFingerprint, true);
                                if (result == HostKeyVerificationResult.AcceptAndAdd)
                                {
                                    _knownHostsManager.AddKnownHost(_host, _port, receivedFingerprint, receivedKeyAlgorithm);
                                    e.CanTrust = true;
                                    hostKeyAccepted = true;
                                }
                                else if (result == HostKeyVerificationResult.AcceptOnce)
                                {
                                    e.CanTrust = true;
                                    hostKeyAccepted = true;
                                }
                                else
                                {
                                    e.CanTrust = false;
                                    hostKeyAccepted = false;
                                }
                            }
                            else
                            {
                                e.CanTrust = false;
                                hostKeyAccepted = false;
                            }
                            return;
                        }
                    }
                }

                if (_hostKeyVerificationCallback != null)
                {
                    var result = _hostKeyVerificationCallback(_host, _port, receivedKeyAlgorithm, receivedFingerprint, false);
                    if (result == HostKeyVerificationResult.AcceptAndAdd)
                    {
                        _knownHostsManager?.AddKnownHost(_host, _port, receivedFingerprint, receivedKeyAlgorithm);
                        e.CanTrust = true;
                        hostKeyAccepted = true;
                    }
                    else if (result == HostKeyVerificationResult.AcceptOnce)
                    {
                        e.CanTrust = true;
                        hostKeyAccepted = true;
                    }
                    else
                    {
                        e.CanTrust = false;
                        hostKeyAccepted = false;
                    }
                }
                else
                {
                    e.CanTrust = false;
                    hostKeyAccepted = false;
                }
            };

            try
            {
                var timeout = TimeSpan.FromSeconds(_config.ConnectionTimeout > 0 ? _config.ConnectionTimeout : CONNECTION_TIMEOUT_SECONDS);
                var connectTask = Task.Run(() =>
                {
                    _sshClient.Connect();
                });
                
                if (await Task.WhenAny(connectTask, Task.Delay(timeout)) != connectTask)
                {
                    _sshClient?.Disconnect();
                    _sshClient?.Dispose();
                    _sshClient = null;
                    throw new SshConnectionException($"Connection timeout after {timeout.TotalSeconds} seconds");
                }
                
                await connectTask;
            }
            catch (SshConnectionException ex)
            {
                if (!hostKeyAccepted && receivedFingerprint != null)
                {
                    hostKeyException = ex;
                }
                else
                {
                    throw;
                }
            }

            if (!hostKeyAccepted && receivedFingerprint != null)
            {
                throw new SshConnectionException("Host key verification failed or was cancelled.", hostKeyException);
            }

            if (_sshClient != null && _sshClient.IsConnected)
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
        catch (SshConnectionException ex)
        {
            ErrorOccurred?.Invoke(this, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Connection error: {ex.Message}");
            return false;
        }
    }

    private static string DetermineKeyAlgorithm(byte[] fingerprint)
    {
        if (fingerprint == null || fingerprint.Length == 0)
        {
            return "unknown";
        }

        int length = fingerprint.Length;
        return length switch
        {
            16 => "ssh-rsa",
            20 => "ssh-rsa",
            32 => "ssh-ed25519",
            64 => "ssh-ed25519",
            _ => $"ssh-key-{length * 8}-bit"
        };
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
            _gatewayForward?.Stop();
            _gatewayClient?.Disconnect();
        });

        _shellStream?.Dispose();
        _sshClient?.Dispose();
        _gatewayForward?.Dispose();
        _gatewayClient?.Dispose();
        _shellStream = null;
        _sshClient = null;
        _gatewayForward = null;
        _gatewayClient = null;
    }

    public async Task WriteAsync(string data)
    {
        if (data == null)
        {
            return;
        }

        if (_shellStream == null || !IsConnected)
        {
            throw new InvalidOperationException("Connection is not established");
        }

        var escapedData = EscapeString(data);
        Debug.WriteLine($"[SshConnection] [SEND] ({data.Length} bytes): {escapedData}");
        System.Console.WriteLine($"[SshConnection] [SEND] ({data.Length} bytes): {escapedData}");

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

        if (_shellStream == null || !IsConnected)
        {
            throw new InvalidOperationException("Connection is not established");
        }

        var dataString = Encoding.UTF8.GetString(data);
        var escapedData = EscapeString(dataString);
        Debug.WriteLine($"[SshConnection] [SEND] ({data.Length} bytes): {escapedData}");
        System.Console.WriteLine($"[SshConnection] [SEND] ({data.Length} bytes): {escapedData}");

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
                                    uint colsUint = (uint)cols;
                                    uint rowsUint = (uint)rows;
                                    uint widthPixels = 0;
                                    uint heightPixels = 0;
                                    var result = sendMethod.Invoke(channel, new object[] { colsUint, rowsUint, widthPixels, heightPixels });
                                    return;
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                    break;
                    
                case "ANSI":
                    var ansiCommand2 = $"\x1B[8;{rows};{cols}t";
                    _shellStream.Write(ansiCommand2);
                    break;
                    
                case "STTY":
                    var lineEnding = _config.LineEnding ?? "\n";
                    var sttyCommand = $"stty cols {cols} rows {rows}{lineEnding}";
                    _shellStream.Write(sttyCommand);
                    break;
                    
                case "XTERM":
                    var xtermCommand = $"\x1B[18t";
                    _shellStream.Write(xtermCommand);
                    Task.Delay(50).ContinueWith(_ =>
                    {
                        if (_shellStream != null && IsConnected)
                        {
                            var resizeCommand = $"\x1B[8;{rows};{cols}t";
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
        var escapedData = EscapeString(data);
        Debug.WriteLine($"[SshConnection] [RECV] ({data.Length} bytes): {escapedData}");
        System.Console.WriteLine($"[SshConnection] [RECV] ({data.Length} bytes): {escapedData}");
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
        _gatewayForward?.Dispose();
        _gatewayClient?.Dispose();
    }

    private async Task<bool> ConnectToGatewayAsync()
    {
        if (_gatewayConfig == null)
        {
            return false;
        }

        try
        {
            AuthenticationMethod gatewayAuthMethod;
            if (_gatewayConfig.UsePasswordAuthentication)
            {
                gatewayAuthMethod = new PasswordAuthenticationMethod(_gatewayConfig.Username, _gatewayConfig.Password ?? string.Empty);
            }
            else if (!string.IsNullOrEmpty(_gatewayConfig.PrivateKeyPath) && File.Exists(_gatewayConfig.PrivateKeyPath))
            {
                var keyFile = new PrivateKeyFile(_gatewayConfig.PrivateKeyPath, _gatewayConfig.PrivateKeyPassphrase);
                gatewayAuthMethod = new PrivateKeyAuthenticationMethod(_gatewayConfig.Username, keyFile);
            }
            else
            {
                ErrorOccurred?.Invoke(this, "Gateway session authentication method not configured");
                return false;
            }

            var gatewayConnectionInfo = new ConnectionInfo(_gatewayConfig.Host, _gatewayConfig.Port > 0 ? _gatewayConfig.Port : DEFAULT_SSH_PORT, _gatewayConfig.Username, gatewayAuthMethod)
            {
                Timeout = TimeSpan.FromSeconds(_gatewayConfig.ConnectionTimeout > 0 ? _gatewayConfig.ConnectionTimeout : CONNECTION_TIMEOUT_SECONDS)
            };

            _gatewayClient = new SshClient(gatewayConnectionInfo);
            
            var gatewayTimeout = TimeSpan.FromSeconds(_gatewayConfig.ConnectionTimeout > 0 ? _gatewayConfig.ConnectionTimeout : CONNECTION_TIMEOUT_SECONDS);
            try
            {
                var gatewayConnectTask = Task.Run(() =>
                {
                    _gatewayClient.Connect();
                });
                
                if (await Task.WhenAny(gatewayConnectTask, Task.Delay(gatewayTimeout)) != gatewayConnectTask)
                {
                    _gatewayClient?.Disconnect();
                    _gatewayClient?.Dispose();
                    _gatewayClient = null;
                    ErrorOccurred?.Invoke(this, $"Gateway connection timeout after {gatewayTimeout.TotalSeconds} seconds");
                    return false;
                }
                
                await gatewayConnectTask;
            }
            catch (Exception ex)
            {
                _gatewayClient?.Disconnect();
                _gatewayClient?.Dispose();
                _gatewayClient = null;
                ErrorOccurred?.Invoke(this, $"Gateway connection failed: {ex.Message}");
                return false;
            }

            if (!_gatewayClient.IsConnected)
            {
                ErrorOccurred?.Invoke(this, "Failed to connect to gateway");
                return false;
            }

            var tcpListener = new TcpListener(IPAddress.Loopback, 0);
            tcpListener.Start();
            _gatewayLocalPort = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
            tcpListener.Stop();

            _gatewayForward = new ForwardedPortLocal("127.0.0.1", (uint)_gatewayLocalPort, _host, (uint)_port);
            _gatewayClient.AddForwardedPort(_gatewayForward);
            _gatewayForward.Start();

            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Gateway connection failed: {ex.Message}");
            _gatewayClient?.Dispose();
            _gatewayClient = null;
            return false;
        }
    }
}

