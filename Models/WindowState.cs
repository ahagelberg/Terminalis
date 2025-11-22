namespace TabbySSH.Models;

public class WindowState
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public System.Windows.WindowState State { get; set; } = System.Windows.WindowState.Normal;
}

