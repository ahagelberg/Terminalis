using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

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
}

public class Vt100Emulator
{
    private const int DEFAULT_COLS = 80;
    private const int DEFAULT_ROWS = 24;

    private readonly List<TerminalLine> _lines = new();
    private readonly AnsiParser _ansiParser = new();
    private readonly StringBuilder _characterBatch = new();
    private bool _batchingCharacters = false;
    private int _currentLineIndex = -1;
    private int _writeCol = 0;
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
    private bool _autoWrapMode = true; // DECAWM: true = auto-wrap enabled (default), false = auto-wrap disabled
    
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

    public int Rows => _rows;
    public int Cols => _cols;
    public int LineCount => _lines.Count;
    public bool BracketedPasteMode => _bracketedPasteMode;
    public bool CursorKeyMode => _cursorKeyMode;
    
    // Stub properties for UI compatibility
    public int CursorRow => _currentLineIndex >= 0 ? _currentLineIndex : 0;
    public int CursorCol => _writeCol;
    public int ScrollbackLineCount => 0;

    public event EventHandler? ScreenChanged;
    public event EventHandler? CursorMoved;
    public event EventHandler? Bell;
    public event EventHandler<string>? TitleChanged;

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
        var escapedData = AnsiParser.EscapeString(data);
        Debug.WriteLine($"[Vt100Emulator] ProcessData: \"{escapedData}\" ({data.Length} bytes)");
        System.Console.WriteLine($"[Vt100Emulator] ProcessData: \"{escapedData}\" ({data.Length} bytes)");
        
        BeginCharacterBatch();
        try
        {
            _ansiParser.ProcessData(data);
        }
        finally
        {
            EndCharacterBatch();
        }
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

