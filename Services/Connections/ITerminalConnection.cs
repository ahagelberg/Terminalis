namespace Terminalis.Services.Connections;

public interface ITerminalConnection : IDisposable
{
    bool IsConnected { get; }
    string ConnectionName { get; }
    event EventHandler<string>? DataReceived;
    event EventHandler<ConnectionClosedEventArgs>? ConnectionClosed;
    event EventHandler<string>? ErrorOccurred;

    Task<bool> ConnectAsync();
    Task DisconnectAsync();
    Task WriteAsync(string data);
    Task WriteAsync(byte[] data);
}

