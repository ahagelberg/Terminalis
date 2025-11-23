using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
    private const int RENDER_THROTTLE_MS = 16;

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
    private Point? _selectionStart;
    private Point? _selectionEnd;
    private Rectangle? _selectionOverlay;
    private Rectangle? _lineFlashOverlay;
    private string _lineEnding = "\n";

    public TerminalEmulator()
    {
        InitializeComponent();
        Loaded += TerminalEmulator_Loaded;
        SizeChanged += TerminalEmulator_SizeChanged;
        PreviewKeyDown += TerminalEmulator_PreviewKeyDown;
        TextInput += TerminalEmulator_TextInput;
        PreviewMouseDown += TerminalEmulator_PreviewMouseDown;
        PreviewMouseMove += TerminalEmulator_PreviewMouseMove;
        PreviewMouseUp += TerminalEmulator_PreviewMouseUp;
        KeyDown += TerminalEmulator_KeyDown;
        KeyUp += TerminalEmulator_KeyUp;
        MouseWheel += TerminalEmulator_MouseWheel;
        ContextMenuOpening += TerminalEmulator_ContextMenuOpening;
        Focusable = true;
        
        _renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(RENDER_THROTTLE_MS)
        };
        _renderTimer.Tick += RenderTimer_Tick;
    }

    public void AttachConnection(ITerminalConnection connection, string? lineEnding = null, string? fontFamily = null, double? fontSize = null, string? foregroundColor = null, string? backgroundColor = null, string? bellNotification = null)
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
        RenderScreen();
        Focus();
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

    public void UpdateSettings(string? lineEnding = null, string? fontFamily = null, double? fontSize = null, string? foregroundColor = null, string? backgroundColor = null, string? bellNotification = null)
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
            TerminalCanvas.Height = rows * _charHeight;
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
        System.Console.WriteLine($"[Server] Raw data received: {EscapeString(data)}");
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_emulator != null)
            {
                _emulator.ProcessData(data);
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

        var selectionOverlay = _selectionOverlay;
        var lineFlashOverlay = _lineFlashOverlay;
        TerminalCanvas.Children.Clear();
        _selectionOverlay = null;
        _lineFlashOverlay = null;

        var startRow = Math.Max(0, _scrollOffset);
        var endRow = Math.Min(_emulator.Rows, startRow + (int)(ActualHeight / _charHeight) + 1);

        for (int row = startRow; row < endRow; row++)
        {
            RenderLine(row);
        }

        if (_scrollOffset == 0)
        {
            RenderCursor();
        }

        if (selectionOverlay != null && _isSelecting)
        {
            _selectionOverlay = selectionOverlay;
            TerminalCanvas.Children.Add(_selectionOverlay);
            UpdateSelection();
        }

        if (lineFlashOverlay != null && lineFlashOverlay.Visibility == Visibility.Visible)
        {
            _lineFlashOverlay = lineFlashOverlay;
            TerminalCanvas.Children.Add(_lineFlashOverlay);
        }

        _previousCursorRow = currentCursorRow;
        _previousCursorCol = currentCursorCol;
    }

    private void RenderLine(int row)
    {
        if (_emulator == null || _typeface == null)
        {
            return;
        }

        var y = (row - _scrollOffset) * _charHeight;
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

        for (int col = 0; col < _emulator.Cols; col++)
        {

            TerminalCell cell;
            if (row < _emulator.Rows)
            {
                cell = _emulator.GetCell(row, col);
            }
            else
            {
                var scrollbackRow = row - _emulator.Rows;
                cell = _emulator.GetScrollbackCell(scrollbackRow, col);
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
                    RenderTextSegment(x, y, textRun, currentFg, currentBg, currentBold, currentItalic, currentUnderline, currentFaint, currentCrossedOut, currentDoubleUnderline, currentOverline, currentConceal);
                    x += textRun.Length * _charWidth;
                }

                if (col < _emulator.Cols - 1)
                {
                    textRun = cell.Character.ToString();
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
            RenderTextSegment(x, y, textRun, currentFg, currentBg, currentBold, currentItalic, currentUnderline, currentFaint, currentCrossedOut, currentDoubleUnderline, currentOverline, currentConceal);
        }
    }

    private void RenderTextSegment(double x, double y, string text, int fgColor, int bgColor, bool bold, bool italic, bool underline, bool faint, bool crossedOut, bool doubleUnderline, bool overline, bool conceal)
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
            Fill = background
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
            1 => new SolidColorBrush(Color.FromRgb(128, 0, 0)),
            2 => new SolidColorBrush(Color.FromRgb(0, 128, 0)),
            3 => new SolidColorBrush(Color.FromRgb(128, 128, 0)),
            4 => new SolidColorBrush(Color.FromRgb(0, 0, 128)),
            5 => new SolidColorBrush(Color.FromRgb(128, 0, 128)),
            6 => new SolidColorBrush(Color.FromRgb(0, 128, 128)),
            7 => new SolidColorBrush(Color.FromRgb(192, 192, 192)),
            8 => new SolidColorBrush(Color.FromRgb(128, 128, 128)),
            9 => new SolidColorBrush(Color.FromRgb(255, 0, 0)),
            10 => new SolidColorBrush(Color.FromRgb(0, 255, 0)),
            11 => new SolidColorBrush(Color.FromRgb(255, 255, 0)),
            12 => new SolidColorBrush(Color.FromRgb(0, 0, 255)),
            13 => new SolidColorBrush(Color.FromRgb(255, 0, 255)),
            14 => new SolidColorBrush(Color.FromRgb(0, 255, 255)),
            15 => new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            _ => Brushes.White
        };
    }

    private void TerminalEmulator_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isSelecting = true;
            var pos = e.GetPosition(TerminalCanvas);
            _selectionStart = pos;
            _selectionEnd = pos;
            ClearSelection();
            UpdateSelection();
        }
        else if (e.RightButton == MouseButtonState.Pressed)
        {
            if (!_isSelecting)
            {
                PasteFromClipboard();
                e.Handled = true;
            }
        }
    }

    private void TerminalEmulator_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isSelecting && e.LeftButton == MouseButtonState.Pressed)
        {
            var pos = e.GetPosition(TerminalCanvas);
            _selectionEnd = pos;
            UpdateSelection();
        }
    }

    private void TerminalEmulator_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released && _isSelecting)
        {
            _isSelecting = false;
            if (_selectionStart.HasValue && _selectionEnd.HasValue)
            {
                CopySelectionToClipboard();
            }
        }
    }

    private void ClearSelection()
    {
        if (_selectionOverlay != null)
        {
            TerminalCanvas.Children.Remove(_selectionOverlay);
            _selectionOverlay = null;
        }
        _selectionStart = null;
        _selectionEnd = null;
    }

    private void TerminalEmulator_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = new ContextMenu();
        var pasteItem = new MenuItem { Header = "Paste" };
        pasteItem.Click += (s, args) => PasteFromClipboard();
        menu.Items.Add(pasteItem);
        ContextMenu = menu;
    }

    private void UpdateSelection()
    {
        if (!_selectionStart.HasValue || !_selectionEnd.HasValue || _emulator == null || _charWidth == 0 || _charHeight == 0)
        {
            return;
        }

        var startX = Math.Min(_selectionStart.Value.X, _selectionEnd.Value.X);
        var endX = Math.Max(_selectionStart.Value.X, _selectionEnd.Value.X);
        var startY = Math.Min(_selectionStart.Value.Y, _selectionEnd.Value.Y);
        var endY = Math.Max(_selectionStart.Value.Y, _selectionEnd.Value.Y);

        if (_selectionOverlay == null)
        {
            _selectionOverlay = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                Stroke = Brushes.White,
                StrokeThickness = 1
            };
            TerminalCanvas.Children.Add(_selectionOverlay);
        }

        Canvas.SetLeft(_selectionOverlay, startX);
        Canvas.SetTop(_selectionOverlay, startY);
        _selectionOverlay.Width = endX - startX;
        _selectionOverlay.Height = endY - startY;
    }

    private void CopySelectionToClipboard()
    {
        if (!_selectionStart.HasValue || !_selectionEnd.HasValue || _emulator == null || _charWidth == 0 || _charHeight == 0)
        {
            return;
        }

        var startCol = Math.Max(0, (int)(Math.Min(_selectionStart.Value.X, _selectionEnd.Value.X) / _charWidth));
        var endCol = Math.Min(_emulator.Cols, (int)(Math.Max(_selectionStart.Value.X, _selectionEnd.Value.X) / _charWidth) + 1);
        var startRow = Math.Max(0, (int)(Math.Min(_selectionStart.Value.Y, _selectionEnd.Value.Y) / _charHeight) + _scrollOffset);
        var endRow = Math.Min(_emulator.Rows + _emulator.ScrollbackLineCount, (int)(Math.Max(_selectionStart.Value.Y, _selectionEnd.Value.Y) / _charHeight) + _scrollOffset + 1);

        var selectedText = new System.Text.StringBuilder();
        for (int row = startRow; row < endRow; row++)
        {
            var lineText = new System.Text.StringBuilder();
            for (int col = startCol; col < endCol; col++)
            {
                TerminalCell cell;
                if (row < _emulator.Rows)
                {
                    cell = _emulator.GetCell(row, col);
                }
                else
                {
                    var scrollbackRow = row - _emulator.Rows;
                    cell = _emulator.GetScrollbackCell(scrollbackRow, col);
                }
                lineText.Append(cell.Character);
            }
            if (lineText.Length > 0)
            {
                selectedText.AppendLine(lineText.ToString().TrimEnd());
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

        if (!string.IsNullOrEmpty(e.Text))
        {
            var text = e.Text;
            if (text == "\r" || text == "\n" || text == "\r\n")
            {
                e.Handled = true;
                return;
            }
            
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                return;
            }
            
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
            if (_selectionStart.HasValue && _selectionEnd.HasValue)
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
            sequence = "\b";
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
        else if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
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

        var delta = e.Delta > 0 ? -3 : 3;
        _scrollOffset = Math.Max(0, Math.Min(_scrollOffset + delta, _emulator.ScrollbackLineCount));
        RenderScreen();
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

