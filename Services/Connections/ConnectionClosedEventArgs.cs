namespace TabbySSH.Services.Connections;

public class ConnectionClosedEventArgs : EventArgs
{
    public bool IsNormalExit { get; }

    public ConnectionClosedEventArgs(bool isNormalExit)
    {
        IsNormalExit = isNormalExit;
    }
}

