using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

namespace TabbySSH.Views
{
    // Helper class to host DrawingVisual in Canvas
    public class VisualHost : FrameworkElement
    {
        private Visual? _visual;

        public VisualHost(Visual visual)
        {
            _visual = visual;
            AddVisualChild(_visual);
        }

        protected override int VisualChildrenCount => _visual != null ? 1 : 0;

        protected override Visual GetVisualChild(int index)
        {
            if (_visual == null || index != 0)
                throw new ArgumentOutOfRangeException();
            return _visual;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // Cannot return PositiveInfinity - return a large but finite size if needed
            var width = double.IsPositiveInfinity(availableSize.Width) ? 10000 : availableSize.Width;
            var height = double.IsPositiveInfinity(availableSize.Height) ? 10000 : availableSize.Height;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            return finalSize;
        }
    }

    public partial class TerminalEmulator : UserControl
{
    private const int DEFAULT_FONT_SIZE = 12;
    private const int DEFAULT_SCROLLBACK_LINES = 20000;
    private const int SCROLL_LINES_PER_TICK = 3;
    private const int ECHO_DETECTION_WINDOW_MS = 100; // Time window to detect echoed user input
    private const int MAX_RAW_BUFFER_LINES = 5000;
    /// <summary>Max length of a single raw buffer line to avoid freeze when server sends no newlines (e.g. alt-screen binary/escape stream).</summary>
    private const int MAX_RAW_LINE_LENGTH = 65536;
    /// <summary>WPF GlyphRun allows at most 65535 glyphs; chunk long lines to avoid ArgumentException.</summary>
    private const int MAX_GLYPH_RUN_CHARS = 60000;

    private Vt100Emulator? _emulator;
    private ITerminalConnection? _connection;
    private Typeface? _typeface;
    private GlyphTypeface? _glyphTypeface;
    private double _charWidth;
    private double _charHeight;
    private double _fontSize = DEFAULT_FONT_SIZE;
    private int _scrollOffset = 0;
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
    private bool _allowTitleChange = false;
    private readonly List<string> _rawBufferLines = new();
    private readonly System.Text.StringBuilder _rawBufferPending = new();
    private readonly object _rawBufferLock = new object();

    /// <summary>Coalesce received data so one UI callback processes all pending data (avoids freeze when server sends burst e.g. alt-screen).</summary>
    private readonly System.Text.StringBuilder _pendingProcessData = new System.Text.StringBuilder();
    private bool _processScheduled;
    private readonly object _pendingProcessLock = new object();

    /// <summary>Raised when raw buffer is appended (on receiving thread). Subscribe to update a raw output window.</summary>
    public event EventHandler? RawBufferUpdated;

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
        PreviewMouseWheel += TerminalEmulator_PreviewMouseWheel;
        ContextMenuOpening += TerminalEmulator_ContextMenuOpening;
        IsVisibleChanged += TerminalEmulator_IsVisibleChanged;
        ContextMenu = null;
        Focusable = true;
        
    }
    

    public void AttachConnection(ITerminalConnection connection, string? lineEnding = null, string? fontFamily = null, double? fontSize = null, string? foregroundColor = null, string? backgroundColor = null, string? bellNotification = null, bool resetScrollOnUserInput = true, bool resetScrollOnServerOutput = false, string? backspaceKey = null, bool allowTitleChange = false)
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
        _allowTitleChange = allowTitleChange;

        if (_emulator != null)
        {
            _emulator.Bell -= OnBell;
            _emulator.TitleChanged -= OnTitleChanged;
        }

        _emulator = new Vt100Emulator();
        _emulator.SetScrollbackLimit(DEFAULT_SCROLLBACK_LINES);
        _emulator.Bell += OnBell;
        _emulator.TitleChanged += OnTitleChanged;
        lock (_rawBufferLock)
        {
            _rawBufferLines.Clear();
            _rawBufferPending.Clear();
        }

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
        
        // Send initial size when connection is attached
        if (_connection != null && _connection.IsConnected)
        {
            SendTerminalSizeToServer(force: true);
        }
        
