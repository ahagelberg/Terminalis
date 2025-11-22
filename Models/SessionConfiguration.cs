namespace TabbySSH.Models;

public abstract class SessionConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string ConnectionType { get; set; } = string.Empty;
    public string? Color { get; set; }
    public string Encoding { get; set; } = "UTF-8";
    public string LineEnding { get; set; } = "\n";
    public string? Group { get; set; }
    public int Order { get; set; } = 0;
}

public class SessionGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? ParentGroup { get; set; }
    public int Order { get; set; } = 0;
}

