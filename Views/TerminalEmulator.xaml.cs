using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TabbySSH.Services.Connections;
using TabbySSH.Utils;

namespace TabbySSH.Views;

public partial class TerminalEmulator : UserControl
{
    private const int DEFAULT_FONT_SIZE = 12;
    private const int DEFAULT_SCROLLBACK_LINES = 20000;
    private const int RENDER_THROTTLE_MS = 33; // ~30 FPS for smoother scrolling
    private const int SCROLL_LINES_PER_TICK = 3;
    private const int ECHO_DETECTION_WINDOW_MS = 100; // Time window to detect echoed user input

    private Vt100Emulator? _emulator;
    private ITerminalConnection? _connection;
    private Typeface? _typeface;
    private double _charWidth;
    private double _charHeight;
    private double _fontSize = DEFAULT_FONT_SIZE;
    private int _scrollOffset = 0;
    private DispatcherTimer? _renderTimer;
    private bool _pendingRender = false;
    private bool _isSelecting = false;
    private bool _hasSelection = false;
    private int? _selectionStartRow;
    private int? _selectionStartCol;
    private int? _selectionEndRow;
    private int? _selectionEndCol;
    private Rectangle? _lineFlashOverlay;
    private string _lineEnding = "\n";
    private bool _resetScrollOnUserInput = true;
    private bool _resetScrollOnServerOutput = false;
    private DateTime _lastInputSentTime = DateTime.MinValue;
    private string _backspaceKey = "DEL";

