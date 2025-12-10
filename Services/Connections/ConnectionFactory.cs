using TabbySSH.Models;
using TabbySSH.Services;

namespace TabbySSH.Services.Connections;

public static class ConnectionFactory
{
    public static ITerminalConnection CreateConnection(SessionConfiguration config, KnownHostsManager? knownHostsManager = null, HostKeyVerificationCallback? hostKeyVerificationCallback = null, Services.SessionManager? sessionManager = null)
    {
        return config switch
        {
            SshSessionConfiguration sshConfig => CreateSshConnection(sshConfig, knownHostsManager, hostKeyVerificationCallback, sessionManager),
            _ => throw new NotSupportedException($"Connection type '{config.ConnectionType}' is not supported")
        };
    }

    private static ITerminalConnection CreateSshConnection(SshSessionConfiguration config, KnownHostsManager? knownHostsManager, HostKeyVerificationCallback? hostKeyVerificationCallback, Services.SessionManager? sessionManager)
    {
        if (string.IsNullOrWhiteSpace(config.Host))
        {
            throw new ArgumentException("Host is required for SSH connection", nameof(config));
        }

        if (string.IsNullOrWhiteSpace(config.Username))
        {
            throw new ArgumentException("Username is required for SSH connection", nameof(config));
        }

        if (config.UsePasswordAuthentication)
        {
            if (string.IsNullOrWhiteSpace(config.Password))
            {
                throw new ArgumentException("Password is required for password authentication", nameof(config));
            }
        }
        else if (string.IsNullOrWhiteSpace(config.PrivateKeyPath))
        {
            throw new ArgumentException("Private key path is required for key authentication", nameof(config));
        }

        SshSessionConfiguration? gatewayConfig = null;
        if (!string.IsNullOrWhiteSpace(config.GatewaySessionId) && sessionManager != null)
        {
            var gatewaySession = sessionManager.GetSession(config.GatewaySessionId);
            if (gatewaySession is SshSessionConfiguration sshGateway)
            {
                gatewayConfig = sshGateway;
            }
        }

        return new SshConnection(config, knownHostsManager, hostKeyVerificationCallback, gatewayConfig);
    }
}

