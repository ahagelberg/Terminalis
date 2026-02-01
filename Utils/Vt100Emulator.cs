using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Linq;

namespace TabbySSH.Utils;

public class TerminalCell
{
    public char Character { get; set; } = ' ';
    public int ForegroundColor { get; set; } = 7;
    public int BackgroundColor { get; set; } = 0;
    public bool Bold { get; set; }
    public bool Faint { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Blink { get; set; }
    public bool Reverse { get; set; }
    public bool Conceal { get; set; }
    public bool CrossedOut { get; set; }
    public bool DoubleUnderline { get; set; }
    public bool Overline { get; set; }
}

public class TerminalLine
{
    public List<TerminalCell> Cells { get; } = new();
    public bool IsDirty { get; set; } = false;
}

public class Vt100Emulator
{
    private const int DEFAULT_COLS = 80;
    private const int DEFAULT_ROWS = 24;

    private readonly List<TerminalLine> _lines = new();
    private readonly AnsiParser _ansiParser = new();
    private readonly StringBuilder _textBatch = new();
    private int _currentLineIndex = -1;
    private int _writeCol = 0;
    private int _textBatchStartRow = -1;
    private int _textBatchStartCol = -1;
    private int _rows = DEFAULT_ROWS;
    private int _cols = DEFAULT_COLS;
    private int _foregroundColor = 7;
    private int _backgroundColor = 0;
    private bool _bold = false;
    private bool _faint = false;
    private bool _italic = false;
    private bool _underline = false;
    private bool _blink = false;
    private bool _reverse = false;
    private bool _conceal = false;
    private bool _crossedOut = false;
    private bool _doubleUnderline = false;
    private bool _overline = false;
    private bool _bracketedPasteMode = false;
    private bool _cursorKeyMode = false; // DECCKM: false = application mode (ESC O), true = cursor mode (ESC [)
    private bool _insertMode = false; // IRM: false = replace mode (default), true = insert mode
    private bool _inAlternateScreen = false;
    private bool _cursorVisible = true;
    private bool _autoWrapMode = true;
    
    // Alternate screen buffer state
    private List<TerminalLine>? _savedMainScreenLines = null;
    private int _savedMainScreenLineIndex = -1;
    private int _savedMainScreenWriteCol = 0;
    private int _savedMainScreenForegroundColor = 7;
    private int _savedMainScreenBackgroundColor = 0;
    private bool _savedMainScreenBold = false;
    private bool _savedMainScreenFaint = false;
    private bool _savedMainScreenItalic = false;
    private bool _savedMainScreenUnderline = false;
    private bool _savedMainScreenBlink = false;
    private bool _savedMainScreenReverse = false;
    private bool _savedMainScreenConceal = false;
    private bool _savedMainScreenCrossedOut = false;
    private bool _savedMainScreenDoubleUnderline = false;
    private bool _savedMainScreenOverline = false;

    private string? _currentTitle = null;
    private string? _savedTitle = null;

    private int _scrollRegionTop = -1; // -1 means no scrolling region (entire screen scrolls)
    private int _scrollRegionBottom = -1; // -1 means no scrolling region (entire screen scrolls)

#if DEBUG
    private readonly Dictionary<string, PerformanceMetric> _performanceMetrics = new();
    
    private class PerformanceMetric
    {
        public long TotalTicks { get; set; }
        public long CallCount { get; set; }
        public long MinTicks { get; set; } = long.MaxValue;
        public long MaxTicks { get; set; }
        
        public double AverageMs => CallCount > 0 ? (TotalTicks / (double)TimeSpan.TicksPerMillisecond) / CallCount : 0;
        public double TotalMs => TotalTicks / (double)TimeSpan.TicksPerMillisecond;
    }
    
    public Dictionary<string, (double avgMs, double totalMs, long calls, double minMs, double maxMs)> GetPerformanceMetrics()
    {
        return _performanceMetrics.ToDictionary(
            kvp => kvp.Key,
            kvp => (
                kvp.Value.AverageMs,
                kvp.Value.TotalMs,
                kvp.Value.CallCount,
                kvp.Value.MinTicks / (double)TimeSpan.TicksPerMillisecond,
                kvp.Value.MaxTicks / (double)TimeSpan.TicksPerMillisecond
            )
        );
    }
    
    public void ResetPerformanceMetrics()
    {
        _performanceMetrics.Clear();
    }
    
    private void RecordPerformance(string operation, long ticks)
    {
        if (!_performanceMetrics.ContainsKey(operation))
        {
            _performanceMetrics[operation] = new PerformanceMetric();
        }
        var metric = _performanceMetrics[operation];
        metric.TotalTicks += ticks;
        metric.CallCount++;
        if (ticks < metric.MinTicks) metric.MinTicks = ticks;
        if (ticks > metric.MaxTicks) metric.MaxTicks = ticks;
    }
#endif

    public int Rows => _rows;
    public int Cols => _cols;
    public int LineCount => _lines.Count;
    public bool BracketedPasteMode => _bracketedPasteMode;
    public bool CursorKeyMode => _cursorKeyMode;
    public bool InAlternateScreen => _inAlternateScreen;
    public bool CursorVisible => _cursorVisible;
    
    // Stub properties for UI compatibility
    public int CursorRow => _currentLineIndex >= 0 ? _currentLineIndex : 0;
    public int CursorCol => _writeCol;
    public int ScrollbackLineCount => 0;
    
#if DEBUG
    public TerminalModes Modes => new TerminalModes
    {
        InAlternateScreen = _inAlternateScreen,
        CursorKeyMode = _cursorKeyMode,
        InsertMode = _insertMode,
        AutoWrapMode = _autoWrapMode,
        CursorVisible = _cursorVisible,
        BracketedPasteMode = _bracketedPasteMode,
        ScrollRegionTop = _scrollRegionTop,
        ScrollRegionBottom = _scrollRegionBottom
    };
    
    public void SetMode(string modeName, bool value)
    {
        // Stub implementation for debug panel
    }
    
    public void SendAnsiCode(string code)
    {
        // Stub implementation for debug panel
    }
#endif

    public event EventHandler? Bell;
    public event EventHandler<string>? TitleChanged;

#if DEBUG
    public bool DebugMode { get; set; }
    public event EventHandler<DebugCommandEventArgs>? DebugCommandExecuted;
    
    public class DebugCommandEventArgs : EventArgs
    {
        public string CommandType { get; set; } = string.Empty;
        public string Parameters { get; set; } = string.Empty;
        public string ResultingState { get; set; } = string.Empty;
        public string RawText { get; set; } = string.Empty;
        public string CommandInterpretation { get; set; } = string.Empty;
        public int CursorRowBefore { get; set; }
        public int CursorColBefore { get; set; }
        public int CursorRowAfter { get; set; }
        public int CursorColAfter { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
#endif

    public Vt100Emulator()
    {
        _ansiParser.CommandReceived += OnAnsiCommand;
        _ansiParser.CharacterReceived += OnCharacter;
    }

    public void SetSize(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        // Terminal width change doesn't affect existing lines - they keep their original length
    }

    public void ProcessData(string data)
    {
#if DEBUG
        var stopwatch = Stopwatch.StartNew();
#endif
        
#if DEBUG
        // Log raw data received from server (only for small chunks to avoid performance issues)
        if (DebugMode && !string.IsNullOrEmpty(data) && data.Length <= 100)
        {
            var escapedData = AnsiParser.EscapeString(data);
            var args = new DebugCommandEventArgs
            {
                CommandType = "Raw Data",
                Parameters = $"length={data.Length} bytes",
                ResultingState = $"raw data received from server",
                RawText = data,
                CommandInterpretation = $"Raw server output: {escapedData}",
                CursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0,
                CursorColBefore = _writeCol,
                CursorRowAfter = _currentLineIndex >= 0 ? _currentLineIndex : 0,
                CursorColAfter = _writeCol
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Process data in order as it arrives - no batching, no reordering
        // This ensures commands and text are processed in the exact order they appear
        _ansiParser.ProcessData(data);
        
#if DEBUG
        stopwatch.Stop();
        RecordPerformance("ProcessData", stopwatch.ElapsedTicks);
#endif
    }

    public TerminalLine? GetLine(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _lines.Count)
        {
            return null;
        }
        return _lines[lineIndex];
    }

    public TerminalCell GetCell(int lineIndex, int col)
    {
        var line = GetLine(lineIndex);
        if (line == null || col < 0 || col >= line.Cells.Count)
        {
            return new TerminalCell();
        }
        return line.Cells[col];
    }

    // Stub method for UI compatibility - no actual scrollback
    public TerminalCell GetScrollbackCell(int row, int col)
    {
        return new TerminalCell();
    }

    // Stub method for UI compatibility - no actual scrollback limit
    public void SetScrollbackLimit(int lines)
    {
        // No-op: scrollback functionality removed
    }

    public string GetLineText(int lineIndex)
    {
        var line = GetLine(lineIndex);
        if (line == null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var cell in line.Cells)
        {
            sb.Append(cell.Character);
        }
        return sb.ToString();
    }

    public HashSet<int> GetDirtyLines()
    {
        var dirtyLines = new HashSet<int>();
        for (int i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].IsDirty)
            {
                dirtyLines.Add(i);
            }
        }
        return dirtyLines;
    }

    public void ClearDirtyLines()
    {
        foreach (var line in _lines)
        {
            line.IsDirty = false;
        }
    }

    private void MarkLineDirty(int lineIndex)
    {
        if (lineIndex >= 0 && lineIndex < _lines.Count)
        {
            _lines[lineIndex].IsDirty = true;
        }
    }

    private void MarkLineDirty(TerminalLine? line)
    {
        if (line != null)
        {
            line.IsDirty = true;
        }
    }

