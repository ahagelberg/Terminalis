using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TabbySSH.Models;
using TabbySSH.Services;

namespace TabbySSH.Views;

public partial class OptionsDialog : Window
{
    private const int ScrollSyncThresholdPixels = 80;

    public ApplicationSettings? Settings { get; private set; }
    private string _selectedTheme;
    private readonly string _originalTheme;
    private bool _updatingSelectionFromScroll;

    public OptionsDialog(ApplicationSettings currentSettings)
    {
        InitializeComponent();
        RestoreSessionsCheckBox.IsChecked = currentSettings.RestoreActiveSessionsOnStartup;
        UseAccentColorForTitleBarCheckBox.IsChecked = currentSettings.UseAccentColorForTitleBar;
        _selectedTheme = currentSettings.Theme ?? "light";
        _originalTheme = _selectedTheme;

        var themes = App.ThemeManager.GetAvailableThemes();
        ThemeComboBox.ItemsSource = themes;
        ThemeComboBox.SelectedItem = _selectedTheme;

        Loaded += (s, e) =>
        {
            SectionAppearance.SizeChanged += (_, _) => UpdateBottomSpacerHeight();
        };
    }

    private void UpdateBottomSpacerHeight()
    {
        var viewportHeight = ContentScrollViewer.ActualHeight - ContentScrollViewer.Padding.Top - ContentScrollViewer.Padding.Bottom;
        var lastSectionTotalHeight = SectionAppearance.ActualHeight + SectionAppearance.Margin.Top + SectionAppearance.Margin.Bottom;
        if (viewportHeight > 0)
            BottomSpacer.Height = Math.Max(0, viewportHeight - lastSectionTotalHeight);
    }

    private void NavListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelectionFromScroll || NavListBox.SelectedItem is not ListBoxItem item || e.AddedItems.Count == 0)
            return;

        FrameworkElement? section = item == NavStartupItem ? SectionStartup : item == NavAppearanceItem ? SectionAppearance : null;
        if (section != null)
            ScrollToSectionTop(section);
    }

    private void ScrollToSectionTop(FrameworkElement section)
    {
        var content = ContentScrollViewer.Content as Visual;
        if (content == null)
            return;

        var transform = section.TransformToAncestor(content);
        var point = transform.Transform(new Point(0, 0));
        var offset = Math.Max(0, Math.Min(point.Y, ContentScrollViewer.ScrollableHeight));
        ContentScrollViewer.ScrollToVerticalOffset(offset);
    }

    private void ContentScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateBottomSpacerHeight();
    }

    private void ContentScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ContentScrollViewer.Content is not Visual content)
            return;

        var scrollOffset = ContentScrollViewer.VerticalOffset;
        ListBoxItem? selectedNav = null;
        var bestTop = double.NegativeInfinity;

        void Consider(FrameworkElement section, ListBoxItem navItem)
        {
            if (section.Visibility != Visibility.Visible)
                return;
            var transform = section.TransformToAncestor(content);
            var point = transform.Transform(new Point(0, 0));
            if (point.Y <= scrollOffset + ScrollSyncThresholdPixels && point.Y > bestTop)
            {
                bestTop = point.Y;
                selectedNav = navItem;
            }
        }

        Consider(SectionStartup, NavStartupItem);
        Consider(SectionAppearance, NavAppearanceItem);

        if (selectedNav != null && NavListBox.SelectedItem != selectedNav)
        {
            _updatingSelectionFromScroll = true;
            NavListBox.SelectedItem = selectedNav;
            _updatingSelectionFromScroll = false;
        }
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is string themeName)
        {
            _selectedTheme = themeName;
            App.ThemeManager.LoadTheme(themeName);
        }
    }

    private void OKButton_Click(object sender, RoutedEventArgs e)
    {
        Settings = new ApplicationSettings
        {
            RestoreActiveSessionsOnStartup = RestoreSessionsCheckBox.IsChecked == true,
            Theme = _selectedTheme,
            UseAccentColorForTitleBar = UseAccentColorForTitleBarCheckBox.IsChecked == true
        };

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        App.ThemeManager.LoadTheme(_originalTheme);
        DialogResult = false;
        Close();
    }
}