    private void OnAnsiCommand(object? sender, AnsiCommand command)
    {
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
            System.Console.WriteLine($"[Vt100Emulator] ***Unhandled ANSI command: {commandDesc}");
        }
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
                        System.Console.WriteLine($"[Vt100Emulator] Handled OSC command: ESC]{oscCode};{title} (Set window title)");
                        TitleChanged?.Invoke(this, title);
                    }
                }
                break;
            default:
                // Other OSC commands not implemented yet
                Debug.WriteLine($"[Vt100Emulator] ***Unhandled OSC command: {oscString} (code: {oscCode})");
                System.Console.WriteLine($"[Vt100Emulator] ***Unhandled OSC command: {oscString} (code: {oscCode})");
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
            var paramStr = p.Count > 0 ? string.Join(";", p) : "";
            var commandStr = $"\x1B[{paramStr}m";
            var escapedStr = AnsiParser.EscapeString(commandStr);
            Debug.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (SGR - Select Graphic Rendition)");
            System.Console.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (SGR - Select Graphic Rendition)");
            ProcessSgr(p);
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
                System.Console.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (DEC Mode change: mode={p[0]}, set={final == 'h'})");
                ProcessDecModeChange(p[0], final == 'h');
            }
            else
            {
                // Non-private mode changes (ANSI modes) - ESC [ ... h/l
                var paramStr = p.Count > 0 ? string.Join(";", p) : "";
                var commandStr = $"\x1B[{paramStr}{final}";
                var escapedStr = AnsiParser.EscapeString(commandStr);
                Debug.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (ANSI Mode change: mode={p[0]}, set={final == 'h'})");
                System.Console.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (ANSI Mode change: mode={p[0]}, set={final == 'h'})");
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
            System.Console.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Erase in Display)");
            ProcessEraseInDisplay(p);
        }
        // Process Erase in Line (K) command
        else if (final == 'K')
        {
            var paramStr = p.Count > 0 ? string.Join(";", p) : "";
            var commandStr = $"\x1B[{paramStr}K";
            var escapedStr = AnsiParser.EscapeString(commandStr);
            Debug.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Erase in Line)");
            System.Console.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Erase in Line)");
            ProcessEraseInLine(p);
        }
        // Process Window Manipulation (t) commands
        else if (final == 't')
        {
            var paramStr = p.Count > 0 ? string.Join(";", p) : "";
            var commandStr = $"\x1B[{paramStr}t";
            var escapedStr = AnsiParser.EscapeString(commandStr);
            Debug.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Window Manipulation)");
            System.Console.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Window Manipulation)");
            ProcessWindowManipulation(p);
        }
        // Process Set Scrolling Region (r) command
        else if (final == 'r')
        {
            var paramStr = p.Count > 0 ? string.Join(";", p) : "";
            var commandStr = $"\x1B[{paramStr}r";
            var escapedStr = AnsiParser.EscapeString(commandStr);
            Debug.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Set Scrolling Region)");
            System.Console.WriteLine($"[Vt100Emulator] Handled CSI command: {escapedStr} (Set Scrolling Region)");
            ProcessSetScrollingRegion(p);
        }
        else
        {
            // Warn about unhandled CSI commands
            var paramStr = p.Count > 0 ? string.Join(";", p) : "";
            var isPrivate = command.IsPrivate ? "?" : "";
            var commandStr = $"\x1B[{isPrivate}{paramStr}{final}";
            var escapedStr = AnsiParser.EscapeString(commandStr);
            Debug.WriteLine($"[Vt100Emulator] ***Unhandled CSI command: {escapedStr} (final char: '{final}' (0x{(int)final:X2}), params: [{paramStr}])");
            System.Console.WriteLine($"[Vt100Emulator] ***Unhandled CSI command: {escapedStr} (final char: '{final}' (0x{(int)final:X2}), params: [{paramStr}])");
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
                Debug.WriteLine($"[Vt100Emulator] Cursor Key Mode (DECCKM): {(set ? "cursor mode (ESC [)" : "application mode (ESC O)")}");
                System.Console.WriteLine($"[Vt100Emulator] Cursor Key Mode (DECCKM): {(set ? "cursor mode (ESC [)" : "application mode (ESC O)")}");
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
                System.Console.WriteLine($"[Vt100Emulator] Alternate Screen Buffer (DECALTB): {(set ? "enabled" : "disabled")}");
                break;
            case 7:
                // DECAWM - Auto-Wrap Mode
                // When enabled (set=true): text automatically wraps to next line when cursor reaches right margin
                // When disabled (set=false): cursor stays at right margin, characters overwrite last character
                _autoWrapMode = set;
                Debug.WriteLine($"[Vt100Emulator] Auto-Wrap Mode (DECAWM): {(set ? "enabled (text wraps at right margin)" : "disabled (text doesn't wrap)")}");
                System.Console.WriteLine($"[Vt100Emulator] Auto-Wrap Mode (DECAWM): {(set ? "enabled (text wraps at right margin)" : "disabled (text doesn't wrap)")}");
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
                System.Console.WriteLine($"[Vt100Emulator] ***Unhandled DEC mode change: mode={mode}, set={set}");
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
                System.Console.WriteLine($"[Vt100Emulator] Insert/Replace Mode (IRM): {(set ? "Insert Mode (characters shift right)" : "Replace Mode (characters overwrite)")}");
                break;
            default:
                // Other ANSI mode changes not implemented yet
                Debug.WriteLine($"[Vt100Emulator] ***Unhandled ANSI mode change: mode={mode}, set={set}");
                System.Console.WriteLine($"[Vt100Emulator] ***Unhandled ANSI mode change: mode={mode}, set={set}");
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
        System.Console.WriteLine($"[Vt100Emulator] Erase in Display: {operation}");
        
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
        
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ProcessEraseInLine(List<int> parameters)
    {
        int param = parameters.Count > 0 ? parameters[0] : 0;
        
        if (_currentLineIndex < 0 || _currentLineIndex >= _lines.Count)
        {
            Debug.WriteLine($"[Vt100Emulator] Erase in Line: Invalid line index ({_currentLineIndex}), ignoring");
            System.Console.WriteLine($"[Vt100Emulator] Erase in Line: Invalid line index ({_currentLineIndex}), ignoring");
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
        System.Console.WriteLine($"[Vt100Emulator] Erase in Line: {operation}");
        
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
        
        ScreenChanged?.Invoke(this, EventArgs.Empty);
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
        
        // Erase all lines after current line
        if (_currentLineIndex + 1 < _lines.Count)
        {
            _lines.RemoveRange(_currentLineIndex + 1, _lines.Count - (_currentLineIndex + 1));
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
        
        // Erase all lines before current line
        if (_currentLineIndex > 0)
        {
            _lines.RemoveRange(0, _currentLineIndex);
            _currentLineIndex = 0;
        }
    }

    private void EraseEntireScreen()
    {
        _lines.Clear();
        _currentLineIndex = -1;
        _writeCol = 0;
    }

    private void EraseFromCursorToEndOfLine(TerminalLine line)
    {
        // Erase cells from cursor position to end of line
        if (_writeCol < line.Cells.Count)
        {
            // Truncate line at cursor position
            line.Cells.RemoveRange(_writeCol, line.Cells.Count - _writeCol);
        }
    }

    private void EraseFromCursorToBeginningOfLine(TerminalLine line)
    {
        // Erase cells from beginning of line to cursor position
        if (_writeCol > 0 && _writeCol <= line.Cells.Count)
        {
            // Remove cells from start to cursor, then shift remaining cells
            line.Cells.RemoveRange(0, _writeCol);
            _writeCol = 0;
        }
        else if (_writeCol > line.Cells.Count)
        {
            // Cursor is beyond line end, just clear the line
            line.Cells.Clear();
            _writeCol = 0;
        }
    }

    private void EraseEntireLine(TerminalLine line)
    {
        line.Cells.Clear();
        _writeCol = 0;
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
                System.Console.WriteLine($"[Vt100Emulator] Window title saved: \"{_savedTitle ?? "(null)"}\"");
                break;
            case 23:
                // Restore window title (xterm extension)
                // Restore the previously saved title
                if (_savedTitle != null)
                {
                    _currentTitle = _savedTitle;
                    TitleChanged?.Invoke(this, _savedTitle);
                    Debug.WriteLine($"[Vt100Emulator] Window title restored: \"{_savedTitle}\"");
                    System.Console.WriteLine($"[Vt100Emulator] Window title restored: \"{_savedTitle}\"");
                }
                else
                {
                    Debug.WriteLine($"[Vt100Emulator] Window title restore requested but no title was saved");
                    System.Console.WriteLine($"[Vt100Emulator] Window title restore requested but no title was saved");
                }
                break;
            default:
                // Other window manipulation operations not implemented
                // Most window manipulation (move, resize, maximize) doesn't apply to client-side terminals
                var paramStr = parameters.Count > 0 ? string.Join(";", parameters) : "";
                Debug.WriteLine($"[Vt100Emulator] ***Unhandled window manipulation: operation={operation}, params=[{paramStr}]");
                System.Console.WriteLine($"[Vt100Emulator] ***Unhandled window manipulation: operation={operation}, params=[{paramStr}]");
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
            System.Console.WriteLine($"[Vt100Emulator] Scrolling region reset to full screen");
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
                System.Console.WriteLine($"[Vt100Emulator] Scrolling region set: lines {top + 1}-{bottom + 1} (0-based: {top}-{bottom})");
            }
            else
            {
                // Invalid parameters, reset to full screen
                _scrollRegionTop = -1;
                _scrollRegionBottom = -1;
                Debug.WriteLine($"[Vt100Emulator] Invalid scrolling region parameters (top={top}, bottom={bottom}), reset to full screen");
                System.Console.WriteLine($"[Vt100Emulator] Invalid scrolling region parameters (top={top}, bottom={bottom}), reset to full screen");
            }
        }
        else
        {
            // Only one parameter - reset to full screen
            _scrollRegionTop = -1;
            _scrollRegionBottom = -1;
            Debug.WriteLine($"[Vt100Emulator] Scrolling region reset to full screen (only one parameter provided)");
            System.Console.WriteLine($"[Vt100Emulator] Scrolling region reset to full screen (only one parameter provided)");
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
                System.Console.WriteLine($"[Vt100Emulator] Handled single-char command: ESC{final} (VT52 Cursor Down)");
                break;
            case '7':
                // Save cursor position
                // TODO: Implement cursor position save/restore
                Debug.WriteLine($"[Vt100Emulator] Handled single-char command: ESC{final} (Save Cursor Position - not yet implemented)");
                System.Console.WriteLine($"[Vt100Emulator] Handled single-char command: ESC{final} (Save Cursor Position - not yet implemented)");
                break;
            case '8':
                // Restore cursor position
                // TODO: Implement cursor position save/restore
                Debug.WriteLine($"[Vt100Emulator] Handled single-char command: ESC{final} (Restore Cursor Position - not yet implemented)");
                System.Console.WriteLine($"[Vt100Emulator] Handled single-char command: ESC{final} (Restore Cursor Position - not yet implemented)");
                break;
            default:
                Debug.WriteLine($"[Vt100Emulator] ***Unhandled single-char command: ESC{final}");
                System.Console.WriteLine($"[Vt100Emulator] ***Unhandled single-char command: ESC{final}");
                break;
        }
    }

    private void MoveCursorDown()
    {
        // Move cursor down one line, keeping the same column
        if (_currentLineIndex < 0)
        {
            // No current line, create one
            _currentLineIndex = 0;
            if (_lines.Count == 0)
            {
                _lines.Add(new TerminalLine());
            }
        }
        else
        {
            // Move to next line
            _currentLineIndex++;
            // Ensure the line exists
            while (_currentLineIndex >= _lines.Count)
            {
                _lines.Add(new TerminalLine());
            }
        }
        // Column position remains the same
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    private void OnCharacter(object? sender, char c)
    {
        if (_batchingCharacters && c >= 32 && c < 127 && c != '\r' && c != '\n' && c != '\t' && c != '\b' && c != '\x07')
        {
            _characterBatch.Append(c);
            return;
        }
        
        FlushCharacterBatch();
        
        var charDisplay = c >= 32 && c < 127 ? $"'{c}'" : $"0x{(int)c:X2}";
        Debug.WriteLine($"[Vt100Emulator] OnCharacter: {charDisplay} (line={_currentLineIndex}, col={_writeCol})");
        System.Console.WriteLine($"[Vt100Emulator] OnCharacter: {charDisplay} (line={_currentLineIndex}, col={_writeCol})");
        
        switch (c)
        {
            case '\r':
                _writeCol = 0;
                CursorMoved?.Invoke(this, EventArgs.Empty);
                break;
            case '\n':
                _writeCol = 0;
                NewLine();
                break;
            case '\t':
                InsertTab();
                break;
            case '\b':
                if (_writeCol > 0)
                {
                    _writeCol--;
                    CursorMoved?.Invoke(this, EventArgs.Empty);
                }
                break;
            case '\x07':
                Bell?.Invoke(this, EventArgs.Empty);
                break;
            default:
                if (c >= 32 && c < 127)
                {
                    // Printable ASCII character
                    WriteCharacter(c, suppressScreenChanged: false);
                }
                else
                {
                    // Unhandled control character
                    var charName = GetControlCharName(c);
                    var charCode = (int)c;
                    Debug.WriteLine($"[Vt100Emulator] ***Unhandled control character: {charName} (0x{charCode:X2}, '\\u{charCode:X4}')");
                    System.Console.WriteLine($"[Vt100Emulator] ***Unhandled control character: {charName} (0x{charCode:X2}, '\\u{charCode:X4}')");
                }
                break;
        }
    }
    
    public void BeginCharacterBatch()
    {
        _batchingCharacters = true;
        _characterBatch.Clear();
    }
    
    public void EndCharacterBatch()
    {
        FlushCharacterBatch();
        _batchingCharacters = false;
    }
    
    private void FlushCharacterBatch()
    {
        if (_characterBatch.Length > 0)
        {
            var text = _characterBatch.ToString();
            Debug.WriteLine($"[Vt100Emulator] FlushCharacterBatch: \"{AnsiParser.EscapeString(text)}\" ({text.Length} chars)");
            System.Console.WriteLine($"[Vt100Emulator] FlushCharacterBatch: \"{AnsiParser.EscapeString(text)}\" ({text.Length} chars)");
            _characterBatch.Clear();
            
            foreach (var c in text)
            {
                WriteCharacter(c, suppressScreenChanged: true);
            }
            
            CursorMoved?.Invoke(this, EventArgs.Empty);
            ScreenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void WriteCharacter(char c, bool suppressScreenChanged = false)
    {
        // If we've reached terminal width, handle according to auto-wrap mode
        if (_writeCol >= _cols && _cols > 0)
        {
            if (_autoWrapMode)
            {
                // Auto-wrap enabled: start a new line
                NewLine();
            }
            else
            {
                // Auto-wrap disabled: stay at right margin, overwrite last character
                // Don't create a new line, just write at the last column
                _writeCol = _cols - 1;
            }
        }

        // Ensure we have a current line
        if (_currentLineIndex < 0)
        {
            _currentLineIndex = _lines.Count;
            _lines.Add(new TerminalLine());
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
        }

        _writeCol++;
        
        if (!suppressScreenChanged)
        {
            CursorMoved?.Invoke(this, EventArgs.Empty);
            ScreenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void NewLine()
    {
        // Check if we're within a scrolling region
        bool inScrollRegion = _scrollRegionTop >= 0 && _scrollRegionBottom >= _scrollRegionTop;
        
        if (inScrollRegion)
        {
            // Calculate the screen-relative position (last _rows lines are the "screen")
            int screenStartIndex = Math.Max(0, _lines.Count - _rows);
            int screenRelativeCursorRow = _currentLineIndex - screenStartIndex;
            
            // Check if cursor is at or beyond the bottom of the scrolling region (in screen coordinates)
            if (screenRelativeCursorRow >= _scrollRegionBottom)
            {
                // We're at or past the bottom of the scrolling region
                // Scroll the region up by removing the top line of the region (in screen coordinates)
                int absoluteTopLineIndex = screenStartIndex + _scrollRegionTop;
                
                if (absoluteTopLineIndex < _lines.Count && absoluteTopLineIndex >= 0)
                {
                    _lines.RemoveAt(absoluteTopLineIndex);
                    // Adjust current line index since we removed a line
                    if (_currentLineIndex > absoluteTopLineIndex)
                    {
                        _currentLineIndex--;
                    }
                    else if (_currentLineIndex == absoluteTopLineIndex)
                    {
                        // Cursor was at the top of the region, stay at the same screen-relative position
                        _currentLineIndex = absoluteTopLineIndex;
                    }
                }
                
                // Create new line at the bottom of the scrolling region (in screen coordinates)
                int newScreenStartIndex = Math.Max(0, _lines.Count - _rows);
                int absoluteBottomLineIndex = newScreenStartIndex + _scrollRegionBottom;
                _currentLineIndex = absoluteBottomLineIndex;
                
                if (_currentLineIndex >= _lines.Count)
                {
                    _lines.Add(new TerminalLine());
                }
                else if (_currentLineIndex >= 0)
                {
                    // Replace the line at the bottom of the scrolling region
                    _lines[_currentLineIndex] = new TerminalLine();
                }
                else
                {
                    // Fallback: just add a new line
                    _currentLineIndex = _lines.Count;
                    _lines.Add(new TerminalLine());
                }
            }
            else
            {
                // We're within the scrolling region but not at the bottom
                // Just move to the next line
                _currentLineIndex++;
                if (_currentLineIndex >= _lines.Count)
                {
                    _lines.Add(new TerminalLine());
                }
                else
                {
                    // Clear the line we're moving to
                    _lines[_currentLineIndex] = new TerminalLine();
                }
            }
        }
        else
        {
            // No scrolling region - normal behavior: just add a new line
            _currentLineIndex = _lines.Count;
            _lines.Add(new TerminalLine());
        }
        
        _writeCol = 0;
        CursorMoved?.Invoke(this, EventArgs.Empty);
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InsertTab()
    {
        var tabStop = 8;
        var nextTab = ((_writeCol / tabStop) + 1) * tabStop;
        // Don't limit by terminal width - lines can be any length
        _writeCol = nextTab;
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    // Process SGR (Select Graphic Rendition) command - ANSI escape sequence for text styles and colors
    // Format: ESC[<parameters>m where parameters are semicolon-separated numbers
    private void ProcessSgr(List<int> parameters)
    {
        if (parameters.Count == 0)
        {
            ResetAttributes();
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
                        var r = parameters[i + 2];
                        var g = parameters[i + 3];
                        var b = parameters[i + 4];
                        _foregroundColor = ConvertRgbTo256Color(r, g, b);
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
                        var r = parameters[i + 2];
                        var g = parameters[i + 3];
                        var b = parameters[i + 4];
                        _backgroundColor = ConvertRgbTo256Color(r, g, b);
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
        // Clear the current screen (alternate buffer starts empty)
        _lines.Clear();
        _currentLineIndex = -1;
        _writeCol = 0;
        
        // Reset attributes to defaults
        ResetAttributes();
        
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RestoreMainScreenState()
    {
        if (_savedMainScreenLines == null)
        {
            // Nothing was saved, just clear the screen
            _lines.Clear();
            _currentLineIndex = -1;
            _writeCol = 0;
            ResetAttributes();
            ScreenChanged?.Invoke(this, EventArgs.Empty);
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
        
        CursorMoved?.Invoke(this, EventArgs.Empty);
        ScreenChanged?.Invoke(this, EventArgs.Empty);
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