    private void OnAnsiCommand(object? sender, AnsiCommand command)
    {
#if DEBUG
        var stopwatch = Stopwatch.StartNew();
#endif
        
        // Flush any pending text batch before processing command
        // This ensures text is processed before the command that follows it
        FlushTextBatch();
        
        if (command.Type == AnsiCommandType.Csi)
        {
            ProcessCsiCommand(command);
        }
        else if (command.Type == AnsiCommandType.Osc)
        {
            ProcessOscCommand(command);
        }
        else if (command.Type == AnsiCommandType.SingleChar)
        {
            ProcessSingleCharCommand(command);
        }
        else
        {
            // Warn about unhandled ANSI commands
            string commandDesc = command.Type switch
            {
                AnsiCommandType.SingleChar => $"SingleChar escape: ESC{command.FinalChar}",
                AnsiCommandType.Dcs => "DCS (Device Control String)",
                _ => $"Unknown command type: {command.Type}"
            };
            Debug.WriteLine($"[Vt100Emulator] ***Unhandled ANSI command: {commandDesc}");
            // System.Console.WriteLine($"[Vt100Emulator] ***Unhandled ANSI command: {commandDesc}");
        }
        
#if DEBUG
        stopwatch.Stop();
        RecordPerformance("OnAnsiCommand", stopwatch.ElapsedTicks);
#endif
    }

    private void ProcessOscCommand(AnsiCommand command)
    {
        if (command.Parameters.Count == 0 || string.IsNullOrEmpty(command.OscString))
        {
            return;
        }

        int oscCode = command.Parameters[0];
        string oscString = command.OscString;

        switch (oscCode)
        {
            case 0:
            case 2:
                // OSC 0: Set window title and icon name
                // OSC 2: Set window title
                // Format: ESC]0;<text>BEL or ESC]2;<text>BEL
                // Always accept these commands (no warning), but TitleChanged event will only fire if allowed by session setting
                if (oscString.Contains(';'))
                {
                    var parts = oscString.Split(';', 2);
                    if (parts.Length > 1)
                    {
                        string title = parts[1];
                        _currentTitle = title;
                        Debug.WriteLine($"[Vt100Emulator] Handled OSC command: ESC]{oscCode};{title} (Set window title)");
                        // System.Console.WriteLine($"[Vt100Emulator] Handled OSC command: ESC]{oscCode};{title} (Set window title)");
                        TitleChanged?.Invoke(this, title);
                    }
                }
                break;
            default:
                // Other OSC commands not implemented yet
                Debug.WriteLine($"[Vt100Emulator] ***Unhandled OSC command: {oscString} (code: {oscCode})");
                // System.Console.WriteLine($"[Vt100Emulator] ***Unhandled OSC command: {oscString} (code: {oscCode})");
                break;
        }
    }