    public TerminalEmulator()
    {
        InitializeComponent();
        Loaded += TerminalEmulator_Loaded;
        SizeChanged += TerminalEmulator_SizeChanged;
        PreviewKeyDown += TerminalEmulator_PreviewKeyDown;
        TextInput += TerminalEmulator_TextInput;
        KeyDown += TerminalEmulator_KeyDown;
        PreviewMouseDown += TerminalEmulator_PreviewMouseDown;
        MouseDown += TerminalEmulator_MouseDown;
        PreviewMouseMove += TerminalEmulator_PreviewMouseMove;
        PreviewMouseUp += TerminalEmulator_PreviewMouseUp;
        KeyUp += TerminalEmulator_KeyUp;
        MouseWheel += TerminalEmulator_MouseWheel;
        ContextMenuOpening += TerminalEmulator_ContextMenuOpening;
        ContextMenu = null;
        Focusable = true;
        
        _renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(RENDER_THROTTLE_MS)
        };
        _renderTimer.Tick += RenderTimer_Tick;
    }

    public void AttachConnection(ITerminalConnection connection, string? lineEnding = null, string? fontFamily = null, double? fontSize = null, string? foregroundColor = null, string? backgroundColor = null, string? bellNotification = null, bool resetScrollOnUserInput = true, bool resetScrollOnServerOutput = false, string? backspaceKey = null)
    {
        if (_connection != null)
        {
            _connection.DataReceived -= OnDataReceived;
            _connection.ConnectionClosed -= OnConnectionClosed;
        }

        _connection = connection;
        _connection.DataReceived += OnDataReceived;
        _connection.ConnectionClosed += OnConnectionClosed;
        _lineEnding = ConvertLineEndingString(lineEnding ?? "\n");
        _bellNotification = bellNotification ?? "Line Flash";
        _resetScrollOnUserInput = resetScrollOnUserInput;
        _resetScrollOnServerOutput = resetScrollOnServerOutput;
        _backspaceKey = backspaceKey ?? "DEL";

        if (_emulator != null)
        {
            _emulator.Bell -= OnBell;
            _emulator.ScreenChanged -= OnScreenChanged;
            _emulator.CursorMoved -= OnCursorMoved;
        }

        _emulator = new Vt100Emulator();
        _emulator.SetScrollbackLimit(DEFAULT_SCROLLBACK_LINES);
        _emulator.ScreenChanged += OnScreenChanged;
        _emulator.CursorMoved += OnCursorMoved;
        _emulator.Bell += OnBell;

        InitializeFont(fontFamily ?? "Consolas", fontSize ?? DEFAULT_FONT_SIZE);

        if (!string.IsNullOrEmpty(backgroundColor))
        {
            ApplyBackgroundColor(backgroundColor);
        }

        if (!string.IsNullOrEmpty(foregroundColor))
        {
            _customForegroundColor = foregroundColor;
        }

        UpdateTerminalSize();
        RenderScreen();
        
        if (_connection != null && _connection.IsConnected)
        {
            SendTerminalSizeToServer();
        }
        
        Dispatcher.BeginInvoke(new Action(() => Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void SendTerminalSizeToServer()
    {
        if (_emulator != null && _connection != null && _connection.IsConnected)
        {
            if (_connection is SshConnection sshConn)
            {
                sshConn.ResizeTerminal(_emulator.Cols, _emulator.Rows);
            }
        }
    }

    private string? _customForegroundColor;
    private string? _customBackgroundColor;
    private string _bellNotification = "Line Flash";
    private DispatcherTimer? _flashTimer;
    private DispatcherTimer? _lineFlashTimer;

    private void TerminalEmulator_Loaded(object sender, RoutedEventArgs e)
    {
        if (_typeface == null)
        {
            InitializeFont();
        }
        UpdateTerminalSize();
        UpdateCanvasHeight();
        RenderScreen();
        Focus();
        
        // Mouse events are handled at UserControl level via PreviewMouseDown/Move/Up
    }
    

    private void TerminalEmulator_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_emulator != null && _charWidth > 0 && _charHeight > 0)
        {
            var oldCols = _emulator.Cols;
            var oldRows = _emulator.Rows;
            UpdateTerminalSize();
            
            if (_connection != null && _connection.IsConnected && (oldCols != _emulator.Cols || oldRows != _emulator.Rows))
            {
                SendTerminalSizeToServer();
            }
            
            RenderScreen();
        }
    }

    private void InitializeFont(string fontFamily = "Consolas", double fontSize = DEFAULT_FONT_SIZE)
    {
        _typeface = new Typeface(new FontFamily(fontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var formattedText = new FormattedText("M", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, fontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _charWidth = formattedText.Width;
        _charHeight = formattedText.Height;
        _fontSize = fontSize;
    }

    public void UpdateSettings(string? lineEnding = null, string? fontFamily = null, double? fontSize = null, string? foregroundColor = null, string? backgroundColor = null, string? bellNotification = null, bool? resetScrollOnUserInput = null, bool? resetScrollOnServerOutput = null, string? backspaceKey = null)
    {
        bool fontChanged = false;
        
        if (lineEnding != null)
        {
            _lineEnding = ConvertLineEndingString(lineEnding);
        }
        
        if (fontFamily != null || fontSize != null)
        {
            InitializeFont(fontFamily ?? _typeface?.FontFamily?.Source ?? "Consolas", fontSize ?? _fontSize);
            fontChanged = true;
        }
        
        if (backgroundColor != null)
        {
            ApplyBackgroundColor(backgroundColor);
        }
        else
        {
            if (_customBackgroundColor != null)
            {
                ClearBackgroundColor();
            }
        }
        
        if (foregroundColor != null)
        {
            _customForegroundColor = foregroundColor;
        }
        else
        {
            if (_customForegroundColor != null)
            {
                _customForegroundColor = null;
            }
        }
        
        if (bellNotification != null)
        {
            _bellNotification = bellNotification;
        }
        
        if (resetScrollOnUserInput.HasValue)
        {
            _resetScrollOnUserInput = resetScrollOnUserInput.Value;
        }
        
        if (resetScrollOnServerOutput.HasValue)
        {
            _resetScrollOnServerOutput = resetScrollOnServerOutput.Value;
        }
        
        if (backspaceKey != null)
        {
            _backspaceKey = backspaceKey;
        }
        
        if (fontChanged)
        {
            UpdateTerminalSize();
        }
        
        RenderScreen();
    }

    private void ApplyBackgroundColor(string colorName)
    {
        try
        {
            var brush = new BrushConverter().ConvertFromString(colorName) as SolidColorBrush;
            if (brush != null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Background = brush;
                    TerminalCanvas.Background = brush;
                }));
                _customBackgroundColor = colorName;
            }
        }
        catch
        {
        }
    }

    private void ClearBackgroundColor()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var defaultBrush = new SolidColorBrush(Colors.Black);
            Background = defaultBrush;
            TerminalCanvas.Background = defaultBrush;
        }));
        _customBackgroundColor = null;
    }

    private void UpdateTerminalSize()
    {
        if (_emulator == null || _charWidth == 0 || _charHeight == 0)
        {
            return;
        }

        var cols = Math.Max(1, (int)(ActualWidth / _charWidth));
        var rows = Math.Max(1, (int)(ActualHeight / _charHeight));
        _emulator.SetSize(cols, rows);
        
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TerminalCanvas.Width = cols * _charWidth;
            // Canvas height should be large enough for scrollback, but we'll update it dynamically
            UpdateCanvasHeight();
        }));
    }

    public void UpdateFont(string fontFamily, double fontSize)
    {
        InitializeFont(fontFamily, fontSize);
        UpdateTerminalSize();
        RenderScreen();
    }

    private void OnDataReceived(object? sender, string data)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_emulator != null)
            {
                _emulator.ProcessData(data);
                
                if (_resetScrollOnServerOutput && _scrollOffset > 0)
                {
                    var timeSinceLastInput = (DateTime.Now - _lastInputSentTime).TotalMilliseconds;
                    if (timeSinceLastInput > ECHO_DETECTION_WINDOW_MS)
                    {
                        ResetScrollPosition();
                    }
                }
            }
        }));
    }

    private static string EscapeString(string s)
    {
        var sb = new System.Text.StringBuilder();
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

    private void OnConnectionClosed(object? sender, ConnectionClosedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
        });
    }

    private void OnScreenChanged(object? sender, EventArgs e)
    {
        if (_emulator != null)
        {
            // Update canvas height to accommodate scrollback
            UpdateCanvasHeight();
            
            // Auto-scroll to bottom if user is already at bottom (scrollOffset == 0)
            // If user has scrolled up, keep their position
            if (_scrollOffset == 0)
            {
                // Stay at bottom (scrollOffset remains 0)
                UpdateCanvasTransform();
            }
        }
        ScheduleRender();
    }

    private int _previousCursorRow = -1;
    private int _previousCursorCol = -1;

    private void OnCursorMoved(object? sender, EventArgs e)
    {
        ScheduleRender();
    }

    private void ScheduleRender()
    {
        _pendingRender = true;
        if (!_renderTimer!.IsEnabled)
        {
            _renderTimer.Start();
        }
    }

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        _renderTimer!.Stop();
        if (_pendingRender)
        {
            _pendingRender = false;
            RenderScreen();
        }
    }

    private void OnBell(object? sender, EventArgs e)
    {
        var currentSetting = _bellNotification;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (string.Equals(currentSetting, "Sound", StringComparison.OrdinalIgnoreCase))
            {
                System.Media.SystemSounds.Beep.Play();
            }
            else if (string.Equals(currentSetting, "Flash", StringComparison.OrdinalIgnoreCase))
            {
                FlashWindow();
            }
            else if (string.Equals(currentSetting, "Line Flash", StringComparison.OrdinalIgnoreCase))
            {
                FlashCurrentLine();
            }
            else if (string.Equals(currentSetting, "None", StringComparison.OrdinalIgnoreCase))
            {
            }
            else
            {
                FlashWindow();
            }
        }));
    }

    private void FlashWindow()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            FlashTerminalBackground();
            FlashTab();
        }));
    }

    private void FlashTerminalBackground()
    {
        if (FlashOverlay == null || TerminalCanvas == null) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            var currentBg = GetBackgroundColor();
            var currentFg = GetForegroundColor();
            
            FlashOverlay.Width = ActualWidth;
            FlashOverlay.Height = ActualHeight;
            FlashOverlay.Fill = new SolidColorBrush(currentFg);
            FlashOverlay.Visibility = Visibility.Visible;
            FlashOverlay.Opacity = 1.0;

            if (_flashTimer != null)
            {
                _flashTimer.Stop();
            }

            _flashTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(75)
            };
            _flashTimer.Tick += (s, e) =>
            {
                _flashTimer.Stop();
                FlashOverlay.Opacity = 0;
                FlashOverlay.Visibility = Visibility.Collapsed;
            };
            _flashTimer.Start();
        }));
    }

    private Color GetBackgroundColor()
    {
        if (!string.IsNullOrEmpty(_customBackgroundColor))
        {
            try
            {
                var brush = new BrushConverter().ConvertFromString(_customBackgroundColor) as SolidColorBrush;
                if (brush != null)
                {
                    return brush.Color;
                }
            }
            catch
            {
            }
        }
        return Colors.Black;
    }

    private Color GetForegroundColor()
    {
        if (!string.IsNullOrEmpty(_customForegroundColor))
        {
            try
            {
                var brush = new BrushConverter().ConvertFromString(_customForegroundColor) as SolidColorBrush;
                if (brush != null)
                {
                    return brush.Color;
                }
            }
            catch
            {
            }
        }
        return Colors.LightGray;
    }

    private void FlashTab()
    {
        var tabItem = FindParent<TabItem>(this);
        if (tabItem == null) return;

        var originalBackground = tabItem.Background;
        var foregroundColor = GetForegroundColor();
        var flashBrush = new SolidColorBrush(foregroundColor);
        
        tabItem.Background = flashBrush;

        var tabFlashTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(75)
        };
        tabFlashTimer.Tick += (s, e) =>
        {
            tabFlashTimer.Stop();
            tabItem.Background = originalBackground;
        };
        tabFlashTimer.Start();
    }

    private void FlashCurrentLine()
    {
        if (TerminalCanvas == null || _emulator == null || _charHeight <= 0) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            var currentFg = GetForegroundColor();
            var cursorRow = _emulator.CursorRow;
            
            var y = cursorRow * _charHeight;
            
            if (y < 0 || TerminalCanvas.ActualHeight <= 0)
            {
                return;
            }
            
            if (_lineFlashOverlay == null)
            {
                _lineFlashOverlay = new Rectangle
                {
                    IsHitTestVisible = false
                };
            }

            _lineFlashOverlay.Width = TerminalCanvas.ActualWidth > 0 ? TerminalCanvas.ActualWidth : ActualWidth;
            _lineFlashOverlay.Height = _charHeight;
            Canvas.SetLeft(_lineFlashOverlay, 0);
            Canvas.SetTop(_lineFlashOverlay, y);
            _lineFlashOverlay.Fill = new SolidColorBrush(currentFg);
            _lineFlashOverlay.Visibility = Visibility.Visible;
            _lineFlashOverlay.Opacity = 1.0;

            if (!TerminalCanvas.Children.Contains(_lineFlashOverlay))
            {
                TerminalCanvas.Children.Add(_lineFlashOverlay);
            }

            if (_lineFlashTimer != null)
            {
                _lineFlashTimer.Stop();
            }

            _lineFlashTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(75)
            };
            _lineFlashTimer.Tick += (s, e) =>
            {
                _lineFlashTimer.Stop();
                if (_lineFlashOverlay != null)
                {
                    _lineFlashOverlay.Opacity = 0;
                    _lineFlashOverlay.Visibility = Visibility.Collapsed;
                    if (TerminalCanvas.Children.Contains(_lineFlashOverlay))
                    {
                        TerminalCanvas.Children.Remove(_lineFlashOverlay);
                    }
                }
            };
            _lineFlashTimer.Start();
        }));
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindParent<T>(parentObject);
    }


    private void RenderScreen()
    {
        if (_emulator == null || _typeface == null)
        {
            return;
        }

        var currentCursorRow = _emulator.CursorRow;
        var currentCursorCol = _emulator.CursorCol;

        var lineFlashOverlay = _lineFlashOverlay;
        TerminalCanvas.Children.Clear();
        _lineFlashOverlay = null;

        // Calculate which rows to render based on scroll offset
        // Unified buffer: rows 0 to (ScrollbackCount-1) are scrollback, rows ScrollbackCount to (ScrollbackCount+Rows-1) are current screen
        var totalRows = _emulator.Rows + _emulator.ScrollbackLineCount;
        var visibleRows = (int)(ActualHeight / _charHeight) + 1;
        
        // When scrollOffset = 0, show current screen (rows ScrollbackCount to totalRows-1)
        // When scrollOffset = 10, show older content (rows ScrollbackCount-10 to ScrollbackCount-10+visibleRows-1)
        var startRow = Math.Max(0, _emulator.ScrollbackLineCount - _scrollOffset);
        var endRow = Math.Min(totalRows, startRow + visibleRows);

        // Render only visible rows, positioned relative to viewport (row 0 at top of viewport)
        for (int row = startRow; row < endRow; row++)
        {
            var viewportRow = row - startRow; // Position in viewport (0 = top)
            RenderLine(row, viewportRow);
        }

        if (_scrollOffset == 0)
        {
            RenderCursor();
        }

        if (lineFlashOverlay != null && lineFlashOverlay.Visibility == Visibility.Visible)
        {
            _lineFlashOverlay = lineFlashOverlay;
            TerminalCanvas.Children.Add(_lineFlashOverlay);
        }

        _previousCursorRow = currentCursorRow;
        _previousCursorCol = currentCursorCol;
    }

    private void RenderLine(int bufferRow, int viewportRow)
    {
        if (_emulator == null || _typeface == null)
        {
            return;
        }

        // Calculate y position: viewportRow 0 is at top of viewport
        var y = viewportRow * _charHeight;
        var currentFg = -1;
        var currentBg = -1;
        var currentBold = false;
        var currentItalic = false;
        var currentUnderline = false;
            var currentFaint = false;
            var currentCrossedOut = false;
            var currentDoubleUnderline = false;
            var currentOverline = false;
            var currentConceal = false;
            var x = 0.0;
            var textRun = "";
            var textRunStartCol = 0;

        for (int col = 0; col < _emulator.Cols; col++)
        {

            TerminalCell cell;
            // Unified buffer: rows 0 to (ScrollbackCount-1) are scrollback, rows ScrollbackCount to (ScrollbackCount+Rows-1) are current screen
            if (bufferRow < _emulator.ScrollbackLineCount)
            {
                // This is scrollback
                cell = _emulator.GetScrollbackCell(bufferRow, col);
            }
            else
            {
                // This is current screen
                var screenRow = bufferRow - _emulator.ScrollbackLineCount;
                cell = _emulator.GetCell(screenRow, col);
            }

            var fg = cell.Reverse ? cell.BackgroundColor : cell.ForegroundColor;
            var bg = cell.Reverse ? cell.ForegroundColor : cell.BackgroundColor;
            var bold = cell.Bold;
            var italic = cell.Italic;
            var underline = cell.Underline;
            var faint = cell.Faint;
            var crossedOut = cell.CrossedOut;
            var doubleUnderline = cell.DoubleUnderline;
            var overline = cell.Overline;
            var conceal = cell.Conceal;

            if (fg != currentFg || bg != currentBg || bold != currentBold || italic != currentItalic || 
                underline != currentUnderline || faint != currentFaint || crossedOut != currentCrossedOut ||
                doubleUnderline != currentDoubleUnderline || overline != currentOverline || conceal != currentConceal || col == _emulator.Cols - 1)
            {
                if (!string.IsNullOrEmpty(textRun))
                {
                    RenderTextSegment(x, y, textRun, currentFg, currentBg, currentBold, currentItalic, currentUnderline, currentFaint, currentCrossedOut, currentDoubleUnderline, currentOverline, currentConceal, textRunStartCol, bufferRow);
                    x += textRun.Length * _charWidth;
                }

                if (col < _emulator.Cols - 1)
                {
                    textRun = cell.Character.ToString();
                    textRunStartCol = col;
                    currentFg = fg;
                    currentBg = bg;
                    currentBold = bold;
                    currentItalic = italic;
                    currentUnderline = underline;
                    currentFaint = faint;
                    currentCrossedOut = crossedOut;
                    currentDoubleUnderline = doubleUnderline;
                    currentOverline = overline;
                    currentConceal = conceal;
                }
            }
            else
            {
                textRun += cell.Character;
            }
        }

        if (!string.IsNullOrEmpty(textRun))
        {
            RenderTextSegment(x, y, textRun, currentFg, currentBg, currentBold, currentItalic, currentUnderline, currentFaint, currentCrossedOut, currentDoubleUnderline, currentOverline, currentConceal, textRunStartCol, bufferRow);
        }
    }

    private bool IsCellSelected(int row, int col)
    {
        if (!_selectionStartRow.HasValue || !_selectionStartCol.HasValue || 
            !_selectionEndRow.HasValue || !_selectionEndCol.HasValue)
        {
            return false;
        }

        var startRow = Math.Min(_selectionStartRow.Value, _selectionEndRow.Value);
        var endRow = Math.Max(_selectionStartRow.Value, _selectionEndRow.Value);
        var startCol = Math.Min(_selectionStartCol.Value, _selectionEndCol.Value);
        var endCol = Math.Max(_selectionStartCol.Value, _selectionEndCol.Value);

        if (row < startRow || row > endRow)
        {
            return false;
        }

        if (row == startRow && row == endRow)
        {
            return col >= startCol && col < endCol;
        }

        if (row == startRow)
        {
            return col >= startCol;
        }

        if (row == endRow)
        {
            return col < endCol;
        }

        return true;
    }

    private void RenderTextSegment(double x, double y, string text, int fgColor, int bgColor, bool bold, bool italic, bool underline, bool faint, bool crossedOut, bool doubleUnderline, bool overline, bool conceal, int startCol, int row)
    {
        if (_typeface == null || string.IsNullOrEmpty(text))
        {
            return;
        }

        if (x < 0 || y < 0 || x + text.Length * _charWidth > TerminalCanvas.Width)
        {
            return;
        }

        var foreground = GetColor(fgColor);
        var background = GetColor(bgColor);
        var fontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
        var fontStyle = italic ? FontStyles.Italic : FontStyles.Normal;

        // Check if this text segment crosses selection boundary - only split if necessary
        if ((_hasSelection || _isSelecting) && text.Length > 1)
        {
            // Check if segment has mixed selection state
            bool firstCharSelected = IsCellSelected(row, startCol);
            bool needsSplit = false;
            
            for (int i = 1; i < text.Length; i++)
            {
                if (IsCellSelected(row, startCol + i) != firstCharSelected)
                {
                    needsSplit = true;
                    break;
                }
            }
            
            // If all characters have same selection state, render normally
            if (!needsSplit)
            {
                if (firstCharSelected)
                {
                    var temp = foreground;
                    foreground = background;
                    background = temp;
                }
            }
            else
            {
                // Split and render character by character only for mixed segments
                for (int i = 0; i < text.Length; i++)
                {
                    var charCol = startCol + i;
                    var cellIsSelected = IsCellSelected(row, charCol);
                    
                    // Calculate exact position based on column index to avoid accumulation errors
                    var charX = charCol * _charWidth;
                    
                    var charForeground = cellIsSelected ? background : foreground;
                    var charBackground = cellIsSelected ? foreground : background;

        if (conceal)
        {
                        charForeground = charBackground;
                    }
                    else if (faint && charForeground is SolidColorBrush fgBrush)
                    {
                        var color = fgBrush.Color;
                        var fadedColor = Color.FromArgb(color.A, (byte)(color.R * 0.5), (byte)(color.G * 0.5), (byte)(color.B * 0.5));
                        charForeground = new SolidColorBrush(fadedColor);
                    }

                    var charBgRect = new Rectangle
                    {
                        Width = _charWidth + 0.1,
                        Height = _charHeight,
                        Fill = charBackground,
                        SnapsToDevicePixels = true
                    };
                    Canvas.SetLeft(charBgRect, charX);
                    Canvas.SetTop(charBgRect, y);
                    TerminalCanvas.Children.Add(charBgRect);

                    var charTextBlock = new TextBlock
                    {
                        Text = text[i].ToString(),
                        Foreground = charForeground,
                        FontFamily = _typeface.FontFamily,
                        FontSize = _fontSize,
                        FontWeight = fontWeight,
                        FontStyle = fontStyle,
                        TextDecorations = new TextDecorationCollection(),
                        SnapsToDevicePixels = true
                    };

                    if (underline || doubleUnderline)
                    {
                        charTextBlock.TextDecorations.Add(TextDecorations.Underline);
                    }

                    if (overline)
                    {
                        charTextBlock.TextDecorations.Add(TextDecorations.OverLine);
                    }

                    if (crossedOut)
                    {
                        charTextBlock.TextDecorations.Add(TextDecorations.Strikethrough);
                    }

                    Canvas.SetLeft(charTextBlock, charX);
                    Canvas.SetTop(charTextBlock, y);
                    TerminalCanvas.Children.Add(charTextBlock);

                    if (doubleUnderline)
                    {
                        var underlineRect = new Rectangle
                        {
                            Width = _charWidth,
                            Height = 1,
                            Fill = charForeground
                        };
                        Canvas.SetLeft(underlineRect, charX);
                        Canvas.SetTop(underlineRect, y + _charHeight - 3);
                        TerminalCanvas.Children.Add(underlineRect);
                    }
                }
                return;
            }
        }

        // For single character or no selection, render normally
        bool charIsSelected = false;
        if ((_hasSelection || _isSelecting) && text.Length == 1)
        {
            charIsSelected = IsCellSelected(row, startCol);
        }

        // Invert colors if selected
        if (charIsSelected)
        {
            var temp = foreground;
            foreground = background;
            background = temp;
        }

        if (conceal)
        {
            foreground = background;
        }
        else if (faint && foreground is SolidColorBrush fgBrush)
        {
            var color = fgBrush.Color;
            var fadedColor = Color.FromArgb(color.A, (byte)(color.R * 0.5), (byte)(color.G * 0.5), (byte)(color.B * 0.5));
            foreground = new SolidColorBrush(fadedColor);
        }

        var bgRect = new Rectangle
        {
            Width = text.Length * _charWidth,
            Height = _charHeight,
            Fill = background,
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(bgRect, x);
        Canvas.SetTop(bgRect, y);
        TerminalCanvas.Children.Add(bgRect);

        var textBlock = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontFamily = _typeface.FontFamily,
            FontSize = _fontSize,
            FontWeight = fontWeight,
            FontStyle = fontStyle,
            TextDecorations = new TextDecorationCollection()
        };

        if (underline || doubleUnderline)
        {
            textBlock.TextDecorations.Add(TextDecorations.Underline);
        }

        if (overline)
        {
            textBlock.TextDecorations.Add(TextDecorations.OverLine);
        }

        if (crossedOut)
        {
            textBlock.TextDecorations.Add(TextDecorations.Strikethrough);
        }

        Canvas.SetLeft(textBlock, x);
        Canvas.SetTop(textBlock, y);
        TerminalCanvas.Children.Add(textBlock);

        if (doubleUnderline)
        {
            var underlineRect = new Rectangle
            {
                Width = text.Length * _charWidth,
                Height = 1,
                Fill = foreground
            };
            Canvas.SetLeft(underlineRect, x);
            Canvas.SetTop(underlineRect, y + _charHeight - 3);
            TerminalCanvas.Children.Add(underlineRect);
        }
    }

    private void RenderCursor()
    {
        if (_emulator == null || _scrollOffset > 0 || _typeface == null)
        {
            return;
        }

        var x = _emulator.CursorCol * _charWidth;
        var y = _emulator.CursorRow * _charHeight;

        var cell = _emulator.GetCell(_emulator.CursorRow, _emulator.CursorCol);
        var fg = cell.Reverse ? cell.BackgroundColor : cell.ForegroundColor;
        var bg = cell.Reverse ? cell.ForegroundColor : cell.BackgroundColor;

        var foregroundBrush = GetColor(fg);
        var foregroundColor = foregroundBrush is SolidColorBrush fgSolid ? fgSolid.Color : Colors.White;

        var underlineRect = new Rectangle
        {
            Width = _charWidth,
            Height = 2,
            Fill = foregroundBrush
        };
        Canvas.SetLeft(underlineRect, x);
        Canvas.SetTop(underlineRect, y + _charHeight - 2);
        Canvas.SetZIndex(underlineRect, 1000);
        TerminalCanvas.Children.Add(underlineRect);
    }

    private Brush GetColor(int colorIndex)
    {
        if (colorIndex == 0 && !string.IsNullOrEmpty(_customBackgroundColor))
        {
            try
            {
                return new BrushConverter().ConvertFromString(_customBackgroundColor) as Brush ?? new SolidColorBrush(Color.FromRgb(0, 0, 0));
            }
            catch
            {
            }
        }
        
        if (colorIndex == 7 && !string.IsNullOrEmpty(_customForegroundColor))
        {
            try
            {
                return new BrushConverter().ConvertFromString(_customForegroundColor) as Brush ?? new SolidColorBrush(Color.FromRgb(192, 192, 192));
            }
            catch
            {
            }
        }
        
        return colorIndex switch
        {
            0 => new SolidColorBrush(Color.FromRgb(0, 0, 0)),
            1 => new SolidColorBrush(Color.FromRgb(192, 0, 0)),
            2 => new SolidColorBrush(Color.FromRgb(0, 192, 0)),
            3 => new SolidColorBrush(Color.FromRgb(192, 192, 0)),
            4 => new SolidColorBrush(Color.FromRgb(0, 0, 255)),
            5 => new SolidColorBrush(Color.FromRgb(192, 0, 192)),
            6 => new SolidColorBrush(Color.FromRgb(0, 192, 192)),
            7 => new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            8 => new SolidColorBrush(Color.FromRgb(128, 128, 128)),
            9 => new SolidColorBrush(Color.FromRgb(255, 0, 0)),
            10 => new SolidColorBrush(Color.FromRgb(0, 255, 0)),
            11 => new SolidColorBrush(Color.FromRgb(255, 255, 0)),
            12 => new SolidColorBrush(Color.FromRgb(80, 80, 255)),
            13 => new SolidColorBrush(Color.FromRgb(255, 0, 255)),
            14 => new SolidColorBrush(Color.FromRgb(0, 255, 255)),
            15 => new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            _ => Brushes.White
        };
    }

    private void TerminalEmulator_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Don't handle clicks on the scrollbar or its parts
        var source = e.OriginalSource;
        if (source is Thumb || 
            source is RepeatButton ||
            source is ScrollBar)
        {
            return;
        }
        
        var sourceDependencyObject = source as DependencyObject;
        if (sourceDependencyObject != null && FindParent<ScrollBar>(sourceDependencyObject) != null)
        {
            return;
        }
        
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            // Check if click is over the Canvas
            var pos = e.GetPosition(TerminalCanvas);
            if (pos.X >= 0 && pos.Y >= 0 && pos.X <= TerminalCanvas.ActualWidth && pos.Y <= TerminalCanvas.ActualHeight && _emulator != null && _charWidth > 0 && _charHeight > 0)
            {
                Focus();
                
                // If there's already a selection and we're clicking, copy it (PuTTY behavior)
                if (_hasSelection && !_isSelecting)
                {
                    CopySelectionToClipboard();
            ClearSelection();
                    _hasSelection = false;
                }
                
                // Calculate cell position
                var col = Math.Max(0, Math.Min(_emulator.Cols - 1, (int)(pos.X / _charWidth)));
                // Mouse position in viewport (0 = top of visible area)
                var viewportRow = Math.Max(0, (int)(pos.Y / _charHeight));
                // Convert viewport row to buffer row
                var startRow = Math.Max(0, _emulator.ScrollbackLineCount - _scrollOffset);
                var row = startRow + viewportRow;
                row = Math.Min(row, _emulator.Rows + _emulator.ScrollbackLineCount - 1);
                
                // Handle double-click (word selection) and triple-click (line selection)
                if (e.ClickCount == 2)
                {
                    // Double-click: select word
                    SelectWordAt(row, col);
                    e.Handled = true;
                    return;
                }
                else if (e.ClickCount == 3)
                {
                    // Triple-click: select line
                    SelectLineAt(row);
                    e.Handled = true;
                    return;
                }
                
                // Start new selection
                _isSelecting = true;
                _selectionStartRow = row;
                _selectionStartCol = col;
                _selectionEndRow = row;
                _selectionEndCol = col;
                
                ScheduleRender();
                
                // Capture mouse and set cursor
                CaptureMouse();
                Cursor = Cursors.IBeam;
                e.Handled = true;
            }
        }
        else if (e.RightButton == MouseButtonState.Pressed)
        {
            // Always handle right-click to prevent context menu from appearing
            var pos = e.GetPosition(TerminalCanvas);
            if (pos.X >= 0 && pos.Y >= 0 && pos.X <= TerminalCanvas.ActualWidth && pos.Y <= TerminalCanvas.ActualHeight)
            {
                if (!_isSelecting && !_hasSelection)
            {
                PasteFromClipboard();
                }
                e.Handled = true;
            }
        }
    }
    
    private void TerminalEmulator_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Handle right-click at MouseDown level to prevent context menu
        if (e.RightButton == MouseButtonState.Pressed)
        {
            var pos = e.GetPosition(TerminalCanvas);
            if (pos.X >= 0 && pos.Y >= 0 && pos.X <= TerminalCanvas.ActualWidth && pos.Y <= TerminalCanvas.ActualHeight)
            {
                e.Handled = true;
            }
        }
    }
    
    private void TerminalEmulator_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Prevent context menu from opening in terminal area
        e.Handled = true;
    }

    private void TerminalEmulator_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // Don't handle mouse move over the scrollbar or its parts
        var source = e.OriginalSource;
        if (source is Thumb || 
            source is RepeatButton ||
            source is ScrollBar)
        {
            return;
        }
        
        var sourceDependencyObject = source as DependencyObject;
        if (sourceDependencyObject != null && FindParent<ScrollBar>(sourceDependencyObject) != null)
        {
            return;
        }
        
        if (_isSelecting && e.LeftButton == MouseButtonState.Pressed && _emulator != null && _charWidth > 0 && _charHeight > 0)
        {
            var pos = e.GetPosition(TerminalCanvas);
            var col = Math.Max(0, Math.Min(_emulator.Cols - 1, (int)(pos.X / _charWidth)));
            // Mouse position in viewport (0 = top of visible area)
            var viewportRow = Math.Max(0, (int)(pos.Y / _charHeight));
            // Convert viewport row to buffer row
            var startRow = Math.Max(0, _emulator.ScrollbackLineCount - _scrollOffset);
            var row = startRow + viewportRow;
            row = Math.Min(row, _emulator.Rows + _emulator.ScrollbackLineCount - 1);
            
            if (_selectionEndRow != row || _selectionEndCol != col)
            {
                _selectionEndRow = row;
                _selectionEndCol = col;
                ScheduleRender();
            }
            
            Cursor = Cursors.IBeam;
            e.Handled = true;
        }
        else if (!_isSelecting && !_hasSelection)
        {
            // Set IBeam cursor when hovering over terminal
            Cursor = Cursors.IBeam;
        }
    }

    private void TerminalEmulator_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released && _isSelecting)
        {
            _isSelecting = false;
            ReleaseMouseCapture();
            
            // Check if we actually have a selection (not just a click)
            if (_selectionStartRow.HasValue && _selectionStartCol.HasValue && 
                _selectionEndRow.HasValue && _selectionEndCol.HasValue)
            {
                var startRow = Math.Min(_selectionStartRow.Value, _selectionEndRow.Value);
                var endRow = Math.Max(_selectionStartRow.Value, _selectionEndRow.Value);
                var startCol = Math.Min(_selectionStartCol.Value, _selectionEndCol.Value);
                var endCol = Math.Max(_selectionStartCol.Value, _selectionEndCol.Value);
                
                // Only mark as having selection if there's actual area selected
                if (endRow > startRow || (endRow == startRow && endCol > startCol))
                {
                    _hasSelection = true;
            }
                else
                {
                    // Just a click, clear selection
                    ClearSelection();
                    _hasSelection = false;
                }
            }
            
            e.Handled = true;
        }
    }

    private void ClearSelection()
    {
        _selectionStartRow = null;
        _selectionStartCol = null;
        _selectionEndRow = null;
        _selectionEndCol = null;
        _hasSelection = false;
    }

    private void SelectWordAt(int row, int col)
    {
        if (_emulator == null) return;
        
        TerminalCell cell;
        // Unified buffer: rows 0 to (ScrollbackCount-1) are scrollback, rows ScrollbackCount to (ScrollbackCount+Rows-1) are current screen
        if (row < _emulator.ScrollbackLineCount)
        {
            // This is scrollback
            cell = _emulator.GetScrollbackCell(row, col);
        }
        else
        {
            // This is current screen
            var screenRow = row - _emulator.ScrollbackLineCount;
            cell = _emulator.GetCell(screenRow, col);
        }
        
        // Check if we're on a word character (non-whitespace)
        bool isWordChar = !char.IsWhiteSpace(cell.Character);
        
        int startCol = col;
        int endCol = col;
        
        if (isWordChar)
        {
            // Find start of word (go left until we hit whitespace or start of line)
            while (startCol > 0)
            {
                TerminalCell leftCell;
                if (row < _emulator.ScrollbackLineCount)
                {
                    leftCell = _emulator.GetScrollbackCell(row, startCol - 1);
                }
                else
                {
                    var screenRow = row - _emulator.ScrollbackLineCount;
                    leftCell = _emulator.GetCell(screenRow, startCol - 1);
                }
                
                if (char.IsWhiteSpace(leftCell.Character))
                {
                    break;
                }
                startCol--;
            }
            
            // Find end of word (go right until we hit whitespace or end of line)
            while (endCol < _emulator.Cols - 1)
            {
                TerminalCell rightCell;
                if (row < _emulator.ScrollbackLineCount)
                {
                    rightCell = _emulator.GetScrollbackCell(row, endCol + 1);
                }
                else
                {
                    var screenRow = row - _emulator.ScrollbackLineCount;
                    rightCell = _emulator.GetCell(screenRow, endCol + 1);
                }
                
                if (char.IsWhiteSpace(rightCell.Character))
                {
                    break;
                }
                endCol++;
            }
            endCol++; // Include the last character
        }
        else
        {
            // If on whitespace, select just that character
            endCol++;
        }
        
        _selectionStartRow = row;
        _selectionStartCol = startCol;
        _selectionEndRow = row;
        _selectionEndCol = endCol;
        _hasSelection = true;
        _isSelecting = false;
        
        ScheduleRender();
    }
    
    private void SelectLineAt(int row)
    {
        if (_emulator == null) return;
        
        // Select entire line from column 0 to end
        _selectionStartRow = row;
        _selectionStartCol = 0;
        _selectionEndRow = row;
        _selectionEndCol = _emulator.Cols;
        _hasSelection = true;
        _isSelecting = false;
        
        ScheduleRender();
    }

    private void CopySelectionToClipboard()
    {
        if (!_selectionStartRow.HasValue || !_selectionStartCol.HasValue || 
            !_selectionEndRow.HasValue || !_selectionEndCol.HasValue || _emulator == null)
        {
            return;
        }

        var startRow = Math.Min(_selectionStartRow.Value, _selectionEndRow.Value);
        var endRow = Math.Max(_selectionStartRow.Value, _selectionEndRow.Value);
        var startCol = Math.Min(_selectionStartCol.Value, _selectionEndCol.Value);
        var endCol = Math.Max(_selectionStartCol.Value, _selectionEndCol.Value);

        var selectedText = new System.Text.StringBuilder();
        for (int row = startRow; row <= endRow; row++)
        {
            var lineText = new System.Text.StringBuilder();
            
            // Determine column range for this row
            int rowStartCol, rowEndCol;
            if (row == startRow && row == endRow)
            {
                // Single line selection
                rowStartCol = startCol;
                rowEndCol = endCol;
            }
            else if (row == startRow)
            {
                // First line - from startCol to end of line
                rowStartCol = startCol;
                rowEndCol = _emulator.Cols;
            }
            else if (row == endRow)
            {
                // Last line - from start of line to endCol
                rowStartCol = 0;
                rowEndCol = endCol;
            }
            else
            {
                // Middle lines - entire line
                rowStartCol = 0;
                rowEndCol = _emulator.Cols;
            }
            
            for (int col = rowStartCol; col < rowEndCol && col < _emulator.Cols; col++)
            {
                TerminalCell cell;
                // Unified buffer: rows 0 to (ScrollbackCount-1) are scrollback, rows ScrollbackCount to (ScrollbackCount+Rows-1) are current screen
                if (row < _emulator.ScrollbackLineCount)
                {
                    // This is scrollback
                    cell = _emulator.GetScrollbackCell(row, col);
                }
                else
                {
                    // This is current screen
                    var screenRow = row - _emulator.ScrollbackLineCount;
                    cell = _emulator.GetCell(screenRow, col);
                }
                lineText.Append(cell.Character);
            }
            
            if (lineText.Length > 0)
            {
                var lineStr = lineText.ToString();
                // Only trim trailing spaces, not all whitespace
                lineStr = lineStr.TrimEnd(' ');
                if (row < endRow)
                {
                    selectedText.AppendLine(lineStr);
                }
                else
                {
                    selectedText.Append(lineStr);
                }
            }
            else if (row < endRow)
            {
                selectedText.AppendLine();
            }
        }

        if (selectedText.Length > 0)
        {
            try
            {
                Clipboard.SetText(selectedText.ToString().TrimEnd());
            }
            catch
            {
            }
        }
    }

    private void PasteFromClipboard()
    {
        if (_connection == null || !_connection.IsConnected)
        {
            return;
        }

        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    _lastInputSentTime = DateTime.Now;
                    
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _connection.WriteAsync(text);
                        }
                        catch
                        {
                        }
                    });
                }
            }
        }
        catch
        {
        }
    }

    private void TerminalEmulator_TextInput(object sender, TextCompositionEventArgs e)
    {
        if (_connection == null || !_connection.IsConnected)
        {
            return;
        }

        // Clear selection when typing
        if (_hasSelection)
        {
            ClearSelection();
        }

        if (!string.IsNullOrEmpty(e.Text))
        {
            var text = e.Text;
            if (text == "\r" || text == "\n" || text == "\r\n")
            {
                e.Handled = true;
                return;
            }
            
            var modifiers = Keyboard.Modifiers;
            if (modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Alt))
            {
                e.Handled = true;
                return;
            }
            
            if (_resetScrollOnUserInput && _scrollOffset > 0)
            {
                ResetScrollPosition();
            }
            
            _lastInputSentTime = DateTime.Now;
            
            e.Handled = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _connection.WriteAsync(text);
                }
                catch
                {
                }
            });
        }
    }

    private void TerminalEmulator_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_connection == null || !_connection.IsConnected)
        {
            return;
        }

        var key = e.Key;
        var modifiers = Keyboard.Modifiers;
        string? sequence = null;

        if (key == Key.Tab && modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            var mainWindow = Window.GetWindow(this) as MainWindow;
            if (mainWindow != null)
            {
                if (modifiers.HasFlag(ModifierKeys.Shift))
                {
                    mainWindow.PreviousTab();
                }
                else
                {
                    mainWindow.NextTab();
                }
            }
            return;
        }

        if (key == Key.T && modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            var mainWindow = Window.GetWindow(this) as MainWindow;
            if (mainWindow != null)
            {
                mainWindow.NewTabMenuItem_Click(this, e);
            }
            return;
        }

        if (key == Key.W && modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            var mainWindow = Window.GetWindow(this) as MainWindow;
            if (mainWindow != null)
            {
                mainWindow.CloseTabMenuItem_Click(this, e);
            }
            return;
        }

        if (key == Key.C && (modifiers & ModifierKeys.Control) == ModifierKeys.Control && (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            if (_selectionStartRow.HasValue && _selectionStartCol.HasValue && _selectionEndRow.HasValue && _selectionEndCol.HasValue)
            {
                CopySelectionToClipboard();
            }
            e.Handled = true;
            return;
        }

        // Ctrl+Insert for copy (Windows Terminal behavior)
        if (key == Key.Insert && (modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (_selectionStartRow.HasValue && _selectionStartCol.HasValue && _selectionEndRow.HasValue && _selectionEndCol.HasValue)
            {
                CopySelectionToClipboard();
            }
            e.Handled = true;
            return;
        }

        if (key == Key.V && (modifiers & ModifierKeys.Control) == ModifierKeys.Control && (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }

        if (key == Key.Enter)
        {
            sequence = _lineEnding;
            e.Handled = true;
        }
        else if (key == Key.Back)
        {
            sequence = _backspaceKey == "CtrlH" ? "\b" : "\x7F";
            e.Handled = true;
        }
        else if (key == Key.Tab)
        {
            sequence = "\t";
            e.Handled = true;
        }
        else if (key == Key.Up)
        {
            sequence = "\x1B[A";
            e.Handled = true;
        }
        else if (key == Key.Down)
        {
            sequence = "\x1B[B";
            e.Handled = true;
        }
        else if (key == Key.Right)
        {
            sequence = "\x1B[C";
            e.Handled = true;
        }
        else if (key == Key.Left)
        {
            sequence = "\x1B[D";
            e.Handled = true;
        }
        else if (key == Key.Escape)
        {
            sequence = "\x1B";
            e.Handled = true;
        }
        else if (key == Key.Delete)
        {
            sequence = "\x1B[3~";
            e.Handled = true;
        }
        else if (key == Key.Insert)
        {
            sequence = "\x1B[2~";
            e.Handled = true;
        }
        else if (key == Key.Home)
        {
            sequence = "\x1B[H";
            e.Handled = true;
        }
        else if (key == Key.End)
        {
            sequence = "\x1B[F";
            e.Handled = true;
        }
        else if (key == Key.PageUp)
        {
            sequence = "\x1B[5~";
            e.Handled = true;
        }
        else if (key == Key.PageDown)
        {
            sequence = "\x1B[6~";
            e.Handled = true;
        }
        else if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control && (modifiers & ModifierKeys.Alt) != ModifierKeys.Alt)
        {
            var controlChar = GetControlCharacter(key);
            if (controlChar.HasValue)
            {
                sequence = new string(new[] { controlChar.Value });
                e.Handled = true;
            }
        }

        if (sequence != null)
        {
            if (_resetScrollOnUserInput && _scrollOffset > 0)
            {
                ResetScrollPosition();
            }
            
            _lastInputSentTime = DateTime.Now;
            
            var seq = sequence;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _connection.WriteAsync(seq);
                }
                catch
                {
                }
            });
        }
    }

    private char? GetControlCharacter(Key key)
    {
        return key switch
        {
            Key.A => '\x01',
            Key.B => '\x02',
            Key.C => '\x03',
            Key.D => '\x04',
            Key.E => '\x05',
            Key.F => '\x06',
            Key.G => '\x07',
            Key.H => '\x08',
            Key.I => '\x09',
            Key.J => '\x0A',
            Key.K => '\x0B',
            Key.L => '\x0C',
            Key.M => '\x0D',
            Key.N => '\x0E',
            Key.O => '\x0F',
            Key.P => '\x10',
            Key.Q => '\x11',
            Key.R => '\x12',
            Key.S => '\x13',
            Key.T => '\x14',
            Key.U => '\x15',
            Key.V => '\x16',
            Key.W => '\x17',
            Key.X => '\x18',
            Key.Y => '\x19',
            Key.Z => '\x1A',
            Key.D0 => '\x1E',
            Key.D1 => '\x1F',
            Key.D2 => '\x00',
            Key.D3 => '\x1B',
            Key.D4 => '\x1C',
            Key.D5 => '\x1D',
            Key.D6 => '\x1E',
            Key.D7 => '\x1F',
            Key.D8 => '\x7F',
            Key.D9 => '\x1F',
            Key.OemMinus => '\x1F',
            Key.OemPlus => '\x1F',
            _ => null
        };
    }

    private async void TerminalEmulator_KeyDown(object sender, KeyEventArgs e)
    {
        if (_connection == null || !_connection.IsConnected || e.Handled)
        {
            return;
        }

        var key = e.Key;
        string? sequence = null;

        if (key == Key.F1)
        {
            sequence = "\x1BOP";
        }
        else if (key == Key.F2)
        {
            sequence = "\x1BOQ";
        }
        else if (key == Key.F3)
        {
            sequence = "\x1BOR";
        }
        else if (key == Key.F4)
        {
            sequence = "\x1BOS";
        }
        else if (key == Key.F5)
        {
            sequence = "\x1B[15~";
        }
        else if (key == Key.F6)
        {
            sequence = "\x1B[17~";
        }
        else if (key == Key.F7)
        {
            sequence = "\x1B[18~";
        }
        else if (key == Key.F8)
        {
            sequence = "\x1B[19~";
        }
        else if (key == Key.F9)
        {
            sequence = "\x1B[20~";
        }
        else if (key == Key.F10)
        {
            sequence = "\x1B[21~";
        }
        else if (key == Key.F11)
        {
            sequence = "\x1B[23~";
        }
        else if (key == Key.F12)
        {
            sequence = "\x1B[24~";
        }

        if (sequence != null)
        {
            // Clear selection when sending keys to terminal
            if (_hasSelection)
            {
                ClearSelection();
            }
            
            if (_resetScrollOnUserInput && _scrollOffset > 0)
            {
                ResetScrollPosition();
            }
            
            _lastInputSentTime = DateTime.Now;
            
            try
            {
                await _connection.WriteAsync(sequence);
                e.Handled = true;
            }
            catch
            {
            }
        }
    }

    private void TerminalEmulator_KeyUp(object sender, KeyEventArgs e)
    {
    }

    private void TerminalEmulator_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_emulator == null)
        {
            return;
        }

        // Clear selection when scrolling
        if (_hasSelection)
        {
            ClearSelection();
        }

        // Scroll by SCROLL_LINES_PER_TICK lines per wheel tick
        // e.Delta > 0 means scroll up (see older content), e.Delta < 0 means scroll down (see newer content)
        var delta = e.Delta > 0 ? SCROLL_LINES_PER_TICK : -SCROLL_LINES_PER_TICK;
        var totalRows = _emulator.Rows + _emulator.ScrollbackLineCount;
        var maxScroll = Math.Max(0, totalRows - _emulator.Rows);
        var newScrollOffset = Math.Max(0, Math.Min(_scrollOffset + delta, maxScroll));
        if (newScrollOffset != _scrollOffset)
        {
            _scrollOffset = newScrollOffset;
            UpdateScrollBar();
            UpdateCanvasTransform();
            // Render immediately for responsive scrolling, don't throttle
        RenderScreen();
    }
        e.Handled = true;
    }
    
    private void UpdateCanvasHeight()
    {
        if (_emulator == null || _charHeight == 0)
        {
            return;
        }
        
        // Canvas should only be as tall as the visible viewport, not the entire scrollback
        // This prevents it from extending beyond the container
        TerminalCanvas.Height = ActualHeight;
        UpdateScrollBar();
        UpdateCanvasTransform();
    }
    
    private void UpdateCanvasTransform()
    {
        if (_emulator == null || _charHeight == 0)
        {
            return;
        }
        
        // Canvas is now only viewport-sized, so we don't need translation
        // Instead, we render the correct rows at their correct positions
        // No transform needed - rendering handles the scroll offset
        TerminalCanvas.RenderTransform = null;
    }
    
    private void ResetScrollPosition()
    {
        if (_scrollOffset == 0)
        {
            return;
        }
        
        _scrollOffset = 0;
        UpdateScrollBar();
        UpdateCanvasTransform();
        RenderScreen();
    }
    
    private void UpdateScrollBar()
    {
        if (_emulator == null || _charHeight == 0)
        {
            TerminalScrollBar.Visibility = Visibility.Collapsed;
            return;
        }
        
        var totalRows = _emulator.Rows + _emulator.ScrollbackLineCount;
        var visibleRows = (int)(ActualHeight / _charHeight);
        var scrollbackCount = _emulator.ScrollbackLineCount;
        var maxScroll = Math.Max(0, totalRows - _emulator.Rows);
        
        if (maxScroll > 0 && totalRows > 0)
        {
            TerminalScrollBar.Maximum = maxScroll;
            TerminalScrollBar.ViewportSize = Math.Max(1, visibleRows);
            TerminalScrollBar.Value = maxScroll - _scrollOffset;
            TerminalScrollBar.Visibility = Visibility.Visible;
        }
        else
        {
            TerminalScrollBar.Visibility = Visibility.Collapsed;
        }
    }
    
    private void TerminalScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_emulator == null)
        {
            return;
        }
        
        var totalRows = _emulator.Rows + _emulator.ScrollbackLineCount;
        var maxScroll = Math.Max(0, totalRows - _emulator.Rows);
        var newScrollOffset = maxScroll - (int)e.NewValue;
        
        if (newScrollOffset != _scrollOffset)
        {
            _scrollOffset = newScrollOffset;
            UpdateCanvasTransform();
            RenderScreen();
        }
    }
    
    private void TerminalScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        if (_emulator == null)
        {
            return;
        }
        
        var totalRows = _emulator.Rows + _emulator.ScrollbackLineCount;
        var maxScroll = Math.Max(0, totalRows - _emulator.Rows);
        var newScrollOffset = maxScroll - (int)TerminalScrollBar.Value;
        
        if (newScrollOffset != _scrollOffset)
        {
            _scrollOffset = newScrollOffset;
            UpdateCanvasTransform();
            RenderScreen();
        }
    }
    

    private static string ConvertLineEndingString(string lineEnding)
    {
        if (string.IsNullOrEmpty(lineEnding))
        {
            return "\n";
        }

        if (lineEnding == "\\n" || lineEnding == @"\n")
        {
            return "\n";
        }

        if (lineEnding == "\\r\\n" || lineEnding == @"\r\n")
        {
            return "\r\n";
        }

        if (lineEnding == "\n" || lineEnding == "\r\n")
        {
            return lineEnding;
        }

        return "\n";
    }
}

