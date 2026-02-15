namespace Terminalis.Models;

public class KnownHostEntry
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string KeyAlgorithm { get; set; } = string.Empty;
}

