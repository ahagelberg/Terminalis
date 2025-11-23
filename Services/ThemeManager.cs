using System.IO;
using System.Windows;
using System.Windows.Media;
using Newtonsoft.Json;
using TabbySSH.Models;

namespace TabbySSH.Services;

public class ThemeManager
{
    private const string THEMES_FOLDER = "Resources/Themes";
    private const string DEFAULT_THEME = "light";

    private Theme? _currentTheme;
    private readonly ResourceDictionary _themeResources = new();

    public Theme? CurrentTheme => _currentTheme;
    public event EventHandler<Theme>? ThemeChanged;

    public ThemeManager()
    {
        LoadTheme(DEFAULT_THEME);
    }

    public void LoadTheme(string themeName)
    {
        var themePath = Path.Combine(THEMES_FOLDER, $"{themeName}.json");
        
        if (!File.Exists(themePath))
        {
            System.Diagnostics.Debug.WriteLine($"Theme file not found: {themePath}, using default");
            LoadTheme(DEFAULT_THEME);
            return;
        }

        try
        {
            var json = File.ReadAllText(themePath);
            var theme = JsonConvert.DeserializeObject<Theme>(json);
            
            if (theme == null)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to deserialize theme: {themePath}");
                return;
            }

            _currentTheme = theme;
            ApplyTheme(theme);
            ThemeChanged?.Invoke(this, theme);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load theme {themePath}: {ex.Message}");
        }
    }

    private void ApplyTheme(Theme theme)
    {
        _themeResources.Clear();

        _themeResources["WindowBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.WindowBackground)!);
        _themeResources["WindowForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.WindowForeground)!);
        _themeResources["TitleBarBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TitleBarBackground)!);
        _themeResources["TitleBarForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TitleBarForeground)!);
        _themeResources["PanelBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.PanelBackground)!);
        _themeResources["PanelForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.PanelForeground)!);
        _themeResources["BorderColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.BorderColor)!);
        _themeResources["BorderColorDark"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.BorderColorDark)!);
        _themeResources["BorderColorLight"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.BorderColorLight)!);
        _themeResources["MenuBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.MenuBackground)!);
        _themeResources["MenuForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.MenuForeground)!);
        _themeResources["MenuHoverBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.MenuHoverBackground)!);
        _themeResources["MenuHoverForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.MenuHoverForeground)!);
        _themeResources["MenuItemIndicator"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.MenuItemIndicator)!);
        _themeResources["ButtonBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.ButtonBackground)!);
        _themeResources["ButtonForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.ButtonForeground)!);
        _themeResources["ButtonHoverBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.ButtonHoverBackground)!);
        _themeResources["ButtonBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.ButtonBorder)!);
        _themeResources["TextBoxBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TextBoxBackground)!);
        _themeResources["TextBoxForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TextBoxForeground)!);
        _themeResources["TextBoxBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TextBoxBorder)!);
        _themeResources["TreeViewBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TreeViewBackground)!);
        _themeResources["TreeViewForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TreeViewForeground)!);
        _themeResources["TreeViewHoverBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TreeViewHoverBackground)!);
        _themeResources["TreeViewSelectedBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TreeViewSelectedBackground)!);
        _themeResources["TabBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TabBackground)!);
        _themeResources["TabForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TabForeground)!);
        _themeResources["TabSelectedBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TabSelectedBackground)!);
        _themeResources["TabSelectedBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TabSelectedBorder)!);
        _themeResources["TabHoverBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TabHoverBorder)!);
        _themeResources["StatusBarBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.StatusBarBackground)!);
        _themeResources["StatusBarForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.StatusBarForeground)!);
        _themeResources["GridSplitterBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.GridSplitterBackground)!);
        _themeResources["NotificationBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.NotificationBackground)!);
        _themeResources["NotificationBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.NotificationBorder)!);
        _themeResources["NotificationForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.NotificationForeground)!);
        _themeResources["GroupBoxBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.GroupBoxBackground)!);
        _themeResources["GroupBoxForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.GroupBoxForeground)!);
        _themeResources["GroupBoxBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.GroupBoxBorder)!);
        _themeResources["ScrollBarBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.ScrollBarBackground)!);
        _themeResources["ScrollBarThumb"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.ScrollBarThumb)!);
        _themeResources["ScrollBarThumbHover"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.ScrollBarThumbHover)!);
        _themeResources["DataGridBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.DataGridBackground)!);
        _themeResources["DataGridForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.DataGridForeground)!);
        _themeResources["DataGridBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.DataGridBorder)!);
        _themeResources["DataGridHeaderBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.DataGridHeaderBackground)!);
        _themeResources["DataGridHeaderForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.DataGridHeaderForeground)!);
        _themeResources["DataGridRowHoverBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.DataGridRowHoverBackground)!);
        _themeResources["DataGridRowSelectedBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.DataGridRowSelectedBackground)!);
        
        var dropIndicatorColor = theme.DropIndicatorColor ?? theme.BorderColorDark;
        _themeResources["DropIndicatorColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dropIndicatorColor)!);

        Application.Current.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.MenuHoverBackground)!);
        Application.Current.Resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.MenuForeground)!);
        Application.Current.Resources[SystemColors.ControlBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.MenuBackground)!);
        Application.Current.Resources[SystemColors.ControlTextBrushKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.MenuForeground)!);

        Application.Current.Resources.MergedDictionaries.Remove(_themeResources);
        Application.Current.Resources.MergedDictionaries.Add(_themeResources);
    }

    public List<string> GetAvailableThemes()
    {
        var themes = new List<string>();
        var themesPath = THEMES_FOLDER;
        
        if (!Directory.Exists(themesPath))
        {
            return themes;
        }

        foreach (var file in Directory.GetFiles(themesPath, "*.json"))
        {
            var themeName = Path.GetFileNameWithoutExtension(file);
            themes.Add(themeName);
        }

        return themes;
    }
}