    private void ProcessCsiCommand(AnsiCommand command)
    {
        var p = command.Parameters;
        var final = command.FinalChar;

        // Process SGR (m) command for text styles and colors
        if (final == 'm')
        {
            ProcessSgr(p, command);
        }
        // Process mode change commands (h/l)
        else if ((final == 'h' || final == 'l') && p.Count > 0)
        {
            if (command.IsPrivate)
            {
                // Private mode changes (DEC modes) - ESC [ ? ... h/l
                var paramStr = p.Count > 0 ? string.Join(";", p) : "";
                var commandStr = $"\x1B[?{paramStr}{final}";
                var escapedStr = AnsiParser.EscapeString(commandStr);
                Debug.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (DEC Mode change: mode={p[0]}, set={final == 'h'})");
                // System.Console.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (DEC Mode change: mode={p[0]}, set={final == 'h'})");
                ProcessDecModeChange(p[0], final == 'h');
            }
            else
            {
                // Non-private mode changes (ANSI modes) - ESC [ ... h/l
                var paramStr = p.Count > 0 ? string.Join(";", p) : "";
                var commandStr = $"\x1B[{paramStr}{final}";
                var escapedStr = AnsiParser.EscapeString(commandStr);
                Debug.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (ANSI Mode change: mode={p[0]}, set={final == 'h'})");
                // System.Console.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (ANSI Mode change: mode={p[0]}, set={final == 'h'})");
                ProcessAnsiModeChange(p[0], final == 'h');
            }
        }
        // Process Erase in Display (J) command
        else if (final == 'J')
        {
            var paramStr = p.Count > 0 ? string.Join(";", p) : "";
            var commandStr = $"\x1B[{paramStr}J";
            var escapedStr = AnsiParser.EscapeString(commandStr);
            Debug.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Erase in Display)");
            // System.Console.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Erase in Display)");
            ProcessEraseInDisplay(p);
        }
        // Process Erase in Line (K) command
        else if (final == 'K')
        {
            var paramStr = p.Count > 0 ? string.Join(";", p) : "";
            var commandStr = $"\x1B[{paramStr}K";
            var escapedStr = AnsiParser.EscapeString(commandStr);
            Debug.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Erase in Line)");
            // System.Console.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Erase in Line)");
            ProcessEraseInLine(p);
        }
        // Process Window Manipulation (t) commands
        else if (final == 't')
        {
            var paramStr = p.Count > 0 ? string.Join(";", p) : "";
            var commandStr = $"\x1B[{paramStr}t";
            var escapedStr = AnsiParser.EscapeString(commandStr);
            Debug.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Window Manipulation)");
            // System.Console.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Window Manipulation)");
            ProcessWindowManipulation(p);
        }
        // Process Set Scrolling Region (r) command
        else if (final == 'r')
        {
            var paramStr = p.Count > 0 ? string.Join(";", p) : "";
            var commandStr = $"\x1B[{paramStr}r";
            var escapedStr = AnsiParser.EscapeString(commandStr);
            Debug.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Set Scrolling Region)");
            // System.Console.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Set Scrolling Region)");
            ProcessSetScrollingRegion(p);
        }
        // Process Cursor Position (H or f) command
        else if (final == 'H' || final == 'f')
        {
            ProcessCursorPosition(p, command);
        }
        // Process Vertical Position Absolute (d) command
        else if (final == 'd')
        {
            ProcessVerticalPositionAbsolute(p, command);
        }
        // Process Cursor Horizontal Absolute (G) command
        else if (final == 'G')
        {
            ProcessCursorHorizontalAbsolute(p, command);
        }
        // Process Cursor Up (A) command
        else if (final == 'A')
        {
            ProcessCursorUp(p, command);
        }
        // Process Cursor Down (B) command
        else if (final == 'B')
        {
            ProcessCursorDown(p, command);
        }
        // Process Cursor Forward (C) command
        else if (final == 'C')
        {
            ProcessCursorForward(p, command);
        }
        // Process Cursor Backward (D) command
        else if (final == 'D')
        {
            ProcessCursorBackward(p, command);
        }
        // Process Cursor Next Line (E) command
        else if (final == 'E')
        {
            ProcessCursorNextLine(p, command);
        }
        // Process Cursor Previous Line (F) command
        else if (final == 'F')
        {
            ProcessCursorPreviousLine(p, command);
        }
        // Process Scroll Up (S) command
        else if (final == 'S')
        {
            ProcessScrollUp(p, command);
        }
        // Process Scroll Down (T) command
        else if (final == 'T')
        {
            ProcessScrollDown(p, command);
        }
        // Process Delete Line (M) command
        else if (final == 'M')
        {
            ProcessDeleteLine(p, command);
        }
        // Process Insert Line (L) command
        else if (final == 'L')
        {
            ProcessInsertLine(p, command);
        }
        // Process Delete Character (P) command
        else if (final == 'P')
        {
            ProcessDeleteCharacter(p, command);
        }
        // Process Insert Character (@) command
        else if (final == '@')
        {
            ProcessInsertCharacter(p, command);
        }
        else
        {
            // Warn about unhandled CSI commands
            var paramStr = p.Count > 0 ? string.Join(";", p) : "";
            var isPrivate = command.IsPrivate ? "?" : "";
            var commandStr = $"\x1B[{isPrivate}{paramStr}{final}";
            var escapedStr = AnsiParser.EscapeString(commandStr);
            Debug.WriteLine($"[Vt100Emulator] ***Unhandled CSI command: {escapedStr} (final char: '{final}' (0x{(int)final:X2}), params: [{paramStr}])");
            // System.Console.WriteLine($"[Vt100Emulator] ***Unhandled CSI command: {escapedStr} (final char: '{final}' (0x{(int)final:X2}), params: [{paramStr}])");
        }
    }

    private void ProcessDecModeChange(int mode, bool set)
    {
        switch (mode)
        {
            case 1:
                // DECCKM - Cursor Key Mode
                // When enabled (set=true): cursor keys send ESC [ A/B/C/D (cursor mode)
                // When disabled (set=false): cursor keys send ESC O A/B/C/D (application mode - default)
                _cursorKeyMode = set;
#if DEBUG
                Debug.WriteLine($"[Vt100Emulator] Cursor Key Mode (DECCKM): {(set ? "cursor mode (ESC [)" : "application mode (ESC O)")}");
#endif
                break;
            case 12:
                // DECSCLM - Start Blinking Cursor (DEC Smooth Cursor Line Mode)
                // This mode controls cursor blinking behavior
                // We track it but don't need to do anything special - cursor visibility is handled by mode 25
                Debug.WriteLine($"[Vt100Emulator] Start Blinking Cursor (DECSCLM): {(set ? "enabled" : "disabled")}");
                break;
            case 25:
                // DECTCEM - DEC Text Cursor Enable Mode
                // When enabled (set=true): cursor is visible
                // When disabled (set=false): cursor is hidden
                _cursorVisible = set;
                // Don't fire events - ScreenChanged will be fired at end of ProcessData
                Debug.WriteLine($"[Vt100Emulator] Cursor Visibility (DECTCEM): {(set ? "visible" : "hidden")}");
                break;
            case 1049:
                // DECALTB - Alternate Screen Buffer
                // When enabled (set=true): save main screen state and switch to alternate buffer
                // When disabled (set=false): restore main screen state
                if (set)
                {
                    SaveMainScreenState();
                    SwitchToAlternateScreen();
                }
                else
                {
                    RestoreMainScreenState();
                }
                Debug.WriteLine($"[Vt100Emulator] Alternate Screen Buffer (DECALTB): {(set ? "enabled" : "disabled")}");
                // System.Console.WriteLine($"[Vt100Emulator] Alternate Screen Buffer (DECALTB): {(set ? "enabled" : "disabled")}");
                break;
            case 2004:
                // Bracketed paste mode
                // When enabled (set=true), pasted text should be wrapped with \x1B[200~ and \x1B[201~
                // When disabled (set=false), pasted text is sent as-is
                _bracketedPasteMode = set;
                Debug.WriteLine($"[Vt100Emulator] Bracketed paste mode: {(set ? "enabled" : "disabled")}");
                break;
            default:
                // Other DEC mode changes not implemented yet
                Debug.WriteLine($"[Vt100Emulator] ***Unhandled DEC mode change: mode={mode}, set={set}");
                // System.Console.WriteLine($"[Vt100Emulator] ***Unhandled DEC mode change: mode={mode}, set={set}");
                break;
        }
    }

    private void ProcessAnsiModeChange(int mode, bool set)
    {
        switch (mode)
        {
            case 4:
                // IRM - Insert Replace Mode
                // When enabled (set=true): Insert Mode - new characters shift existing characters to the right
                // When disabled (set=false): Replace Mode - new characters overwrite existing characters (default)
                _insertMode = set;
                Debug.WriteLine($"[Vt100Emulator] Insert/Replace Mode (IRM): {(set ? "Insert Mode (characters shift right)" : "Replace Mode (characters overwrite)")}");
                // System.Console.WriteLine($"[Vt100Emulator] Insert/Replace Mode (IRM): {(set ? "Insert Mode (characters shift right)" : "Replace Mode (characters overwrite)")}");
                break;
            default:
                // Other ANSI mode changes not implemented yet
                Debug.WriteLine($"[Vt100Emulator] ***Unhandled ANSI mode change: mode={mode}, set={set}");
                // System.Console.WriteLine($"[Vt100Emulator] ***Unhandled ANSI mode change: mode={mode}, set={set}");
                break;
        }
    }

    private void ProcessEraseInDisplay(List<int> parameters)
    {
        int param = parameters.Count > 0 ? parameters[0] : 0;
        
        string operation = param switch
        {
            0 => "Erase from cursor to end of screen",
            1 => "Erase from cursor to beginning of screen",
            2 => "Erase entire screen",
            3 => "Erase entire screen and scrollback buffer",
            _ => $"Unknown erase operation (param={param})"
        };
        Debug.WriteLine($"[Vt100Emulator] Erase in Display: {operation}");
        // System.Console.WriteLine($"[Vt100Emulator] Erase in Display: {operation}");
        
        if (param == 0)
        {
            // Erase from cursor to end of screen
            EraseFromCursorToEndOfScreen();
        }
        else if (param == 1)
        {
            // Erase from cursor to beginning of screen
            EraseFromCursorToBeginningOfScreen();
        }
        else if (param == 2)
        {
            // Erase entire screen
            EraseEntireScreen();
        }
        else if (param == 3)
        {
            // Erase entire screen and scrollback buffer (if supported)
            // For now, treat same as param 2
            EraseEntireScreen();
        }
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessEraseInLine(List<int> parameters)
    {
        int param = parameters.Count > 0 ? parameters[0] : 0;
        
        if (_currentLineIndex < 0 || _currentLineIndex >= _lines.Count)
        {
            Debug.WriteLine($"[Vt100Emulator] Erase in Line: Invalid line index ({_currentLineIndex}), ignoring");
            // System.Console.WriteLine($"[Vt100Emulator] Erase in Line: Invalid line index ({_currentLineIndex}), ignoring");
            return;
        }
        
        string operation = param switch
        {
            0 => "Erase from cursor to end of line",
            1 => "Erase from cursor to beginning of line",
            2 => "Erase entire line",
            _ => $"Unknown erase operation (param={param})"
        };
        Debug.WriteLine($"[Vt100Emulator] Erase in Line: {operation}");
        // System.Console.WriteLine($"[Vt100Emulator] Erase in Line: {operation}");
        
        var line = _lines[_currentLineIndex];
        
        if (param == 0)
        {
            // Erase from cursor to end of line
            EraseFromCursorToEndOfLine(line);
        }
        else if (param == 1)
        {
            // Erase from cursor to beginning of line
            EraseFromCursorToBeginningOfLine(line);
        }
        else if (param == 2)
        {
            // Erase entire line
            EraseEntireLine(line);
        }
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void EraseFromCursorToEndOfScreen()
    {
        if (_currentLineIndex < 0 || _currentLineIndex >= _lines.Count)
        {
            return;
        }
        
        // Erase from cursor position to end of current line
        var currentLine = _lines[_currentLineIndex];
        EraseFromCursorToEndOfLine(currentLine);
        
        if (_inAlternateScreen)
        {
            for (int i = _currentLineIndex + 1; i < _rows; i++)
            {
                _lines[i].Cells.Clear();
                MarkLineDirty(i);
            }
        }
        else
        {
            // Main screen: remove lines after current line
            if (_currentLineIndex + 1 < _lines.Count)
            {
                _lines.RemoveRange(_currentLineIndex + 1, _lines.Count - (_currentLineIndex + 1));
            }
        }
    }

    private void EraseFromCursorToBeginningOfScreen()
    {
        if (_currentLineIndex < 0 || _currentLineIndex >= _lines.Count)
        {
            return;
        }
        
        // Erase from beginning of current line to cursor position
        var currentLine = _lines[_currentLineIndex];
        EraseFromCursorToBeginningOfLine(currentLine);
        
        if (_inAlternateScreen)
        {
            for (int i = 0; i < _currentLineIndex; i++)
            {
                _lines[i].Cells.Clear();
                MarkLineDirty(i);
            }
        }
        else
        {
            // Main screen: remove lines before current line
            if (_currentLineIndex > 0)
            {
                _lines.RemoveRange(0, _currentLineIndex);
                _currentLineIndex = 0;
            }
        }
    }

    private void EraseEntireScreen()
    {
        _lines.Clear();
        _currentLineIndex = -1;
        _writeCol = 0;
        
        // In alternate screen, maintain buffer size
        if (_inAlternateScreen)
        {
            for (int i = 0; i < _rows; i++)
            {
                _lines.Add(new TerminalLine());
            }
        }
    }

    private void EraseFromCursorToEndOfLine(TerminalLine line)
    {
        // Erase cells from cursor position to end of line
        if (_writeCol < line.Cells.Count)
        {
            // Truncate line at cursor position
            line.Cells.RemoveRange(_writeCol, line.Cells.Count - _writeCol);
            MarkLineDirty(line);
        }
    }

    private void CopyLineCells(TerminalLine source, TerminalLine destination)
    {
#if DEBUG
        var stopwatch = Stopwatch.StartNew();
#endif
        destination.Cells.Clear();
        foreach (var cell in source.Cells)
        {
            destination.Cells.Add(new TerminalCell
            {
                Character = cell.Character,
                ForegroundColor = cell.ForegroundColor,
                BackgroundColor = cell.BackgroundColor,
                Bold = cell.Bold,
                Faint = cell.Faint,
                Italic = cell.Italic,
                Underline = cell.Underline,
                Blink = cell.Blink,
                Reverse = cell.Reverse,
                Conceal = cell.Conceal,
                CrossedOut = cell.CrossedOut,
                DoubleUnderline = cell.DoubleUnderline,
                Overline = cell.Overline
            });
        }
        MarkLineDirty(destination);
#if DEBUG
        stopwatch.Stop();
        RecordPerformance("CopyLineCells", stopwatch.ElapsedTicks);
#endif
    }

    private void EraseFromCursorToBeginningOfLine(TerminalLine line)
    {
        // Erase cells from beginning of line to cursor position
        if (_writeCol > 0 && _writeCol <= line.Cells.Count)
        {
            // Remove cells from start to cursor, then shift remaining cells
            line.Cells.RemoveRange(0, _writeCol);
            _writeCol = 0;
            MarkLineDirty(line);
        }
        else if (_writeCol > line.Cells.Count)
        {
            // Cursor is beyond line end, just clear the line
            line.Cells.Clear();
            _writeCol = 0;
            MarkLineDirty(line);
        }
    }

    private void EraseEntireLine(TerminalLine line)
    {
        line.Cells.Clear();
        _writeCol = 0;
        MarkLineDirty(line);
    }

    private void ProcessWindowManipulation(List<int> parameters)
    {
        if (parameters.Count == 0)
        {
            return;
        }
        
        int operation = parameters[0];
        
        switch (operation)
        {
            case 22:
                // Save window title (xterm extension)
                // Save the current title so it can be restored later with operation 23
                _savedTitle = _currentTitle;
                Debug.WriteLine($"[Vt100Emulator] Window title saved: \"{_savedTitle ?? "(null)"}\"");
                // System.Console.WriteLine($"[Vt100Emulator] Window title saved: \"{_savedTitle ?? "(null)"}\"");
                break;
            case 23:
                // Restore window title (xterm extension)
                // Restore the previously saved title
                if (_savedTitle != null)
                {
                    _currentTitle = _savedTitle;
                    TitleChanged?.Invoke(this, _savedTitle);
                    Debug.WriteLine($"[Vt100Emulator] Window title restored: \"{_savedTitle}\"");
                    // System.Console.WriteLine($"[Vt100Emulator] Window title restored: \"{_savedTitle}\"");
                }
                else
                {
                    Debug.WriteLine($"[Vt100Emulator] Window title restore requested but no title was saved");
                    // System.Console.WriteLine($"[Vt100Emulator] Window title restore requested but no title was saved");
                }
                break;
            default:
                // Other window manipulation operations not implemented
                // Most window manipulation (move, resize, maximize) doesn't apply to client-side terminals
                var paramStr = parameters.Count > 0 ? string.Join(";", parameters) : "";
                Debug.WriteLine($"[Vt100Emulator] ***Unhandled window manipulation: operation={operation}, params=[{paramStr}]");
                // System.Console.WriteLine($"[Vt100Emulator] ***Unhandled window manipulation: operation={operation}, params=[{paramStr}]");
                break;
        }
    }

    private void ProcessSetScrollingRegion(List<int> parameters)
    {
        // ESC [ top ; bottom r - Set scrolling region
        // Parameters are 1-based (1 = first line)
        // If no parameters or only one parameter, reset to full screen
        if (parameters.Count == 0)
        {
            // Reset scrolling region to full screen
            _scrollRegionTop = -1;
            _scrollRegionBottom = -1;
            Debug.WriteLine($"[Vt100Emulator] Scrolling region reset to full screen");
            // System.Console.WriteLine($"[Vt100Emulator] Scrolling region reset to full screen");
        }
        else if (parameters.Count >= 2)
        {
            // Convert from 1-based to 0-based
            int top = parameters[0] - 1;
            int bottom = parameters[1] - 1;
            
            // Validate: top must be >= 0, bottom must be >= top, and both must be < terminal height
            if (top >= 0 && bottom >= top && bottom < _rows)
            {
                _scrollRegionTop = top;
                _scrollRegionBottom = bottom;
                Debug.WriteLine($"[Vt100Emulator] Scrolling region set: lines {top + 1}-{bottom + 1} (0-based: {top}-{bottom})");
                // System.Console.WriteLine($"[Vt100Emulator] Scrolling region set: lines {top + 1}-{bottom + 1} (0-based: {top}-{bottom})");
            }
            else
            {
                // Invalid parameters, reset to full screen
                _scrollRegionTop = -1;
                _scrollRegionBottom = -1;
                Debug.WriteLine($"[Vt100Emulator] Invalid scrolling region parameters (top={top}, bottom={bottom}), reset to full screen");
                // System.Console.WriteLine($"[Vt100Emulator] Invalid scrolling region parameters (top={top}, bottom={bottom}), reset to full screen");
            }
        }
        else
        {
            // Only one parameter - reset to full screen
            _scrollRegionTop = -1;
            _scrollRegionBottom = -1;
            Debug.WriteLine($"[Vt100Emulator] Scrolling region reset to full screen (only one parameter provided)");
            // System.Console.WriteLine($"[Vt100Emulator] Scrolling region reset to full screen (only one parameter provided)");
        }
    }

    private void ProcessSingleCharCommand(AnsiCommand command)
    {
        char final = command.FinalChar;
        
        switch (final)
        {
            case 'B':
                // VT52: Cursor Down - move cursor down one line, same column
                MoveCursorDown();
                Debug.WriteLine($"[Vt100Emulator] Handled single-char command: ESC{final} (VT52 Cursor Down)");
                // System.Console.WriteLine($"[Vt100Emulator] Handled single-char command: ESC{final} (VT52 Cursor Down)");
                break;
            case '7':
                // Save cursor position
                // TODO: Implement cursor position save/restore
                Debug.WriteLine($"[Vt100Emulator] Handled single-char command: ESC{final} (Save Cursor Position - not yet implemented)");
                // System.Console.WriteLine($"[Vt100Emulator] Handled single-char command: ESC{final} (Save Cursor Position - not yet implemented)");
                break;
            case '8':
                // Restore cursor position
                // TODO: Implement cursor position save/restore
                Debug.WriteLine($"[Vt100Emulator] Handled single-char command: ESC{final} (Restore Cursor Position - not yet implemented)");
                // System.Console.WriteLine($"[Vt100Emulator] Handled single-char command: ESC{final} (Restore Cursor Position - not yet implemented)");
                break;
            default:
                Debug.WriteLine($"[Vt100Emulator] ***Unhandled single-char command: ESC{final}");
                // System.Console.WriteLine($"[Vt100Emulator] ***Unhandled single-char command: ESC{final}");
                break;
        }
    }

    private void MoveCursorDown()
    {
        // Move cursor down one line, keeping the same column
        if (_inAlternateScreen)
        {
            // Alternate screen: buffer is fixed size, don't add lines
            if (_currentLineIndex < 0)
            {
                _currentLineIndex = 0;
            }
            else if (_currentLineIndex < _rows - 1)
            {
                _currentLineIndex++;
            }
            // If at bottom, stay at bottom (don't scroll)
        }
        else
        {
            // Main screen: can add lines
            if (_currentLineIndex < 0)
            {
                _currentLineIndex = 0;
                if (_lines.Count == 0)
                {
                    _lines.Add(new TerminalLine());
                }
            }
            else
            {
                _currentLineIndex++;
                // Ensure the line exists
                while (_currentLineIndex >= _lines.Count)
                {
                    _lines.Add(new TerminalLine());
                }
            }
        }
        // Column position remains the same
        // Don't fire events - UI will poll cursor position when needed
    }

    private void OnCharacter(object? sender, char c)
    {
#if DEBUG
        var stopwatch = Stopwatch.StartNew();
#endif
        
        // Process characters immediately in order - no batching, no reordering
        
        switch (c)
        {
            case '\r':
                FlushTextBatch();
                var crRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
                var crColBefore = _writeCol;
                _writeCol = 0;
#if DEBUG
                if (DebugMode)
                {
                    var args = new DebugCommandEventArgs
                    {
                        CommandType = "Control Character",
                        Parameters = "CR (Carriage Return)",
                        ResultingState = $"cursor moved to col=0",
                        RawText = "\r",
                        CommandInterpretation = "Carriage Return: Move cursor to column 0",
                        CursorRowBefore = crRowBefore,
                        CursorColBefore = crColBefore,
                        CursorRowAfter = crRowBefore,
                        CursorColAfter = 0
                    };
                    DebugCommandExecuted?.Invoke(this, args);
                }
#endif
                break;
            case '\n':
                FlushTextBatch();
                var lfRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
                var lfColBefore = _writeCol;
                _writeCol = 0;
                NewLine();
                var lfRowAfter = _currentLineIndex >= 0 ? _currentLineIndex : 0;
#if DEBUG
                if (DebugMode)
                {
                    var args = new DebugCommandEventArgs
                    {
                        CommandType = "Control Character",
                        Parameters = "LF (Line Feed)",
                        ResultingState = $"cursor moved to line={lfRowAfter}, col=0",
                        RawText = "\n",
                        CommandInterpretation = "Line Feed: Move cursor to next line, column 0",
                        CursorRowBefore = lfRowBefore,
                        CursorColBefore = lfColBefore,
                        CursorRowAfter = lfRowAfter,
                        CursorColAfter = 0
                    };
                    DebugCommandExecuted?.Invoke(this, args);
                }
#endif
                break;
            case '\t':
                FlushTextBatch();
                var tabRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
                var tabColBefore = _writeCol;
                InsertTab();
                var tabColAfter = _writeCol;
#if DEBUG
                if (DebugMode)
                {
                    var args = new DebugCommandEventArgs
                    {
                        CommandType = "Control Character",
                        Parameters = "TAB (Horizontal Tab)",
                        ResultingState = $"cursor moved to col={tabColAfter}",
                        RawText = "\t",
                        CommandInterpretation = "Horizontal Tab: Move cursor to next tab stop",
                        CursorRowBefore = tabRowBefore,
                        CursorColBefore = tabColBefore,
                        CursorRowAfter = tabRowBefore,
                        CursorColAfter = tabColAfter
                    };
                    DebugCommandExecuted?.Invoke(this, args);
                }
#endif
                break;
            case '\b':
                FlushTextBatch();
                var bsRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
                var bsColBefore = _writeCol;
                if (_writeCol > 0)
                {
                    _writeCol--;
                }
                var bsColAfter = _writeCol;
#if DEBUG
                if (DebugMode)
                {
                    var args = new DebugCommandEventArgs
                    {
                        CommandType = "Control Character",
                        Parameters = "BS (Backspace)",
                        ResultingState = $"cursor moved to col={bsColAfter}",
                        RawText = "\b",
                        CommandInterpretation = "Backspace: Move cursor back one column",
                        CursorRowBefore = bsRowBefore,
                        CursorColBefore = bsColBefore,
                        CursorRowAfter = bsRowBefore,
                        CursorColAfter = bsColAfter
                    };
                    DebugCommandExecuted?.Invoke(this, args);
                }
#endif
                break;
            case '\x07':
                FlushTextBatch();
                var belRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
                var belColBefore = _writeCol;
                Bell?.Invoke(this, EventArgs.Empty);
#if DEBUG
                if (DebugMode)
                {
                    var args = new DebugCommandEventArgs
                    {
                        CommandType = "Control Character",
                        Parameters = "BEL (Bell)",
                        ResultingState = "bell sound triggered",
                        RawText = "\x07",
                        CommandInterpretation = "Bell: Sound terminal bell",
                        CursorRowBefore = belRowBefore,
                        CursorColBefore = belColBefore,
                        CursorRowAfter = belRowBefore,
                        CursorColAfter = belColBefore
                    };
                    DebugCommandExecuted?.Invoke(this, args);
                }
#endif
                break;
            default:
                // Check if character is printable (not a control character)
                // This includes ASCII printable characters (32-126) and all Unicode printable characters
                if (!char.IsControl(c) || c == '\t' || c == '\n' || c == '\r')
                {
                    // Printable character (including Unicode) - process immediately, no batching
                    // Track cursor position at start of batch for logging
                    if (_textBatch.Length == 0)
                    {
                        _textBatchStartRow = _currentLineIndex >= 0 ? _currentLineIndex : 0;
                        _textBatchStartCol = _writeCol;
                    }
                    _textBatch.Append(c);
                    WriteCharacter(c, suppressScreenChanged: false);
                }
                else
                {
                    // Unhandled control character - flush batch and log
                    FlushTextBatch();
                    var charName = GetControlCharName(c);
                    var charCode = (int)c;
#if DEBUG
                    if (DebugMode)
                    {
                        var args = new DebugCommandEventArgs
                        {
                            CommandType = "Unhandled Control Character",
                            Parameters = $"code=0x{charCode:X2}, name={charName}",
                            ResultingState = $"character='\\u{charCode:X4}', not processed",
                            RawText = c.ToString(),
                            CommandInterpretation = $"Unhandled control character: {charName} (0x{charCode:X2})",
                            CursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0,
                            CursorColBefore = _writeCol,
                            CursorRowAfter = _currentLineIndex >= 0 ? _currentLineIndex : 0,
                            CursorColAfter = _writeCol
                        };
                        DebugCommandExecuted?.Invoke(this, args);
                    }
#endif
                }
                break;
        }
        
#if DEBUG
        stopwatch.Stop();
        RecordPerformance("OnCharacter", stopwatch.ElapsedTicks);
#endif
    }
    
    private void FlushTextBatch()
    {
#if DEBUG
        var stopwatch = Stopwatch.StartNew();
#endif
#if DEBUG
        if (DebugMode && _textBatch.Length > 0)
        {
            var text = _textBatch.ToString();
            var textLength = text.Length;
            
            // Use the tracked start position, not a calculated one
            var cursorRowBefore = _textBatchStartRow >= 0 ? _textBatchStartRow : (_currentLineIndex >= 0 ? _currentLineIndex : 0);
            var cursorColBefore = _textBatchStartCol >= 0 ? _textBatchStartCol : 0;
            
            var cursorRowAfter = _currentLineIndex >= 0 ? _currentLineIndex : 0;
            var cursorColAfter = _writeCol;
            
            var args = new DebugCommandEventArgs
            {
                CommandType = "Text",
                Parameters = $"length={textLength}",
                ResultingState = $"text=\"{AnsiParser.EscapeString(text)}\"",
                RawText = text,
                CommandInterpretation = $"Text Output: {textLength} character(s)",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = cursorRowAfter,
                CursorColAfter = cursorColAfter
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        _textBatch.Clear();
        _textBatchStartRow = -1;
        _textBatchStartCol = -1;
#if DEBUG
        stopwatch.Stop();
        RecordPerformance("FlushTextBatch", stopwatch.ElapsedTicks);
#endif
    }

    private void WriteCharacter(char c, bool suppressScreenChanged = false)
    {
#if DEBUG
        var stopwatch = Stopwatch.StartNew();
#endif
        
        if (_inAlternateScreen)
        {
            // Alternate screen: buffer is fixed size (_rows lines)
            if (_currentLineIndex < 0)
            {
                _currentLineIndex = 0;
            }
            else if (_currentLineIndex >= _rows)
            {
                return;
            }
        }
        else
        {
            if (_writeCol >= _cols && _cols > 0)
            {
                NewLine();
            }

            if (_currentLineIndex < 0)
            {
                _currentLineIndex = _lines.Count;
                _lines.Add(new TerminalLine());
            }
        }

        var line = _lines[_currentLineIndex];
        
        if (_insertMode)
        {
            // Insert Mode: shift existing characters to the right
            // Ensure line has enough cells up to current column
            while (line.Cells.Count <= _writeCol)
            {
                line.Cells.Add(new TerminalCell());
            }
            
            // Insert a new cell at the cursor position (shifts existing cells right)
            var newCell = new TerminalCell
            {
                Character = c,
                ForegroundColor = _foregroundColor,
                BackgroundColor = _backgroundColor,
                Bold = _bold,
                Faint = _faint,
                Italic = _italic,
                Underline = _underline,
                DoubleUnderline = _doubleUnderline,
                Blink = _blink,
                Reverse = _reverse,
                Conceal = _conceal,
                CrossedOut = _crossedOut,
                Overline = _overline
            };
            line.Cells.Insert(_writeCol, newCell);
            MarkLineDirty(line);
        }
        else
        {
            // Replace Mode: overwrite existing character (default behavior)
            // Ensure line has enough cells up to current column
            while (line.Cells.Count <= _writeCol)
            {
                line.Cells.Add(new TerminalCell());
            }

            var cell = line.Cells[_writeCol];
            cell.Character = c;
            cell.ForegroundColor = _foregroundColor;
            cell.BackgroundColor = _backgroundColor;
            cell.Bold = _bold;
            cell.Faint = _faint;
            cell.Italic = _italic;
            cell.Underline = _underline;
            cell.DoubleUnderline = _doubleUnderline;
            cell.Blink = _blink;
            cell.Reverse = _reverse;
            cell.Conceal = _conceal;
            cell.CrossedOut = _crossedOut;
            cell.Overline = _overline;
            MarkLineDirty(line);
        }
        _writeCol++;
        
#if DEBUG
        stopwatch.Stop();
        RecordPerformance("WriteCharacter", stopwatch.ElapsedTicks);
#endif
    }

    private void NewLine()
    {
        if (_inAlternateScreen)
        {
            if (_currentLineIndex < 0)
            {
                _currentLineIndex = 0;
            }
            else if (_currentLineIndex >= _rows - 1)
            {
                for (int i = 0; i < _rows - 1; i++)
                {
                    CopyLineCells(_lines[i + 1], _lines[i]);
                    MarkLineDirty(i);
                    MarkLineDirty(i + 1);
                }
                _lines[_rows - 1].Cells.Clear();
                MarkLineDirty(_rows - 1);
                _currentLineIndex = _rows - 1;
            }
            else
            {
                // Move to next line
                _currentLineIndex++;
            }
        }
        else
        {
            if (_currentLineIndex < 0)
            {
                _currentLineIndex = _lines.Count;
            }
            else
            {
                _currentLineIndex++;
            }
            
            // Ensure the line exists
            while (_currentLineIndex >= _lines.Count)
            {
                _lines.Add(new TerminalLine());
            }
        }
        
        _writeCol = 0;
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void InsertTab()
    {
        var tabStop = 8;
        var nextTab = ((_writeCol / tabStop) + 1) * tabStop;
        // Don't limit by terminal width - lines can be any length
        _writeCol = nextTab;
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessCursorPosition(List<int> parameters, AnsiCommand command)
    {
        // CUP (Cursor Position) - ESC [ row ; col H or ESC [ row ; col f
        // Parameters are 1-based (1 = first row/column)
        // Default is 1 if not specified
        int targetRow = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        int targetCol = parameters.Count > 1 && parameters[1] > 0 ? parameters[1] : 1;
        
        // Convert from 1-based to 0-based
        int rowIndex = targetRow - 1;
        int colIndex = targetCol - 1;
        
        // Clamp to valid range
        if (rowIndex < 0) rowIndex = 0;
        if (rowIndex >= _rows) rowIndex = _rows - 1;
        if (colIndex < 0) colIndex = 0;
        if (colIndex >= _cols) colIndex = _cols - 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        if (_inAlternateScreen)
        {
            // Alternate screen: buffer is fixed size, don't add lines
            _currentLineIndex = rowIndex;
            _writeCol = colIndex;
        }
        else
        {
            // Main screen: ensure line exists
            while (rowIndex >= _lines.Count)
            {
                _lines.Add(new TerminalLine());
            }
            _currentLineIndex = rowIndex;
            _writeCol = colIndex;
        }
        
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "CUP",
                Parameters = $"row={targetRow}, col={targetCol}",
                ResultingState = $"cursor moved to line={_currentLineIndex}, col={_writeCol}",
                RawText = command.RawText,
                CommandInterpretation = $"Cursor Position: row {targetRow} (0-based: {rowIndex}), col {targetCol} (0-based: {colIndex})",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = _currentLineIndex,
                CursorColAfter = _writeCol
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessVerticalPositionAbsolute(List<int> parameters, AnsiCommand command)
    {
        // VPA (Vertical Position Absolute) - ESC [ row d
        // Parameter is 1-based (1 = first row)
        // Default is 1 if not specified
        int targetRow = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
        // Convert from 1-based to 0-based
        int rowIndex = targetRow - 1;
        
        // Clamp to valid range
        if (rowIndex < 0) rowIndex = 0;
        if (rowIndex >= _rows) rowIndex = _rows - 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        if (_inAlternateScreen)
        {
            // Alternate screen: buffer is fixed size, don't add lines
            _currentLineIndex = rowIndex;
        }
        else
        {
            // Main screen: ensure line exists
            while (rowIndex >= _lines.Count)
            {
                _lines.Add(new TerminalLine());
            }
            _currentLineIndex = rowIndex;
        }
         
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "VPA",
                Parameters = $"row={targetRow}",
                ResultingState = $"cursor moved to line={_currentLineIndex}",
                RawText = command.RawText,
                CommandInterpretation = $"Vertical Position Absolute: row {targetRow} (0-based: {rowIndex})",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = _currentLineIndex,
                CursorColAfter = _writeCol
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessCursorHorizontalAbsolute(List<int> parameters, AnsiCommand command)
    {
        // CHA (Cursor Horizontal Absolute) - ESC [ col G
        // Parameter is 1-based (1 = first column)
        // Default is 1 if not specified
        int targetCol = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
        // Convert from 1-based to 0-based
        int colIndex = targetCol - 1;
        
        // Clamp to valid range
        if (colIndex < 0) colIndex = 0;
        if (colIndex >= _cols) colIndex = _cols - 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        _writeCol = colIndex;
        
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "CHA",
                Parameters = $"col={targetCol}",
                ResultingState = $"cursor moved to col={_writeCol}",
                RawText = command.RawText,
                CommandInterpretation = $"Cursor Horizontal Absolute: col {targetCol} (0-based: {colIndex})",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = cursorRowBefore,
                CursorColAfter = _writeCol
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessCursorUp(List<int> parameters, AnsiCommand command)
    {
        // CUU (Cursor Up) - ESC [ n A
        // Moves cursor up n lines (default 1)
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        if (_currentLineIndex >= 0)
        {
            _currentLineIndex = Math.Max(0, _currentLineIndex - count);
        }
        else
        {
            _currentLineIndex = 0;
        }
        
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "CUU",
                Parameters = $"count={count}",
                ResultingState = $"cursor moved to line={_currentLineIndex}",
                RawText = command.RawText,
                CommandInterpretation = $"Cursor Up: {count} line(s)",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = _currentLineIndex,
                CursorColAfter = cursorColBefore
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessCursorDown(List<int> parameters, AnsiCommand command)
    {
        // CUD (Cursor Down) - ESC [ n B
        // Moves cursor down n lines (default 1)
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        if (_inAlternateScreen)
        {
            // Alternate screen: clamp to valid range
            if (_currentLineIndex < 0)
            {
                _currentLineIndex = 0;
            }
            else
            {
                _currentLineIndex = Math.Min(_rows - 1, _currentLineIndex + count);
            }
        }
        else
        {
            // Main screen: can add lines
            if (_currentLineIndex < 0)
            {
                _currentLineIndex = 0;
            }
            else
            {
                _currentLineIndex += count;
            }
            while (_currentLineIndex >= _lines.Count)
            {
                _lines.Add(new TerminalLine());
            }
        }
        
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "CUD",
                Parameters = $"count={count}",
                ResultingState = $"cursor moved to line={_currentLineIndex}",
                RawText = command.RawText,
                CommandInterpretation = $"Cursor Down: {count} line(s)",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = _currentLineIndex,
                CursorColAfter = cursorColBefore
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessCursorForward(List<int> parameters, AnsiCommand command)
    {
        // CUF (Cursor Forward) - ESC [ n C
        // Moves cursor right n columns (default 1)
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        _writeCol += count;
        if (_writeCol >= _cols && _cols > 0)
        {
            _writeCol = _cols - 1;
        }
        
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "CUF",
                Parameters = $"count={count}",
                ResultingState = $"cursor moved to col={_writeCol}",
                RawText = command.RawText,
                CommandInterpretation = $"Cursor Forward: {count} column(s)",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = cursorRowBefore,
                CursorColAfter = _writeCol
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessCursorBackward(List<int> parameters, AnsiCommand command)
    {
        // CUB (Cursor Backward) - ESC [ n D
        // Moves cursor left n columns (default 1)
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        _writeCol = Math.Max(0, _writeCol - count);
        
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "CUB",
                Parameters = $"count={count}",
                ResultingState = $"cursor moved to col={_writeCol}",
                RawText = command.RawText,
                CommandInterpretation = $"Cursor Backward: {count} column(s)",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = cursorRowBefore,
                CursorColAfter = _writeCol
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessCursorNextLine(List<int> parameters, AnsiCommand command)
    {
        // CNL (Cursor Next Line) - ESC [ n E
        // Moves cursor to beginning of line n lines down (default 1)
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        if (_inAlternateScreen)
        {
            // Alternate screen: clamp to valid range
            if (_currentLineIndex < 0)
            {
                _currentLineIndex = 0;
            }
            else
            {
                _currentLineIndex = Math.Min(_rows - 1, _currentLineIndex + count);
            }
        }
        else
        {
            // Main screen: can add lines
            if (_currentLineIndex < 0)
            {
                _currentLineIndex = 0;
            }
            else
            {
                _currentLineIndex += count;
            }
            while (_currentLineIndex >= _lines.Count)
            {
                _lines.Add(new TerminalLine());
            }
        }
        
        _writeCol = 0;
        
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "CNL",
                Parameters = $"count={count}",
                ResultingState = $"cursor moved to line={_currentLineIndex}, col=0",
                RawText = command.RawText,
                CommandInterpretation = $"Cursor Next Line: {count} line(s), col=0",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = _currentLineIndex,
                CursorColAfter = 0
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessScrollUp(List<int> parameters, AnsiCommand command)
    {
        // SU (Scroll Up) - ESC [ n S
        // Scrolls the screen up by n lines (default 1)
        // Content moves up: lines from (scrollTop + count) to scrollBottom are copied to scrollTop to (scrollBottom - count)
        // Lines at (scrollBottom - count + 1) to scrollBottom are cleared
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        if (_inAlternateScreen)
        {
            // Determine scroll region bounds
            int scrollTop = _scrollRegionTop >= 0 ? _scrollRegionTop : 0;
            int scrollBottom = _scrollRegionBottom >= 0 ? _scrollRegionBottom : (_rows - 1);
            
            // Clamp count to scroll region size
            int regionSize = scrollBottom - scrollTop + 1;
            count = Math.Min(count, regionSize);
            
            for (int i = scrollBottom - count; i >= scrollTop; i--)
            {
                CopyLineCells(_lines[i + count], _lines[i]);
                MarkLineDirty(i);
                MarkLineDirty(i + count);
            }
            
            for (int i = scrollBottom - count + 1; i <= scrollBottom; i++)
            {
                _lines[i].Cells.Clear();
                MarkLineDirty(i);
            }
        }
        else
        {
            // Main screen: scroll up by removing lines from top
            for (int i = 0; i < count && _lines.Count > 0; i++)
            {
                _lines.RemoveAt(0);
                if (_currentLineIndex > 0)
                {
                    _currentLineIndex--;
                }
            }
        }
        
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "SU",
                Parameters = $"count={count}",
                ResultingState = $"scrolled up {count} line(s), cursor unchanged at line={_currentLineIndex}, col={_writeCol}",
                RawText = command.RawText,
                CommandInterpretation = $"Scroll Up: {count} line(s)",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = _currentLineIndex,
                CursorColAfter = _writeCol
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessScrollDown(List<int> parameters, AnsiCommand command)
    {
        // SD (Scroll Down) - ESC [ n T
        // Scrolls the screen down by n lines (default 1)
        // Content moves down: lines from scrollTop to (scrollBottom - count) are copied to (scrollTop + count) to scrollBottom
        // Lines at scrollTop to (scrollTop + count - 1) are cleared
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        if (_inAlternateScreen)
        {
            // Determine scroll region bounds
            int scrollTop = _scrollRegionTop >= 0 ? _scrollRegionTop : 0;
            int scrollBottom = _scrollRegionBottom >= 0 ? _scrollRegionBottom : (_rows - 1);
            
            // Clamp count to scroll region size
            int regionSize = scrollBottom - scrollTop + 1;
            count = Math.Min(count, regionSize);
            
            for (int i = scrollTop; i <= scrollBottom - count; i++)
            {
                CopyLineCells(_lines[i], _lines[i + count]);
                MarkLineDirty(i);
                MarkLineDirty(i + count);
            }
            
            for (int i = scrollTop; i < scrollTop + count; i++)
            {
                _lines[i].Cells.Clear();
                MarkLineDirty(i);
            }
            
            // Cursor stays at same screen position, so if it's in the scroll region, it moves down with content
            if (_currentLineIndex >= scrollTop && _currentLineIndex <= scrollBottom)
            {
                _currentLineIndex = Math.Min(_currentLineIndex + count, scrollBottom);
            }
        }
        else
        {
            // Main screen: scroll down by inserting blank lines at top
            for (int i = 0; i < count; i++)
            {
                _lines.Insert(0, new TerminalLine());
                if (_currentLineIndex >= 0)
                {
                    _currentLineIndex++;
                }
            }
        }
        
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "SD",
                Parameters = $"count={count}",
                ResultingState = $"scrolled down {count} line(s), cursor at line={_currentLineIndex}, col={_writeCol}",
                RawText = command.RawText,
                CommandInterpretation = $"Scroll Down: {count} line(s)",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = _currentLineIndex,
                CursorColAfter = _writeCol
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessDeleteLine(List<int> parameters, AnsiCommand command)
    {
        // DL (Delete Line) - ESC [ Ps M
        // Deletes Ps lines at the cursor position (default 1)
        // Lines below move up, blank lines inserted at bottom of scroll region
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        if (_inAlternateScreen)
        {
            // Determine scroll region bounds
            int scrollTop = _scrollRegionTop >= 0 ? _scrollRegionTop : 0;
            int scrollBottom = _scrollRegionBottom >= 0 ? _scrollRegionBottom : (_rows - 1);
            
            // Get cursor position
            int cursorRow = _currentLineIndex >= 0 ? _currentLineIndex : 0;
            
            // Clamp cursor row to scroll region
            if (cursorRow < scrollTop) cursorRow = scrollTop;
            if (cursorRow > scrollBottom) cursorRow = scrollBottom;
            
            // Calculate how many lines to delete (can't delete beyond scroll region)
            int maxDelete = scrollBottom - cursorRow + 1;
            count = Math.Min(count, maxDelete);
            
            for (int i = scrollBottom - count; i >= cursorRow; i--)
            {
                CopyLineCells(_lines[i + count], _lines[i]);
            }
            
            // Clear the lines at the bottom of scroll region that were copied from
            for (int i = scrollBottom - count + 1; i <= scrollBottom; i++)
            {
                if (i >= scrollTop && i <= scrollBottom && i >= 0 && i < _lines.Count)
                {
                    _lines[i].Cells.Clear();
                    MarkLineDirty(i);
                }
            }
        }
        else
        {
            // Main screen: delete lines by removing them
            int cursorRow = _currentLineIndex >= 0 ? _currentLineIndex : 0;
            for (int i = 0; i < count && cursorRow < _lines.Count; i++)
            {
                if (cursorRow < _lines.Count)
                {
                    _lines.RemoveAt(cursorRow);
                }
            }
        }
        
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "DL",
                Parameters = $"count={count}",
                ResultingState = $"deleted {count} line(s) at cursor, cursor at line={_currentLineIndex}, col={_writeCol}",
                RawText = command.RawText,
                CommandInterpretation = $"Delete Line: {count} line(s)",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = _currentLineIndex,
                CursorColAfter = _writeCol
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessInsertLine(List<int> parameters, AnsiCommand command)
    {
        // IL (Insert Line) - ESC [ Ps L
        // Inserts Ps blank lines at the cursor position (default 1)
        // Lines below move down, lines at bottom of scroll region are removed
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        if (_inAlternateScreen)
        {
            // Determine scroll region bounds
            int scrollTop = _scrollRegionTop >= 0 ? _scrollRegionTop : 0;
            int scrollBottom = _scrollRegionBottom >= 0 ? _scrollRegionBottom : (_rows - 1);
            
            // Get cursor position
            int cursorRow = _currentLineIndex >= 0 ? _currentLineIndex : 0;
            
            // Clamp cursor row to scroll region
            if (cursorRow < scrollTop) cursorRow = scrollTop;
            if (cursorRow > scrollBottom) cursorRow = scrollBottom;
            
            // Calculate how many lines to insert (can't insert beyond scroll region)
            int maxInsert = scrollBottom - cursorRow + 1;
            count = Math.Min(count, maxInsert);
            
            for (int i = scrollBottom - count; i >= cursorRow; i--)
            {
                CopyLineCells(_lines[i], _lines[i + count]);
                MarkLineDirty(i);
                MarkLineDirty(i + count);
            }
            
            for (int i = cursorRow; i < cursorRow + count; i++)
            {
                _lines[i].Cells.Clear();
                MarkLineDirty(i);
            }
        }
        else
        {
            // Main screen: insert blank lines
            int cursorRow = _currentLineIndex >= 0 ? _currentLineIndex : 0;
            for (int i = 0; i < count; i++)
            {
                _lines.Insert(cursorRow, new TerminalLine());
            }
        }
        
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "IL",
                Parameters = $"count={count}",
                ResultingState = $"inserted {count} blank line(s) at cursor, cursor at line={_currentLineIndex}, col={_writeCol}",
                RawText = command.RawText,
                CommandInterpretation = $"Insert Line: {count} line(s)",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = _currentLineIndex,
                CursorColAfter = _writeCol
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessDeleteCharacter(List<int> parameters, AnsiCommand command)
    {
        // DCH (Delete Character) - ESC [ Ps P
        // Deletes Ps characters at the cursor position (default 1)
        // Characters to the right shift left
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        if (_currentLineIndex >= 0 && _currentLineIndex < _lines.Count)
        {
            var line = _lines[_currentLineIndex];
            
            // Delete characters by shifting left
            if (_writeCol < line.Cells.Count)
            {
                int deleteCount = Math.Min(count, line.Cells.Count - _writeCol);
                line.Cells.RemoveRange(_writeCol, deleteCount);
            }
        }
        
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "DCH",
                Parameters = $"count={count}",
                ResultingState = $"deleted {count} character(s) at cursor, cursor at line={_currentLineIndex}, col={_writeCol}",
                RawText = command.RawText,
                CommandInterpretation = $"Delete Character: {count} character(s)",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = _currentLineIndex,
                CursorColAfter = _writeCol
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessInsertCharacter(List<int> parameters, AnsiCommand command)
    {
        // ICH (Insert Character) - ESC [ Ps @
        // Inserts Ps blank characters at the cursor position (default 1)
        // Existing characters shift right
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        if (_currentLineIndex >= 0 && _currentLineIndex < _lines.Count)
        {
            var line = _lines[_currentLineIndex];
            
            // Ensure line has enough cells up to cursor position
            while (line.Cells.Count < _writeCol)
            {
                line.Cells.Add(new TerminalCell { Character = ' ' });
            }
            
            // Insert blank characters
            var blankCell = new TerminalCell
            {
                Character = ' ',
                ForegroundColor = _foregroundColor,
                BackgroundColor = _backgroundColor,
                Bold = _bold,
                Faint = _faint,
                Italic = _italic,
                Underline = _underline,
                Blink = _blink,
                Reverse = _reverse,
                Conceal = _conceal,
                CrossedOut = _crossedOut,
                DoubleUnderline = _doubleUnderline,
                Overline = _overline
            };
            
            for (int i = 0; i < count; i++)
            {
                line.Cells.Insert(_writeCol, new TerminalCell
                {
                    Character = blankCell.Character,
                    ForegroundColor = blankCell.ForegroundColor,
                    BackgroundColor = blankCell.BackgroundColor,
                    Bold = blankCell.Bold,
                    Faint = blankCell.Faint,
                    Italic = blankCell.Italic,
                    Underline = blankCell.Underline,
                    Blink = blankCell.Blink,
                    Reverse = blankCell.Reverse,
                    Conceal = blankCell.Conceal,
                    CrossedOut = blankCell.CrossedOut,
                    DoubleUnderline = blankCell.DoubleUnderline,
                    Overline = blankCell.Overline
                });
            }
        }
        
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "ICH",
                Parameters = $"count={count}",
                ResultingState = $"inserted {count} blank character(s) at cursor, cursor at line={_currentLineIndex}, col={_writeCol}",
                RawText = command.RawText,
                CommandInterpretation = $"Insert Character: {count} character(s)",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = _currentLineIndex,
                CursorColAfter = _writeCol
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessCursorPreviousLine(List<int> parameters, AnsiCommand command)
    {
        // CPL (Cursor Previous Line) - ESC [ n F
        // Moves cursor to beginning of line n lines up (default 1)
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        if (_currentLineIndex >= 0)
        {
            _currentLineIndex = Math.Max(0, _currentLineIndex - count);
        }
        else
        {
            _currentLineIndex = 0;
        }
        
        _writeCol = 0;
        
#if DEBUG
        if (DebugMode)
        {
            var args = new DebugCommandEventArgs
            {
                CommandType = "CPL",
                Parameters = $"count={count}",
                ResultingState = $"cursor moved to line={_currentLineIndex}, col=0",
                RawText = command.RawText,
                CommandInterpretation = $"Cursor Previous Line: {count} line(s), col=0",
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = _currentLineIndex,
                CursorColAfter = 0
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    // Process SGR (Select Graphic Rendition) command - ANSI escape sequence for text styles and colors
    // Format: ESC[<parameters>m where parameters are semicolon-separated numbers
    private void ProcessSgr(List<int> parameters, AnsiCommand? command = null)
    {
#if DEBUG
        var cursorRowBefore = _currentLineIndex >= 0 ? _currentLineIndex : 0;
        var cursorColBefore = _writeCol;
#endif
        
        if (parameters.Count == 0)
        {
            ResetAttributes();
#if DEBUG
            if (DebugMode)
            {
                var rawText = command?.RawText ?? "\x1B[m";
                var args = new DebugCommandEventArgs
                {
                    CommandType = "SGR",
                    Parameters = "0 (reset all)",
                    ResultingState = "all attributes reset to defaults",
                    RawText = rawText,
                    CommandInterpretation = "SGR (Select Graphic Rendition): Reset all attributes",
                    CursorRowBefore = cursorRowBefore,
                    CursorColBefore = cursorColBefore,
                    CursorRowAfter = cursorRowBefore,
                    CursorColAfter = cursorColBefore
                };
                DebugCommandExecuted?.Invoke(this, args);
            }
#endif
            return;
        }

        int i = 0;
        while (i < parameters.Count)
        {
            var param = parameters[i];
            int consumed = 1; // Default: consume 1 parameter
            
            if (param == 0)
            {
                // Reset all attributes to default
                ResetAttributes();
            }
            else if (param == 1)
            {
                // Bold or increased intensity
                _bold = true;
                _faint = false;
            }
            else if (param == 2)
            {
                // Faint, decreased intensity, or dim
                _faint = true;
                _bold = false;
            }
            else if (param == 3)
            {
                // Italic
                _italic = true;
            }
            else if (param == 4)
            {
                // Underline
                _underline = true;
                _doubleUnderline = false;
            }
            else if (param == 5)
            {
                // Slow blink
                _blink = true;
            }
            else if (param == 6)
            {
                // Rapid blink (treated same as slow blink)
                _blink = true;
            }
            else if (param == 7)
            {
                // Reverse video (swap foreground and background colors)
                _reverse = true;
            }
            else if (param == 8)
            {
                // Conceal (hide text, also called "invisible")
                _conceal = true;
            }
            else if (param == 9)
            {
                // Crossed out (strikethrough)
                _crossedOut = true;
            }
            else if (param >= 10 && param <= 19)
            {
                // Font selection (10-19) - not implemented, ignored
            }
            else if (param == 20)
            {
                // Fraktur (Gothic font) - not implemented, ignored
            }
            else if (param == 21)
            {
                // Bold off or double underline (implementation-dependent)
                _bold = false;
                _doubleUnderline = false;
            }
            else if (param == 22)
            {
                // Normal intensity (neither bold nor faint)
                _bold = false;
                _faint = false;
            }
            else if (param == 23)
            {
                // Not italic, not fraktur
                _italic = false;
            }
            else if (param == 24)
            {
                // Underline off
                _underline = false;
                _doubleUnderline = false;
            }
            else if (param == 25)
            {
                // Blink off
                _blink = false;
            }
            else if (param == 27)
            {
                // Reverse video off
                _reverse = false;
            }
            else if (param == 28)
            {
                // Conceal off (reveal text)
                _conceal = false;
            }
            else if (param == 29)
            {
                // Crossed out off
                _crossedOut = false;
            }
            else if (param >= 30 && param <= 37)
            {
                // Set foreground color to standard color (30-37 = black, red, green, yellow, blue, magenta, cyan, white)
                _foregroundColor = param - 30;
            }
            else if (param == 38)
            {
                // Set foreground color (extended)
                // Format: 38;5;<index> for 256-color palette, or 38;2;<r>;<g>;<b> for RGB
                if (i + 1 < parameters.Count)
                {
                    var colorType = parameters[i + 1];
                    if (colorType == 5 && i + 2 < parameters.Count)
                    {
                        // 256-color palette: 38;5;<index>
                        var colorIndex = parameters[i + 2];
                        _foregroundColor = colorIndex;
                        consumed = 3;
                    }
                    else if (colorType == 2 && i + 4 < parameters.Count)
                    {
                        // True color RGB: 38;2;<r>;<g>;<b>
                        // Store as packed RGB: 0x1000000 | (r << 16) | (g << 8) | b
                        var r = Math.Max(0, Math.Min(255, parameters[i + 2]));
                        var g = Math.Max(0, Math.Min(255, parameters[i + 3]));
                        var b = Math.Max(0, Math.Min(255, parameters[i + 4]));
                        _foregroundColor = 0x1000000 | (r << 16) | (g << 8) | b;
                        consumed = 5;
                    }
                }
            }
            else if (param == 39)
            {
                // Default foreground color
                _foregroundColor = 7;
            }
            else if (param >= 40 && param <= 47)
            {
                // Set background color to standard color (40-47 = black, red, green, yellow, blue, magenta, cyan, white)
                _backgroundColor = param - 40;
            }
            else if (param == 48)
            {
                // Set background color (extended)
                // Format: 48;5;<index> for 256-color palette, or 48;2;<r>;<g>;<b> for RGB
                if (i + 1 < parameters.Count)
                {
                    var colorType = parameters[i + 1];
                    if (colorType == 5 && i + 2 < parameters.Count)
                    {
                        // 256-color palette: 48;5;<index>
                        var colorIndex = parameters[i + 2];
                        _backgroundColor = colorIndex;
                        consumed = 3;
                    }
                    else if (colorType == 2 && i + 4 < parameters.Count)
                    {
                        // True color RGB: 48;2;<r>;<g>;<b>
                        // Store as packed RGB: 0x1000000 | (r << 16) | (g << 8) | b
                        var r = Math.Max(0, Math.Min(255, parameters[i + 2]));
                        var g = Math.Max(0, Math.Min(255, parameters[i + 3]));
                        var b = Math.Max(0, Math.Min(255, parameters[i + 4]));
                        _backgroundColor = 0x1000000 | (r << 16) | (g << 8) | b;
                        consumed = 5;
                    }
                }
            }
            else if (param == 49)
            {
                // Default background color
                _backgroundColor = 0;
            }
            else if (param >= 50 && param <= 59)
            {
                // Framed, encircled, overlined, etc. - not implemented, ignored
            }
            else if (param >= 60 && param <= 65)
            {
                // Ideogram attributes - not implemented, ignored
            }
            else if (param >= 73 && param <= 75)
            {
                // Superscript, subscript - not implemented, ignored
            }
            else if (param >= 90 && param <= 97)
            {
                // Bright foreground color (90-97 = bright black, red, green, yellow, blue, magenta, cyan, white)
                _foregroundColor = param - 90 + 8;
            }
            else if (param >= 100 && param <= 107)
            {
                // Bright background color (100-107 = bright black, red, green, yellow, blue, magenta, cyan, white)
                _backgroundColor = param - 100 + 8;
            }
            else
            {
                // Unknown parameter - skip it
            }
            
            i += consumed;
        }
        
#if DEBUG
        if (DebugMode)
        {
            var rawText = command?.RawText ?? $"\x1B[{string.Join(";", parameters)}m";
            var paramStr = string.Join(";", parameters);
            var interpretation = $"SGR (Select Graphic Rendition): Set text attributes";
            var resultingState = $"fg={_foregroundColor}, bg={_backgroundColor}, bold={_bold}, underline={_underline}, italic={_italic}, reverse={_reverse}";
            var args = new DebugCommandEventArgs
            {
                CommandType = "SGR",
                Parameters = paramStr,
                ResultingState = resultingState,
                RawText = rawText,
                CommandInterpretation = interpretation,
                CursorRowBefore = cursorRowBefore,
                CursorColBefore = cursorColBefore,
                CursorRowAfter = cursorRowBefore,
                CursorColAfter = cursorColBefore
            };
            DebugCommandExecuted?.Invoke(this, args);
        }
#endif
    }

    private static int ConvertRgbTo256Color(int r, int g, int b)
    {
        if (r == g && g == b && r < 8)
        {
            return r == 0 ? 0 : 232 + (r * 24 / 255);
        }
        var ri = (r * 5) / 255;
        var gi = (g * 5) / 255;
        var bi = (b * 5) / 255;
        return 16 + (ri * 36) + (gi * 6) + bi;
    }

    private void ResetAttributes()
    {
        _foregroundColor = 7;
        _backgroundColor = 0;
        _bold = false;
        _faint = false;
        _italic = false;
        _underline = false;
        _doubleUnderline = false;
        _blink = false;
        _reverse = false;
        _conceal = false;
        _crossedOut = false;
        _overline = false;
    }

    private void SaveMainScreenState()
    {
        // Deep copy the lines
        _savedMainScreenLines = new List<TerminalLine>();
        foreach (var line in _lines)
        {
            var savedLine = new TerminalLine();
            foreach (var cell in line.Cells)
            {
                var savedCell = new TerminalCell
                {
                    Character = cell.Character,
                    ForegroundColor = cell.ForegroundColor,
                    BackgroundColor = cell.BackgroundColor,
                    Bold = cell.Bold,
                    Faint = cell.Faint,
                    Italic = cell.Italic,
                    Underline = cell.Underline,
                    Blink = cell.Blink,
                    Reverse = cell.Reverse,
                    Conceal = cell.Conceal,
                    CrossedOut = cell.CrossedOut,
                    DoubleUnderline = cell.DoubleUnderline,
                    Overline = cell.Overline
                };
                savedLine.Cells.Add(savedCell);
            }
            _savedMainScreenLines.Add(savedLine);
        }
        
        // Save cursor position and attributes
        _savedMainScreenLineIndex = _currentLineIndex;
        _savedMainScreenWriteCol = _writeCol;
        _savedMainScreenForegroundColor = _foregroundColor;
        _savedMainScreenBackgroundColor = _backgroundColor;
        _savedMainScreenBold = _bold;
        _savedMainScreenFaint = _faint;
        _savedMainScreenItalic = _italic;
        _savedMainScreenUnderline = _underline;
        _savedMainScreenBlink = _blink;
        _savedMainScreenReverse = _reverse;
        _savedMainScreenConceal = _conceal;
        _savedMainScreenCrossedOut = _crossedOut;
        _savedMainScreenDoubleUnderline = _doubleUnderline;
        _savedMainScreenOverline = _overline;
    }

    private void SwitchToAlternateScreen()
    {
        _inAlternateScreen = true;
        _lines.Clear();
        _currentLineIndex = -1;
        _writeCol = 0;
        
        // Pre-allocate _rows empty lines so the buffer is always the correct size
        // This ensures rendering always shows _rows lines from the top
        for (int i = 0; i < _rows; i++)
        {
            _lines.Add(new TerminalLine());
        }
        
        // Reset attributes to defaults
        ResetAttributes();
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void RestoreMainScreenState()
    {
        _inAlternateScreen = false;
        
        if (_savedMainScreenLines == null)
        {
            _lines.Clear();
            _currentLineIndex = -1;
            _writeCol = 0;
            ResetAttributes();
            // Don't fire events - ScreenChanged will be fired at end of ProcessData
            return;
        }
        
        // Restore the saved lines
        _lines.Clear();
        foreach (var savedLine in _savedMainScreenLines)
        {
            var restoredLine = new TerminalLine();
            foreach (var savedCell in savedLine.Cells)
            {
                var restoredCell = new TerminalCell
                {
                    Character = savedCell.Character,
                    ForegroundColor = savedCell.ForegroundColor,
                    BackgroundColor = savedCell.BackgroundColor,
                    Bold = savedCell.Bold,
                    Faint = savedCell.Faint,
                    Italic = savedCell.Italic,
                    Underline = savedCell.Underline,
                    Blink = savedCell.Blink,
                    Reverse = savedCell.Reverse,
                    Conceal = savedCell.Conceal,
                    CrossedOut = savedCell.CrossedOut,
                    DoubleUnderline = savedCell.DoubleUnderline,
                    Overline = savedCell.Overline
                };
                restoredLine.Cells.Add(restoredCell);
            }
            _lines.Add(restoredLine);
        }
        
        // Restore cursor position and attributes
        _currentLineIndex = _savedMainScreenLineIndex;
        _writeCol = _savedMainScreenWriteCol;
        _foregroundColor = _savedMainScreenForegroundColor;
        _backgroundColor = _savedMainScreenBackgroundColor;
        _bold = _savedMainScreenBold;
        _faint = _savedMainScreenFaint;
        _italic = _savedMainScreenItalic;
        _underline = _savedMainScreenUnderline;
        _blink = _savedMainScreenBlink;
        _reverse = _savedMainScreenReverse;
        _conceal = _savedMainScreenConceal;
        _crossedOut = _savedMainScreenCrossedOut;
        _doubleUnderline = _savedMainScreenDoubleUnderline;
        _overline = _savedMainScreenOverline;
        
        // Clear saved state
        _savedMainScreenLines = null;
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private static string GetControlCharName(char c)
    {
        return c switch
        {
            '\x00' => "NUL",
            '\x01' => "SOH",
            '\x02' => "STX",
            '\x03' => "ETX",
            '\x04' => "EOT",
            '\x05' => "ENQ",
            '\x06' => "ACK",
            '\x07' => "BEL",
            '\x08' => "BS",
            '\x09' => "TAB",
            '\x0A' => "LF",
            '\x0B' => "VT",
            '\x0C' => "FF",
            '\x0D' => "CR",
            '\x0E' => "SO",
            '\x0F' => "SI",
            '\x10' => "DLE",
            '\x11' => "DC1",
            '\x12' => "DC2",
            '\x13' => "DC3",
            '\x14' => "DC4",
            '\x15' => "NAK",
            '\x16' => "SYN",
            '\x17' => "ETB",
            '\x18' => "CAN",
            '\x19' => "EM",
            '\x1A' => "SUB",
            '\x1B' => "ESC",
            '\x1C' => "FS",
            '\x1D' => "GS",
            '\x1E' => "RS",
            '\x1F' => "US",
            '\x7F' => "DEL",
            _ => $"UNKNOWN(0x{(int)c:X2})"
        };
    }
}