        Dispatcher.BeginInvoke(new Action(() => Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void SendTerminalSizeToServer(bool force = false)
    {
        if (_emulator == null || _connection == null || !_connection.IsConnected)
            return;
        if (_emulator.Cols <= 1 || _emulator.Rows <= 1)
            return;
        if (_connection is SshConnection sshConn)
            sshConn.ResizeTerminal(_emulator.Cols, _emulator.Rows);
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
        // DPI might have changed, invalidate cache
        _dpiCached = false;
        
        if (_emulator != null && _charWidth > 0 && _charHeight > 0)
        {
            var oldCols = _emulator.Cols;
            var oldRows = _emulator.Rows;
            
            UpdateTerminalSize();
            
            if (_connection != null && _connection.IsConnected && (oldCols != _emulator.Cols || oldRows != _emulator.Rows))
            {
                SendTerminalSizeToServer(force: true);
            }
            
            RenderScreen();
        }
    }
    

    private void InitializeFont(string fontFamily = "Consolas", double fontSize = DEFAULT_FONT_SIZE)
    {
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(fontFamily) || !installed.Contains(fontFamily))
            fontFamily = "Consolas";
        _typeface = new Typeface(new FontFamily(fontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _glyphTypeface = _typeface.TryGetGlyphTypeface(out var gt) ? gt : null;
        _dpiCached = false;
        var formattedText = new FormattedText("M", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, fontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _charWidth = formattedText.Width;
        _charHeight = formattedText.Height;
        _charHeightSnapped = _charHeight;
        _charWidthSnapped = _charWidth;
        _fontSize = fontSize;
    }

    private void EnsureSnappedMetrics()
    {
        if (_charHeight <= 0 || _charWidth <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _cachedDpi = dpi;
        _dpiCached = true;
        _charHeightSnapped = Math.Round(_charHeight * dpi) / dpi;
        _charWidthSnapped = Math.Round(_charWidth * dpi) / dpi;
    }

    private void EnsureDpiCached()
    {
        if (!_dpiCached)
        {
            _cachedDpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            _dpiCached = true;
        }
    }

    public void UpdateSettings(string? lineEnding = null, string? fontFamily = null, double? fontSize = null, string? foregroundColor = null, string? backgroundColor = null, string? bellNotification = null, bool? resetScrollOnUserInput = null, bool? resetScrollOnServerOutput = null, string? backspaceKey = null, bool? allowTitleChange = null)
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
        
        if (allowTitleChange.HasValue)
        {
            _allowTitleChange = allowTitleChange.Value;
        }
        
        if (fontChanged)
        {
            ClearRenderedLines();
            UpdateTerminalSize();
            SendTerminalSizeToServer(force: true);
        }
        
        RenderScreen();
    }

    private void ClearRenderedLines()
    {
        foreach (var kvp in _renderedLines)
        {
            if (TerminalCanvas.Children.Contains(kvp.Value))
                TerminalCanvas.Children.Remove(kvp.Value);
        }
        _renderedLines.Clear();
        _lastStartLineIndex = -1;
        _lastEndLineIndex = -1;
        _lastViewportOffset = 0;
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

    private const int DEFAULT_TERMINAL_COLS = 80;
    private const int DEFAULT_TERMINAL_ROWS = 24;

    public void UpdateTerminalSize()
    {
        if (_emulator == null || _charWidth == 0 || _charHeight == 0)
        {
            return;
        }

        // When tab is not visible, ActualWidth/ActualHeight can be 0 or very small (control not laid out).
        // Use default size so we don't send 1x1 to the server; size will be corrected when tab is shown.
        var availableWidth = ActualWidth;
        var availableHeight = ActualHeight;

        // Vertical scrollbar is always visible (disabled when not needed) so reserve its width
        if (TerminalScrollBar != null && TerminalScrollBar.ActualWidth > 0)
            availableWidth -= TerminalScrollBar.ActualWidth;

        int cols;
        int rows;
        if (availableWidth < _charWidth * 2 || availableHeight < _charHeight * 2)
        {
            cols = DEFAULT_TERMINAL_COLS;
            rows = DEFAULT_TERMINAL_ROWS;
        }
        else
        {
            cols = Math.Max(1, (int)Math.Floor(availableWidth / _charWidth));
            rows = Math.Max(1, (int)Math.Floor(availableHeight / _charHeight));
        }
        _emulator.SetSize(cols, rows);
        
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TerminalCanvas.Width = cols * _charWidth;
            UpdateCanvasHeight();
        }));
    }

    public void UpdateFont(string fontFamily, double fontSize)
    {
        InitializeFont(fontFamily, fontSize);
        ClearRenderedLines();
        UpdateTerminalSize();
        _colorCache.Clear();
        RenderScreen();
    }

    private void OnDataReceived(object? sender, string data)
    {
        // Append to raw buffer immediately on receiving thread so raw mode keeps updating.
        AppendRawBufferLines(data);

        // Coalesce: one UI callback drains all pending data (avoids freeze when server sends burst e.g. alt-screen).
        bool shouldSchedule = false;
        lock (_pendingProcessLock)
        {
            _pendingProcessData.Append(data);
            if (!_processScheduled)
            {
                _processScheduled = true;
                shouldSchedule = true;
            }
        }
        if (shouldSchedule)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ProcessPendingData));
    }

