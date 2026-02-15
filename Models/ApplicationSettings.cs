namespace Terminalis.Models;

public class ApplicationSettings
{
    public bool RestoreActiveSessionsOnStartup { get; set; } = false;
    public string Theme { get; set; } = "light";
    public bool UseAccentColorForTitleBar { get; set; } = false;
}

