using System.Collections.Generic;
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
    public bool InAlternateScreen => _inAlternateScreen;
    public bool CursorVisible => _cursorVisible;
    
    // Stub properties for UI compatibility
    public int CursorRow => _currentLineIndex >= 0 ? _currentLineIndex : 0;
    public int CursorCol => _writeCol;
    public int ScrollbackLineCount => 0;

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
        // Process data in order as it arrives - no batching, no reordering
        // This ensures commands and text are processed in the exact order they appear
        _ansiParser.ProcessData(data);
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
            // Unhandled ANSI command - ignore
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
                        TitleChanged?.Invoke(this, title);
                    }
                }
                break;
            default:
                // Other OSC commands not implemented yet
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
                ProcessDecModeChange(p[0], final == 'h');
            }
            else
            {
                // Non-private mode changes (ANSI modes) - ESC [ ... h/l
                var paramStr = p.Count > 0 ? string.Join(";", p) : "";
                var commandStr = $"\x1B[{paramStr}{final}";
                ProcessAnsiModeChange(p[0], final == 'h');
            }
        }
        // Process Erase in Display (J) command
        else if (final == 'J')
        {
            ProcessEraseInDisplay(p);
        }
        // Process Erase in Line (K) command
        else if (final == 'K')
        {
            ProcessEraseInLine(p);
        }
        // Process Window Manipulation (t) commands
        else if (final == 't')
        {
            var paramStr = p.Count > 0 ? string.Join(";", p) : "";
            var commandStr = $"\x1B[{paramStr}t";
            var escapedStr = AnsiParser.EscapeString(commandStr);
            ProcessWindowManipulation(p);
        }
        // Process Set Scrolling Region (r) command
        else if (final == 'r')
        {
            var paramStr = p.Count > 0 ? string.Join(";", p) : "";
            var commandStr = $"\x1B[{paramStr}r";
            var escapedStr = AnsiParser.EscapeString(commandStr);
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
            var paramStr = p.Count > 0 ? string.Join(";", p) : "";
            var isPrivate = command.IsPrivate ? "?" : "";
            var commandStr = $"\x1B[{isPrivate}{paramStr}{final}";
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
                break;
            case 12:
                // DECSCLM - Start Blinking Cursor (DEC Smooth Cursor Line Mode)
                // This mode controls cursor blinking behavior
                // We track it but don't need to do anything special - cursor visibility is handled by mode 25
                break;
            case 25:
                // DECTCEM - DEC Text Cursor Enable Mode
                // When enabled (set=true): cursor is visible
                // When disabled (set=false): cursor is hidden
                _cursorVisible = set;
                break;
            case 1049:
                // DECALTB - Alternate Screen Buffer
                if (set)
                {
                    SaveMainScreenState();
                    SwitchToAlternateScreen();
                }
                else
                {
                    RestoreMainScreenState();
                }
                break;
            case 2004:
                // Bracketed paste mode
                // When enabled (set=true), pasted text should be wrapped with \x1B[200~ and \x1B[201~
                // When disabled (set=false), pasted text is sent as-is
                _bracketedPasteMode = set;
                break;
            default:
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
                break;
            default:
                break;
        }
    }

    private void ProcessEraseInDisplay(List<int> parameters)
    {
        int param = parameters.Count > 0 ? parameters[0] : 0;
        
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
        string desc = param == 0 ? "cursor to EOL" : param == 1 ? "BOL to cursor" : param == 2 ? "entire line" : $"param={param}";
        if (_currentLineIndex < 0 || _currentLineIndex >= _lines.Count)
        {
            return;
        }
        var line = _lines[_currentLineIndex];
        if (param == 0)
        {
            EraseFromCursorToEndOfLine(line);
        }
        else if (param == 1)
        {
            EraseFromCursorToBeginningOfLine(line);
        }
        else if (param == 2)
        {
            EraseEntireLine(line);
        }
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
                break;
            case 23:
                // Restore window title (xterm extension)
                // Restore the previously saved title
                if (_savedTitle != null)
                {
                    _currentTitle = _savedTitle;
                    TitleChanged?.Invoke(this, _savedTitle);
                }
                break;
            default:
                break;
        }
    }

    private void ProcessSetScrollingRegion(List<int> parameters)
    {
        if (parameters.Count == 0)
        {
            _scrollRegionTop = -1;
            _scrollRegionBottom = -1;
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
            }
            else
            {
                _scrollRegionTop = -1;
                _scrollRegionBottom = -1;
            }
        }
        else
        {
            _scrollRegionTop = -1;
            _scrollRegionBottom = -1;
        }
    }

    private void ProcessSingleCharCommand(AnsiCommand command)
    {
        char final = command.FinalChar;
        
        switch (final)
        {
            case 'B':
                MoveCursorDown();
                break;
            case '7':
            case '8':
                // Save/Restore cursor position - not yet implemented
                break;
            default:
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
        switch (c)
        {
            case '\r':
                FlushTextBatch();
                _writeCol = 0;
                break;
            case '\n':
                FlushTextBatch();
                _writeCol = 0;
                NewLine();
                break;
            case '\t':
                FlushTextBatch();
                InsertTab();
                break;
            case '\b':
                FlushTextBatch();
                if (_writeCol > 0)
                {
                    _writeCol--;
                }
                break;
            case '\x07':
                FlushTextBatch();
                Bell?.Invoke(this, EventArgs.Empty);
                break;
            default:
                if (!char.IsControl(c) || c == '\t' || c == '\n' || c == '\r')
                {
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
                    FlushTextBatch();
                }
                break;
        }
    }
    
    private void FlushTextBatch()
    {
        _textBatch.Clear();
        _textBatchStartRow = -1;
        _textBatchStartCol = -1;
    }

    private void WriteCharacter(char c, bool suppressScreenChanged = false)
    {
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
        
        
        _writeCol = colIndex;
        
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessCursorUp(List<int> parameters, AnsiCommand command)
    {
        // CUU (Cursor Up) - ESC [ n A
        // Moves cursor up n lines (default 1)
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
        
        if (_currentLineIndex >= 0)
        {
            _currentLineIndex = Math.Max(0, _currentLineIndex - count);
        }
        else
        {
            _currentLineIndex = 0;
        }
        
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessCursorDown(List<int> parameters, AnsiCommand command)
    {
        // CUD (Cursor Down) - ESC [ n B
        // Moves cursor down n lines (default 1)
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
        
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
        
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessCursorForward(List<int> parameters, AnsiCommand command)
    {
        // CUF (Cursor Forward) - ESC [ n C
        // Moves cursor right n columns (default 1)
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
        
        _writeCol += count;
        if (_writeCol >= _cols && _cols > 0)
        {
            _writeCol = _cols - 1;
        }
        
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessCursorBackward(List<int> parameters, AnsiCommand command)
    {
        // CUB (Cursor Backward) - ESC [ n D
        // Moves cursor left n columns (default 1)
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
        
        _writeCol = Math.Max(0, _writeCol - count);
        
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessCursorNextLine(List<int> parameters, AnsiCommand command)
    {
        // CNL (Cursor Next Line) - ESC [ n E
        // Moves cursor to beginning of line n lines down (default 1)
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
        
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
        
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessScrollUp(List<int> parameters, AnsiCommand command)
    {
        // SU (Scroll Up) - ESC [ n S
        // Scrolls the screen up by n lines (default 1)
        // Content moves up: lines from (scrollTop + count) to scrollBottom are copied to scrollTop to (scrollBottom - count)
        // Lines at (scrollBottom - count + 1) to scrollBottom are cleared
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
        
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
        
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessScrollDown(List<int> parameters, AnsiCommand command)
    {
        // SD (Scroll Down) - ESC [ n T
        // Scrolls the screen down by n lines (default 1)
        // Content moves down: lines from scrollTop to (scrollBottom - count) are copied to (scrollTop + count) to scrollBottom
        // Lines at scrollTop to (scrollTop + count - 1) are cleared
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
        
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
        
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessDeleteLine(List<int> parameters, AnsiCommand command)
    {
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
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
        
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessInsertLine(List<int> parameters, AnsiCommand command)
    {
        // IL (Insert Line) - ESC [ Ps L
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        if (_inAlternateScreen)
        {
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
        
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessDeleteCharacter(List<int> parameters, AnsiCommand command)
    {
        // DCH (Delete Character) - ESC [ Ps P
        // Deletes Ps characters at the cursor position (default 1)
        // Characters to the right shift left
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
        
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
        
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessInsertCharacter(List<int> parameters, AnsiCommand command)
    {
        // ICH (Insert Character) - ESC [ Ps @
        // Inserts Ps blank characters at the cursor position (default 1)
        // Existing characters shift right
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
        
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
        
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    private void ProcessCursorPreviousLine(List<int> parameters, AnsiCommand command)
    {
        // CPL (Cursor Previous Line) - ESC [ n F
        // Moves cursor to beginning of line n lines up (default 1)
        int count = parameters.Count > 0 && parameters[0] > 0 ? parameters[0] : 1;
        
        
        if (_currentLineIndex >= 0)
        {
            _currentLineIndex = Math.Max(0, _currentLineIndex - count);
        }
        else
        {
            _currentLineIndex = 0;
        }
        
        _writeCol = 0;
        
        
        // Don't fire events - ScreenChanged will be fired at end of ProcessData
    }

    // Process SGR (Select Graphic Rendition) command - ANSI escape sequence for text styles and colors
    // Format: ESC[<parameters>m where parameters are semicolon-separated numbers
    private void ProcessSgr(List<int> parameters, AnsiCommand? command = null)
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