    private void ProcessPendingData()
    {
        string toProcess;
        lock (_pendingProcessLock)
        {
            toProcess = _pendingProcessData.ToString();
            _pendingProcessData.Clear();
            _processScheduled = false;
        }

        if (_emulator == null || toProcess.Length == 0) return;

        _emulator.ProcessData(toProcess);

        if (_resetScrollOnServerOutput && _scrollOffset > 0)
        {
            var timeSinceLastInput = (DateTime.Now - _lastInputSentTime).TotalMilliseconds;
            if (timeSinceLastInput > ECHO_DETECTION_WINDOW_MS)
                ResetScrollPosition();
        }

        UpdateCanvasHeight();
        if (_scrollOffset == 0)
            UpdateCanvasTransform();

        if (!_renderPending)
        {
            _renderPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _renderPending = false;
                RenderScreen();
            }));
        }
    }

    private void OnConnectionClosed(object? sender, ConnectionClosedEventArgs e)
    {
        // Connection closed; handler kept for event subscription. UI updates happen via existing flow.
    }

    /// <summary>Thread-safe: append received data to the raw buffer. Callable from any thread.</summary>
    private void AppendRawBufferLines(string data)
    {
        lock (_rawBufferLock)
        {
            _rawBufferPending.Append(data);
            var full = _rawBufferPending.ToString();
            _rawBufferPending.Clear();
            var parts = full.Split('\n');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                AddRawLine(parts[i].TrimEnd('\r'));
            }
            if (parts.Length > 0)
                _rawBufferPending.Append(parts[parts.Length - 1]);

            // Cap pending line length so one line can't grow unbounded (e.g. alt-screen with no newlines).
            while (_rawBufferPending.Length > MAX_RAW_LINE_LENGTH)
            {
                var chunk = new System.Text.StringBuilder(MAX_RAW_LINE_LENGTH);
                for (int i = 0; i < MAX_RAW_LINE_LENGTH; i++)
                    chunk.Append(_rawBufferPending[i]);
                _rawBufferPending.Remove(0, MAX_RAW_LINE_LENGTH);
                AddRawLine(chunk.ToString());
            }
        }
        RawBufferUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Thread-safe snapshot of raw buffer for the raw output window.</summary>
    public (IReadOnlyList<string> lines, string pending) GetRawBufferSnapshot()
    {
        lock (_rawBufferLock)
        {
            var lines = new List<string>(_rawBufferLines);
            var pending = _rawBufferPending.ToString();
            return (lines, pending);
        }
    }

    private void AddRawLine(string line)
    {
        _rawBufferLines.Add(line);
        while (_rawBufferLines.Count > MAX_RAW_BUFFER_LINES)
            _rawBufferLines.RemoveAt(0);
    }

    /// <summary>Escape control characters for display in raw output window. Public for RawOutputWindow.</summary>
    public static string EscapeRawLineForDisplay(string raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in raw)
        {
            switch (c)
            {
                case '\x1B': sb.Append("\\x1B"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\x07': sb.Append("\\a"); break;
                default:
                    if (c >= 32 && c < 127) sb.Append(c);
                    else sb.Append($"\\u{(int)c:X4}");
                    break;
            }
        }
        return sb.ToString();
    }

    private bool _renderPending = false;
    private int _previousCursorRow = -1;
    private int _previousCursorCol = -1;
    private readonly Dictionary<int, Brush> _colorCache = new Dictionary<int, Brush>();
    private readonly System.Text.StringBuilder _textRunBuilder = new System.Text.StringBuilder();
    private double _cachedDpi = 96.0;
    private bool _dpiCached = false;
    private double _charHeightSnapped;
    private double _charWidthSnapped;

    // Incremental rendering: cache rendered line visuals
    private readonly Dictionary<int, VisualHost> _renderedLines = new Dictionary<int, VisualHost>();
    private VisualHost? _cursorVisual = null;
    private int _lastStartLineIndex = -1;
    private int _lastEndLineIndex = -1;
    private int _lastViewportOffset = 0;

    public event EventHandler<string>? TitleChanged;

    private void OnTitleChanged(object? sender, string title)
    {
        // Only fire the event if title changes are allowed by the session setting
        if (_allowTitleChange)
        {
            TitleChanged?.Invoke(this, title);
        }
    }

    private void TerminalEmulator_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is bool isVisible && isVisible)
        {
            // Tab just became visible; defer size update until after layout so ActualWidth/Height are valid.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                UpdateTerminalSize();
                SendTerminalSizeToServer(force: true);
                RenderScreen();
            }));
        }
    }

    private void OnBell(object? sender, EventArgs e)
    {
        var setting = _bellNotification;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (setting?.ToLowerInvariant())
            {
                case "sound":
                    System.Media.SystemSounds.Beep.Play();
                    break;
                case "flash":
                    FlashWindow();
                    break;
                case "line flash":
                    FlashCurrentLine();
                    break;
                case "none":
                    break;
                default:
                    FlashWindow();
                    break;
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

    private static Color ParseColorOrDefault(string? colorName, Color defaultColor)
    {
        if (string.IsNullOrEmpty(colorName)) return defaultColor;
        try
        {
            var brush = new BrushConverter().ConvertFromString(colorName) as SolidColorBrush;
            return brush?.Color ?? defaultColor;
        }
        catch
        {
            return defaultColor;
        }
    }

    private Color GetBackgroundColor() => ParseColorOrDefault(_customBackgroundColor, Colors.Black);
    private Color GetForegroundColor() => ParseColorOrDefault(_customForegroundColor, Colors.LightGray);

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
            EnsureSnappedMetrics();
            var y = cursorRow * _charHeightSnapped;

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
            _lineFlashOverlay.Height = _charHeightSnapped;
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


    // Renderer methods are read-only - they only read from the buffer and create UI elements.
    // The buffer is only modified by server output via OnDataReceived -> _emulator.ProcessData()
    private TerminalLine? GetLineForRender(int lineIndex)
    {
        return _emulator?.GetLine(lineIndex);
    }

    private readonly struct ViewportRange
    {
        public int StartLineIndex { get; }
        public int EndLineIndex { get; }
        public int ViewportOffset { get; }

        public ViewportRange(int start, int end, int offset)
        {
            StartLineIndex = start;
            EndLineIndex = end;
            ViewportOffset = offset;
        }
    }

    private ViewportRange ComputeViewportRange(int totalLines, int visibleRows)
    {
        int effectiveLines = _emulator != null && _emulator.InAlternateScreen ? _emulator.Rows : totalLines;
        if (effectiveLines <= visibleRows)
            return new ViewportRange(0, effectiveLines, 0);
        int start = Math.Max(0, effectiveLines - visibleRows - _scrollOffset);
        int end = Math.Min(effectiveLines, start + visibleRows);
        int offset = _scrollOffset == 0 ? visibleRows - (end - start) : 0;
        return new ViewportRange(start, end, offset);
    }

    private void RenderScreen()
    {
        if (_typeface == null) return;

        var totalLines = GetDisplayLineCount();
        if (totalLines == 0) return;

        EnsureSnappedMetrics();
        int visibleRows = GetVisibleRowCount();
        var range = ComputeViewportRange(totalLines, visibleRows);
        int startLineIndex = range.StartLineIndex;
        int endLineIndex = range.EndLineIndex;
        int viewportOffset = range.ViewportOffset;

        // Canvas width: same logic for both modes - max visible line length
        var viewportWidth = TerminalScrollViewer?.ActualWidth > 0 ? TerminalScrollViewer.ActualWidth : ActualWidth;
        int maxLen = 0;
        for (int i = startLineIndex; i < endLineIndex; i++)
        {
            var line = GetLineForRender(i);
            if (line != null && line.Cells.Count > maxLen)
                maxLen = line.Cells.Count;
        }
        TerminalCanvas.Width = Math.Max(viewportWidth, maxLen * _charWidthSnapped);

        var currentCursorRow = _emulator?.CursorRow ?? 0;
        var currentCursorCol = _emulator?.CursorCol ?? 0;
        bool viewportChanged = _lastStartLineIndex != startLineIndex || _lastEndLineIndex != endLineIndex || _lastViewportOffset != viewportOffset;

        if (viewportChanged)
        {
            HandleViewportChange(startLineIndex, endLineIndex, viewportOffset, visibleRows);
            _lastStartLineIndex = startLineIndex;
            _lastEndLineIndex = endLineIndex;
            _lastViewportOffset = viewportOffset;
        }

        bool selectionActive = _hasSelection || _isSelecting;
        bool useFullRange = viewportChanged || _renderedLines.Count == 0 || selectionActive;
        var linesToRender = useFullRange
            ? Enumerable.Range(startLineIndex, endLineIndex - startLineIndex).ToHashSet()
            : (_emulator?.GetDirtyLines() ?? Enumerable.Empty<int>().ToHashSet());

        foreach (var lineIndex in linesToRender)
        {
            if (lineIndex >= startLineIndex && lineIndex < endLineIndex)
                RenderLineIncremental(lineIndex, startLineIndex, viewportOffset);
        }
        _emulator?.ClearDirtyLines();

        if (_emulator != null)
        {
            UpdateCursor(currentCursorRow, currentCursorCol, startLineIndex, viewportOffset);
            _previousCursorRow = currentCursorRow;
            _previousCursorCol = currentCursorCol;
        }
    }

    private int GetVisibleRowCount()
    {
        return Math.Max(1, (int)(ActualHeight / _charHeightSnapped));
    }

    private void HandleViewportChange(int startLineIndex, int endLineIndex, int viewportOffset, int visibleRows)
    {
        // Remove lines that are no longer visible
        var linesToRemove = new List<int>();
        foreach (var kvp in _renderedLines)
        {
            if (kvp.Key < startLineIndex || kvp.Key >= endLineIndex)
            {
                if (TerminalCanvas.Children.Contains(kvp.Value))
                {
                    TerminalCanvas.Children.Remove(kvp.Value);
                }
                linesToRemove.Add(kvp.Key);
            }
        }
        foreach (var lineIndex in linesToRemove)
        {
            _renderedLines.Remove(lineIndex);
        }

        // Update positions of existing lines using transforms
        foreach (var kvp in _renderedLines)
        {
            var lineIndex = kvp.Key;
            var visual = kvp.Value;
            var viewportRow = (lineIndex - startLineIndex) + viewportOffset;
            var y = viewportRow * _charHeightSnapped;
            Canvas.SetTop(visual, y);
        }
    }

    private void RenderLineIncremental(int lineIndex, int startLineIndex, int viewportOffset)
    {
        var viewportRow = (lineIndex - startLineIndex) + viewportOffset;
        var y = viewportRow * _charHeightSnapped;

        // Remove old visual if it exists
        if (_renderedLines.TryGetValue(lineIndex, out var oldVisual))
        {
            if (TerminalCanvas.Children.Contains(oldVisual))
            {
                TerminalCanvas.Children.Remove(oldVisual);
            }
        }

        // Create new visual for this line
        var drawingVisual = new DrawingVisual();
        DrawingContext? dc = null;
        try
        {
            dc = drawingVisual.RenderOpen();
            RenderLine(dc, lineIndex, viewportRow);
        }
        finally
        {
            dc?.Close();
        }

        // Add to canvas
        var host = new VisualHost(drawingVisual);
        Canvas.SetTop(host, y);
        Canvas.SetLeft(host, 0);
        TerminalCanvas.Children.Add(host);
        _renderedLines[lineIndex] = host;
    }

    private void UpdateCursor(int cursorRow, int cursorCol, int startLineIndex, int viewportOffset)
    {
        // Remove old cursor if it exists
        if (_cursorVisual != null && TerminalCanvas.Children.Contains(_cursorVisual))
        {
            TerminalCanvas.Children.Remove(_cursorVisual);
            _cursorVisual = null;
        }

        // Only show cursor when at bottom (scrollOffset == 0) and cursor is visible
        int visibleRows = GetVisibleRowCount();
        if (_scrollOffset == 0 && cursorRow >= 0 && _emulator != null && _emulator.CursorVisible &&
            cursorRow >= startLineIndex && cursorRow < startLineIndex + visibleRows)
        {
            var drawingVisual = new DrawingVisual();
            DrawingContext? dc = null;
            try
            {
                dc = drawingVisual.RenderOpen();

                var x = cursorCol * _charWidthSnapped;
                var y = _charHeightSnapped - 2; // underline at bottom of line

                // Use same data source as rendering (raw buffer or emulator)
                int fg = 7, bg = 0;
                var line = GetLineForRender(cursorRow);
                if (line != null && cursorCol >= 0 && cursorCol < line.Cells.Count)
                {
                    var cell = line.Cells[cursorCol];
                    fg = cell.Reverse ? cell.BackgroundColor : cell.ForegroundColor;
                    bg = cell.Reverse ? cell.ForegroundColor : cell.BackgroundColor;
                }

                var foregroundBrush = GetColor(fg);
                var underlineRect = new Rect(x, y, _charWidthSnapped, 2);
                dc.DrawRectangle(foregroundBrush, null, underlineRect);
            }
            finally
            {
                dc?.Close();
            }

            var host = new VisualHost(drawingVisual);
            var viewportRowPos = (cursorRow - startLineIndex) + viewportOffset;
            Canvas.SetTop(host, viewportRowPos * _charHeightSnapped);
            Canvas.SetLeft(host, 0);
            TerminalCanvas.Children.Add(host);
            _cursorVisual = host;
        }
    }

    // Read-only: only reads from buffer, never modifies it.
    private void RenderLine(DrawingContext dc, int lineIndex, int viewportRow)
    {
        if (_typeface == null)
        {
            return;
        }

        var line = GetLineForRender(lineIndex);
        if (line == null)
        {
            return;
        }

        // When called from RenderLineIncremental, the DrawingVisual is positioned on the Canvas,
        // so we draw at y=0 relative to the DrawingVisual. viewportRow is only used for
        // calculating which line to render, not for positioning.
        var y = 0.0;

        var cells = line.Cells;
        var viewportWidth = TerminalCanvas.ActualWidth > 0 ? TerminalCanvas.ActualWidth : ActualWidth;
        int visibleCols = (int)(viewportWidth / _charWidth);
        if (visibleCols <= 0)
        {
            return;
        }

        var maxCol = Math.Min(cells.Count, visibleCols);
        if (maxCol <= 0)
        {
            return;
        }

        // First pass: draw all background rectangles (batch by color). For selected cells use inverted (fg) color so highlight is visible.
        var currentBg = -1;
        var bgStartCol = 0;
        for (int col = 0; col < maxCol; col++)
        {
            var cell = cells[col];
            var cellBg = cell.Reverse ? cell.ForegroundColor : cell.BackgroundColor;
            var cellFg = cell.Reverse ? cell.BackgroundColor : cell.ForegroundColor;
            var bg = (_hasSelection || _isSelecting) && IsCellSelected(lineIndex, col) ? cellFg : cellBg;

            if (bg != currentBg)
            {
                if (currentBg >= 0 && col > bgStartCol)
                {
                    var bgWidth = (col - bgStartCol) * _charWidthSnapped;
                    var bgBrush = GetColor(currentBg);
                    var bgRect = new Rect(bgStartCol * _charWidthSnapped, y, bgWidth, _charHeightSnapped);
                    dc.DrawRectangle(bgBrush, null, bgRect);
                }
                bgStartCol = col;
                currentBg = bg;
            }
        }
        // Draw final background rectangle
        if (currentBg >= 0 && maxCol > bgStartCol)
        {
            var bgWidth = (maxCol - bgStartCol) * _charWidthSnapped;
            var bgBrush = GetColor(currentBg);
            var bgRect = new Rect(bgStartCol * _charWidthSnapped, y, bgWidth, _charHeightSnapped);
            dc.DrawRectangle(bgBrush, null, bgRect);
        }

        // Second pass: draw text segments (only break on foreground/style changes, not background)
        var currentFg = -1;
        currentBg = -1;
        var currentBold = false;
        var currentItalic = false;
        var currentUnderline = false;
        var currentFaint = false;
        var currentCrossedOut = false;
        var currentDoubleUnderline = false;
        var currentOverline = false;
        var currentConceal = false;
        var x = 0.0;
        _textRunBuilder.Clear();
        var textRunStartCol = 0;

        // Render only cells that fit in the viewport (truncate long lines)
        for (int col = 0; col < maxCol; col++)
        {
            var cell = cells[col];

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

            // Only break on foreground color or text style changes (not background, since backgrounds are drawn separately)
            // This allows us to batch more text together for faster rendering
            if (fg != currentFg || bold != currentBold || italic != currentItalic || 
                underline != currentUnderline || faint != currentFaint || crossedOut != currentCrossedOut ||
                doubleUnderline != currentDoubleUnderline || overline != currentOverline || conceal != currentConceal)
            {
                if (_textRunBuilder.Length > 0)
                {
                    var textRun = _textRunBuilder.ToString();
                    RenderTextSegment(dc, x, y, textRun, currentFg, currentBg, currentBold, currentItalic, currentUnderline, currentFaint, currentCrossedOut, currentDoubleUnderline, currentOverline, currentConceal, textRunStartCol, lineIndex);
                    x += textRun.Length * _charWidthSnapped;
                    _textRunBuilder.Clear();
                }

                _textRunBuilder.Append(cell.Character);
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
            else
            {
                // Background changed but foreground/style didn't - update background but keep batching text
                if (bg != currentBg)
                {
                    currentBg = bg;
                }
                _textRunBuilder.Append(cell.Character);
            }
        }

        // Render any remaining textRun
        if (_textRunBuilder.Length > 0)
        {
            var textRun = _textRunBuilder.ToString();
            RenderTextSegment(dc, x, y, textRun, currentFg, currentBg, currentBold, currentItalic, currentUnderline, currentFaint, currentCrossedOut, currentDoubleUnderline, currentOverline, currentConceal, textRunStartCol, lineIndex);
            _textRunBuilder.Clear();
        }
    }

    private bool IsCellSelected(int lineIndex, int col)
    {
        if (!_selectionStartRow.HasValue || !_selectionStartCol.HasValue ||
            !_selectionEndRow.HasValue || !_selectionEndCol.HasValue)
        {
            return false;
        }

        var topRow = Math.Min(_selectionStartRow.Value, _selectionEndRow.Value);
        var bottomRow = Math.Max(_selectionStartRow.Value, _selectionEndRow.Value);
        // Use actual start/end columns per row so ending on an empty line (endCol=0) doesn't pull start to column 0
        var colOnTopRow = _selectionStartRow.Value <= _selectionEndRow.Value ? _selectionStartCol.Value : _selectionEndCol.Value;
        var colOnBottomRow = _selectionStartRow.Value <= _selectionEndRow.Value ? _selectionEndCol.Value : _selectionStartCol.Value;

        if (lineIndex < topRow || lineIndex > bottomRow)
        {
            return false;
        }

        if (topRow == bottomRow)
        {
            var colMin = Math.Min(colOnTopRow, colOnBottomRow);
            var colMax = Math.Max(colOnTopRow, colOnBottomRow);
            return col >= colMin && col < colMax;
        }

        if (lineIndex == topRow)
        {
            return col >= colOnTopRow;
        }

        if (lineIndex == bottomRow)
        {
            return col < colOnBottomRow;
        }

        return true;
    }

    // Read-only: only reads from buffer for selection state, never modifies buffer
    private void RenderTextSegment(DrawingContext dc, double x, double y, string text, int fgColor, int bgColor, bool bold, bool italic, bool underline, bool faint, bool crossedOut, bool doubleUnderline, bool overline, bool conceal, int startCol, int lineIndex)
    {
        if (_typeface == null || string.IsNullOrEmpty(text))
        {
            return;
        }

        if (x < 0 || y < 0 || x + text.Length * _charWidthSnapped > TerminalCanvas.Width)
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
            bool firstCharSelected = IsCellSelected(lineIndex, startCol);
            bool needsSplit = false;

            for (int i = 1; i < text.Length; i++)
            {
                if (IsCellSelected(lineIndex, startCol + i) != firstCharSelected)
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
                    var cellIsSelected = IsCellSelected(lineIndex, charCol);
                    
                    // Calculate exact position based on column index to avoid accumulation errors
                    var charX = charCol * _charWidthSnapped;
                    
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

                    // Draw background
                    var charBgRect = new Rect(charX, y, _charWidthSnapped + 0.1, _charHeightSnapped);
                    dc.DrawRectangle(charBackground, null, charBgRect);

                    // Create FormattedText for character
                    var charFormattedText = new FormattedText(
                        text[i].ToString(),
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        _fontSize,
                        charForeground,
                        VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    
                    charFormattedText.SetFontWeight(fontWeight);
                    charFormattedText.SetFontStyle(fontStyle);
                    
                    if (underline || doubleUnderline)
                    {
                        charFormattedText.SetTextDecorations(TextDecorations.Underline);
                    }
                    
                    if (overline)
                    {
                        charFormattedText.SetTextDecorations(TextDecorations.OverLine);
                    }
                    
                    if (crossedOut)
                    {
                        charFormattedText.SetTextDecorations(TextDecorations.Strikethrough);
                    }

                    // Draw character
                    dc.DrawText(charFormattedText, new Point(charX, y));

                    // Draw double underline if needed
                    if (doubleUnderline)
                    {
                        var underlineRect = new Rect(charX, y + _charHeightSnapped - 3, _charWidthSnapped, 1);
                        dc.DrawRectangle(charForeground, null, underlineRect);
                    }
                }
                return;
            }
        }

        // For single character or no selection, render normally
        bool charIsSelected = false;
        if ((_hasSelection || _isSelecting) && text.Length == 1)
        {
            charIsSelected = IsCellSelected(lineIndex, startCol);
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
            // Cache faint color - use a key based on the original color
            var color = fgBrush.Color;
            var faintKey = unchecked((int)((uint)color.R << 24 | (uint)color.G << 16 | (uint)color.B << 8 | (uint)color.A));
            if (!_colorCache.TryGetValue(faintKey, out var faintBrush))
            {
                var fadedColor = Color.FromArgb(color.A, (byte)(color.R * 0.5), (byte)(color.G * 0.5), (byte)(color.B * 0.5));
                faintBrush = new SolidColorBrush(fadedColor);
                _colorCache[faintKey] = faintBrush;
            }
            foreground = faintBrush;
        }

        // Background is drawn separately in first pass, skip it here (unless we need per-character backgrounds for selection)

        // Use GlyphRun for faster rendering if available, otherwise fall back to FormattedText
        if (_glyphTypeface != null && !bold && !italic && !underline && !overline && !crossedOut && !doubleUnderline)
        {
            // Fast path: use GlyphRun for plain text. Chunk to avoid GlyphRun's 65535 glyph limit.
            double currentX = x;
            int offset = 0;
            while (offset < text.Length)
            {
                int chunkLen = Math.Min(MAX_GLYPH_RUN_CHARS, text.Length - offset);
                var chunk = text.Substring(offset, chunkLen);
                var glyphIndices = new ushort[chunkLen];
                var advanceWidths = new double[chunkLen];
                int glyphCount = 0;

                foreach (var ch in chunk)
                {
                    if (_glyphTypeface.CharacterToGlyphMap.TryGetValue(ch, out var glyphIndex))
                    {
                        glyphIndices[glyphCount] = glyphIndex;
                        advanceWidths[glyphCount] = _charWidthSnapped;
                        glyphCount++;
                    }
                }

                if (glyphCount > 0)
                {
                    EnsureDpiCached();
                    var glyphRun = new GlyphRun(
                        _glyphTypeface,
                        0,
                        false,
                        _fontSize,
                        (float)_cachedDpi,
                        glyphIndices,
                        new Point(currentX, y + _glyphTypeface.Baseline * _fontSize),
                        advanceWidths,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null);

                    dc.DrawGlyphRun(foreground, glyphRun);
                }
                currentX += chunkLen * _charWidthSnapped;
                offset += chunkLen;
            }
            if (offset == 0)
            {
                // No glyphs mapped (e.g. replacement chars only) - fall back to FormattedText
                EnsureDpiCached();
                var formattedText = new FormattedText(
                    text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _typeface,
                    _fontSize,
                    foreground,
                    _cachedDpi);
                formattedText.SetFontWeight(fontWeight);
                formattedText.SetFontStyle(fontStyle);
                if (underline || doubleUnderline)
                    formattedText.SetTextDecorations(TextDecorations.Underline);
                if (overline)
                    formattedText.SetTextDecorations(TextDecorations.OverLine);
                if (crossedOut)
                    formattedText.SetTextDecorations(TextDecorations.Strikethrough);
                dc.DrawText(formattedText, new Point(x, y));
            }
        }
        else
        {
            EnsureDpiCached();
            var formattedText = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                _typeface,
                _fontSize,
                foreground,
                _cachedDpi);
            formattedText.SetFontWeight(fontWeight);
            formattedText.SetFontStyle(fontStyle);
            if (underline || doubleUnderline)
                formattedText.SetTextDecorations(TextDecorations.Underline);
            if (overline)
                formattedText.SetTextDecorations(TextDecorations.OverLine);
            if (crossedOut)
                formattedText.SetTextDecorations(TextDecorations.Strikethrough);
            dc.DrawText(formattedText, new Point(x, y));
        }

        // Draw double underline if needed
        if (doubleUnderline)
        {
            var underlineRect = new Rect(x, y + _charHeightSnapped - 3, text.Length * _charWidthSnapped, 1);
            dc.DrawRectangle(foreground, null, underlineRect);
        }
    }

    private int GetLineIndexAtViewportRow(int viewportRow)
    {
        var totalLines = GetDisplayLineCount();
        if (totalLines == 0) return 0;

        int visibleRows = Math.Max(1, (int)(ActualHeight / _charHeight));
        var range = ComputeViewportRange(totalLines, visibleRows);

        int adjustedViewportRow = viewportRow - range.ViewportOffset;
        if (adjustedViewportRow < 0) return 0;
        return Math.Min(range.StartLineIndex + adjustedViewportRow, Math.Max(0, totalLines - 1));
    }

    private Brush GetColor(int colorIndex)
    {
        if (_colorCache.TryGetValue(colorIndex, out var cachedBrush))
        {
            return cachedBrush;
        }
        
        Brush brush;
        if (colorIndex == 0 && !string.IsNullOrEmpty(_customBackgroundColor))
        {
            try
            {
                brush = new BrushConverter().ConvertFromString(_customBackgroundColor) as Brush ?? new SolidColorBrush(Color.FromRgb(0, 0, 0));
            }
            catch
            {
                brush = new SolidColorBrush(Color.FromRgb(0, 0, 0));
            }
        }
        else if (colorIndex == 7 && !string.IsNullOrEmpty(_customForegroundColor))
        {
            try
            {
                brush = new BrushConverter().ConvertFromString(_customForegroundColor) as Brush ?? new SolidColorBrush(Color.FromRgb(192, 192, 192));
            }
            catch
            {
                brush = new SolidColorBrush(Color.FromRgb(192, 192, 192));
            }
        }
        else if ((colorIndex & 0x1000000) != 0)
        {
            // RGB color: 0x1000000 | (r << 16) | (g << 8) | b
            var r = (byte)((colorIndex >> 16) & 0xFF);
            var g = (byte)((colorIndex >> 8) & 0xFF);
            var b = (byte)(colorIndex & 0xFF);
            brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        }
        else if (colorIndex >= 16 && colorIndex <= 255)
        {
            brush = Get256Color(colorIndex);
        }
        else
        {
            brush = colorIndex switch
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
        
        _colorCache[colorIndex] = brush;
        return brush;
    }
    
    private Brush Get256Color(int colorIndex)
    {
        // Standard 256-color palette
        // Colors 0-15: standard colors (already handled in GetColor)
        // Colors 16-231: 6x6x6 color cube
        // Colors 232-255: grayscale
        
        if (colorIndex >= 232 && colorIndex <= 255)
        {
            // Grayscale: 232-255
            var gray = (byte)(8 + (colorIndex - 232) * 10);
            return new SolidColorBrush(Color.FromRgb(gray, gray, gray));
        }
        else if (colorIndex >= 16 && colorIndex <= 231)
        {
            // 6x6x6 color cube: 16 + 36*r + 6*g + b, where r,g,b are 0-5
            var index = colorIndex - 16;
            var r = index / 36;
            var g = (index % 36) / 6;
            var b = index % 6;
            var red = (byte)(r == 0 ? 0 : 55 + r * 40);
            var green = (byte)(g == 0 ? 0 : 55 + g * 40);
            var blue = (byte)(b == 0 ? 0 : 55 + b * 40);
            return new SolidColorBrush(Color.FromRgb(red, green, blue));
        }
        
        return Brushes.White;
    }

    private static bool IsMouseOverScrollBar(DependencyObject? source)
    {
        if (source is Thumb or RepeatButton or ScrollBar) return true;
        return source != null && FindParent<ScrollBar>(source) != null;
    }

    private bool IsPointOverCanvas(Point pos)
    {
        return pos.X >= 0 && pos.Y >= 0 &&
               pos.X <= TerminalCanvas.ActualWidth && pos.Y <= TerminalCanvas.ActualHeight;
    }

    private void TerminalEmulator_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseOverScrollBar(e.OriginalSource as DependencyObject))
            return;

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            var pos = e.GetPosition(TerminalCanvas);
            if (!IsPointOverCanvas(pos) || _charWidth <= 0 || _charHeight <= 0 || GetDisplayLineCount() == 0)
                return;

            Focus();
                
                // If there's already a selection and we're clicking, copy it (PuTTY behavior)
                if (_hasSelection && !_isSelecting)
                {
                    CopySelectionToClipboard();
                    ClearSelection();
                    _hasSelection = false;
                }
                
                // Calculate cell position (works for both normal and raw mode)
                var viewportRow = Math.Max(0, (int)(pos.Y / _charHeightSnapped));
                var lineIndex = GetLineIndexAtViewportRow(viewportRow);
                var line = GetLineForRender(lineIndex);
                var maxCol = line != null ? Math.Max(0, line.Cells.Count - 1) : 0;
                var col = Math.Max(0, Math.Min(maxCol, (int)(pos.X / _charWidthSnapped)));
                
                // Handle double-click (word selection) and triple-click (line selection)
                if (e.ClickCount == 2)
                {
                    // Double-click: select word
                    SelectWordAt(lineIndex, col);
                    e.Handled = true;
                    return;
                }
                else if (e.ClickCount == 3)
                {
                    // Triple-click: select line
                    SelectLineAt(lineIndex);
                    e.Handled = true;
                    return;
                }
                
                // Start new selection
                _isSelecting = true;
                _selectionStartRow = lineIndex;
                _selectionStartCol = col;
                _selectionEndRow = lineIndex;
                _selectionEndCol = col;
                
                RenderScreen();
                
                // Capture mouse and set cursor
                CaptureMouse();
                Cursor = Cursors.IBeam;
                e.Handled = true;
        }
        else if (e.RightButton == MouseButtonState.Pressed)
        {
            var pos = e.GetPosition(TerminalCanvas);
            if (IsPointOverCanvas(pos))
            {
                if (!_isSelecting && !_hasSelection)
                    PasteFromClipboard();
                e.Handled = true;
            }
        }
    }
    
    private void TerminalEmulator_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.RightButton == MouseButtonState.Pressed && IsPointOverCanvas(e.GetPosition(TerminalCanvas)))
            e.Handled = true;
    }
    
    private void TerminalEmulator_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Prevent context menu from opening in terminal area
        e.Handled = true;
    }

    private void TerminalEmulator_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (IsMouseOverScrollBar(e.OriginalSource as DependencyObject))
            return;

        if (_isSelecting && e.LeftButton == MouseButtonState.Pressed && _charWidth > 0 && _charHeight > 0)
        {
            var pos = e.GetPosition(TerminalCanvas);
            var viewportRow = Math.Max(0, (int)(pos.Y / _charHeight));
            var lineIndex = GetLineIndexAtViewportRow(viewportRow);
            var line = GetLineForRender(lineIndex);
            var maxCol = line != null ? Math.Max(0, line.Cells.Count - 1) : 0;
            var col = Math.Max(0, Math.Min(maxCol, (int)(pos.X / _charWidth)));
            
            if (_selectionEndRow != lineIndex || _selectionEndCol != col)
            {
                _selectionEndRow = lineIndex;
                _selectionEndCol = col;
                RenderScreen();
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
                    _hasSelection = true;
                else
                {
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

    private char GetCellCharacter(int lineIndex, int col)
    {
        var line = GetLineForRender(lineIndex);
        if (line == null || col < 0 || col >= line.Cells.Count)
            return ' ';
        return line.Cells[col].Character;
    }

    private void SelectWordAt(int lineIndex, int col)
    {
        var line = GetLineForRender(lineIndex);
        if (line == null) return;

        var ch = col < line.Cells.Count ? line.Cells[col].Character : ' ';
        bool isWordChar = !char.IsWhiteSpace(ch);

        int startCol = col;
        int endCol = col;

        if (isWordChar)
        {
            while (startCol > 0)
            {
                if (char.IsWhiteSpace(GetCellCharacter(lineIndex, startCol - 1)))
                    break;
                startCol--;
            }
            while (endCol < line.Cells.Count - 1)
            {
                if (char.IsWhiteSpace(GetCellCharacter(lineIndex, endCol + 1)))
                    break;
                endCol++;
            }
            endCol++;
        }
        else
        {
            endCol++;
        }

        _selectionStartRow = lineIndex;
        _selectionStartCol = startCol;
        _selectionEndRow = lineIndex;
        _selectionEndCol = endCol;
        _hasSelection = true;
        _isSelecting = false;

        RenderScreen();
    }

    private void SelectLineAt(int lineIndex)
    {
        var line = GetLineForRender(lineIndex);
        var lineLength = line != null ? line.Cells.Count : 0;
        
        // Select entire line from column 0 to end
        _selectionStartRow = lineIndex;
        _selectionStartCol = 0;
        _selectionEndRow = lineIndex;
        _selectionEndCol = lineLength;
        _hasSelection = true;
        _isSelecting = false;
        
        RenderScreen();
    }

    private void CopySelectionToClipboard()
    {
        if (!_selectionStartRow.HasValue || !_selectionStartCol.HasValue ||
            !_selectionEndRow.HasValue || !_selectionEndCol.HasValue)
        {
            return;
        }
        if (_emulator == null)
            return;

        var topRow = Math.Min(_selectionStartRow.Value, _selectionEndRow.Value);
        var bottomRow = Math.Max(_selectionStartRow.Value, _selectionEndRow.Value);
        var colOnTopRow = _selectionStartRow.Value <= _selectionEndRow.Value ? _selectionStartCol.Value : _selectionEndCol.Value;
        var colOnBottomRow = _selectionStartRow.Value <= _selectionEndRow.Value ? _selectionEndCol.Value : _selectionStartCol.Value;

        var selectedText = new System.Text.StringBuilder();
        for (int lineIndex = topRow; lineIndex <= bottomRow; lineIndex++)
        {
            var line = GetLineForRender(lineIndex);
            if (line == null) continue;
            
            var lineText = new System.Text.StringBuilder();
            
            // Determine column range for this line (same logic as IsCellSelected so copy matches highlight)
            int rowStartCol, rowEndCol;
            if (topRow == bottomRow)
            {
                rowStartCol = Math.Min(colOnTopRow, colOnBottomRow);
                rowEndCol = Math.Max(colOnTopRow, colOnBottomRow);
            }
            else if (lineIndex == topRow)
            {
                rowStartCol = colOnTopRow;
                rowEndCol = line.Cells.Count;
            }
            else if (lineIndex == bottomRow)
            {
                rowStartCol = 0;
                rowEndCol = colOnBottomRow;
            }
            else
            {
                rowStartCol = 0;
                rowEndCol = line.Cells.Count;
            }
            
            for (int col = rowStartCol; col < rowEndCol && col < line.Cells.Count; col++)
            {
                var cell = line.Cells[col];
                lineText.Append(cell.Character);
            }
            
            if (lineText.Length > 0)
            {
                var lineStr = lineText.ToString();
                if (lineIndex < bottomRow)
                {
                    selectedText.AppendLine(lineStr);
                }
                else
                {
                    selectedText.Append(lineStr);
                }
            }
            else if (lineIndex < bottomRow)
            {
                selectedText.AppendLine();
            }
        }

        if (selectedText.Length > 0)
        {
            try
            {
                Clipboard.SetText(selectedText.ToString());
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
                    // Clear selection and redraw so any existing highlight is gone before new text appears
                    ClearSelection();
                    RenderScreen();
                    
                    _lastInputSentTime = DateTime.Now;
                    
                    // Send pasted text as raw characters (no bracketed paste wrappers) so the server echoes it
                    // exactly like typed input with no special formatting/highlight
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
            // DECCKM mode: true = cursor mode (ESC [), false = application mode (ESC O)
            sequence = _emulator != null && _emulator.CursorKeyMode ? "\x1B[A" : "\x1BOA";
            e.Handled = true;
        }
        else if (key == Key.Down)
        {
            sequence = _emulator != null && _emulator.CursorKeyMode ? "\x1B[B" : "\x1BOB";
            e.Handled = true;
        }
        else if (key == Key.Right)
        {
            sequence = _emulator != null && _emulator.CursorKeyMode ? "\x1B[C" : "\x1BOC";
            e.Handled = true;
        }
        else if (key == Key.Left)
        {
            sequence = _emulator != null && _emulator.CursorKeyMode ? "\x1B[D" : "\x1BOD";
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

    private int GetDisplayLineCount()
    {
        return _emulator?.LineCount ?? 0;
    }

    private void TerminalEmulator_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Handle wheel before ScrollViewer so our custom scroll offset is used (ScrollViewer would eat the event otherwise)
        if (GetDisplayLineCount() == 0) return;
        if (_hasSelection) ClearSelection();

        int delta = e.Delta > 0 ? SCROLL_LINES_PER_TICK : -SCROLL_LINES_PER_TICK;
        int totalLines = GetDisplayLineCount();
        int visibleRows = GetVisibleRowCount();
        int maxScroll = Math.Max(0, totalLines - visibleRows);
        int newScrollOffset = Math.Max(0, Math.Min(_scrollOffset + delta, maxScroll));
        if (newScrollOffset != _scrollOffset)
        {
            _scrollOffset = newScrollOffset;
            UpdateScrollBar();
            UpdateCanvasTransform();
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
            TerminalScrollBar.IsEnabled = false;
            TerminalScrollBar.Maximum = 0;
            TerminalScrollBar.ViewportSize = 0;
            TerminalScrollBar.Value = 0;
            return;
        }
        int totalLines = GetDisplayLineCount();
        int visibleRows = GetVisibleRowCount();
        int maxScroll = Math.Max(0, totalLines - visibleRows);

        TerminalScrollBar.Maximum = maxScroll;
        TerminalScrollBar.ViewportSize = Math.Max(1, visibleRows);
        TerminalScrollBar.Value = maxScroll - _scrollOffset;
        TerminalScrollBar.IsEnabled = maxScroll > 0 && totalLines > 0;
    }
    
    private void ApplyScrollOffsetFromScrollBar()
    {
        if (GetDisplayLineCount() == 0) return;
        int totalLines = GetDisplayLineCount();
        int visibleRows = GetVisibleRowCount();
        int maxScroll = Math.Max(0, totalLines - visibleRows);
        int newScrollOffset = maxScroll - (int)TerminalScrollBar.Value;
        if (newScrollOffset == _scrollOffset) return;
        _scrollOffset = newScrollOffset;
        UpdateCanvasTransform();
        RenderScreen();
    }

    private void TerminalScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyScrollOffsetFromScrollBar();
    }

    private void TerminalScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        ApplyScrollOffsetFromScrollBar();
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
}

