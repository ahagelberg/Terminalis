using System.Collections.Generic;
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

public class Vt100Emulator
{
    private const int DEFAULT_SCROLLBACK_LINES = 20000;
    private const int MAX_SCROLLBACK_LINES = 1000000;
    private const int DEFAULT_COLS = 80;
    private const int DEFAULT_ROWS = 24;

    private readonly List<List<TerminalCell>> _screen = new();
    private readonly List<List<TerminalCell>> _scrollback = new();
    private readonly AnsiParser _ansiParser = new();
    private int _cursorRow = 0;
    private int _cursorCol = 0;
    private int _rows = DEFAULT_ROWS;
    private int _cols = DEFAULT_COLS;
    private int _scrollbackLimit = DEFAULT_SCROLLBACK_LINES;
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
    private int _savedCursorRow = 0;
    private int _savedCursorCol = 0;
    private int _savedForegroundColor = 7;
    private int _savedBackgroundColor = 0;
    private bool _savedBold = false;
    private bool _savedFaint = false;
    private bool _savedItalic = false;
    private bool _savedUnderline = false;
    private bool _savedBlink = false;
    private bool _savedReverse = false;
    private bool _savedConceal = false;
    private bool _savedCrossedOut = false;
    private bool _savedDoubleUnderline = false;
    private bool _savedOverline = false;

    public int Rows => _rows;
    public int Cols => _cols;
    public int CursorRow => _cursorRow;
    public int CursorCol => _cursorCol;
    public int ScrollbackLineCount => _scrollback.Count;

    public event EventHandler? ScreenChanged;
    public event EventHandler? CursorMoved;
    public event EventHandler? Bell;
    public event EventHandler<string>? TitleChanged;

    public Vt100Emulator()
    {
        InitializeScreen();
        _ansiParser.CommandReceived += OnAnsiCommand;
        _ansiParser.CharacterReceived += OnCharacter;
    }

    public void SetSize(int cols, int rows)
    {
        if (cols == _cols && rows == _rows)
        {
            return;
        }

        var oldCols = _cols;
        var oldRows = _rows;
        _cols = cols;
        _rows = rows;

        var oldScreen = _screen.ToList();
        _screen.Clear();
        
        for (int i = 0; i < _rows; i++)
        {
            var row = new List<TerminalCell>();
            for (int j = 0; j < _cols; j++)
            {
                if (i < oldRows && j < oldCols)
                {
                    var oldCell = oldScreen[i][j];
                    row.Add(new TerminalCell
                    {
                        Character = oldCell.Character,
                        ForegroundColor = oldCell.ForegroundColor,
                        BackgroundColor = oldCell.BackgroundColor,
                        Bold = oldCell.Bold,
                        Faint = oldCell.Faint,
                        Italic = oldCell.Italic,
                        Underline = oldCell.Underline,
                        Blink = oldCell.Blink,
                        Reverse = oldCell.Reverse,
                        Conceal = oldCell.Conceal,
                        CrossedOut = oldCell.CrossedOut,
                        DoubleUnderline = oldCell.DoubleUnderline,
                        Overline = oldCell.Overline
                    });
                }
                else
                {
                    row.Add(new TerminalCell());
                }
            }
            _screen.Add(row);
        }

        _scrollTop = 0;
        _scrollBottom = -1;
        EnsureCursorInBounds();
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetScrollbackLimit(int lines)
    {
        _scrollbackLimit = Math.Min(Math.Max(0, lines), MAX_SCROLLBACK_LINES);
        TrimScrollback();
    }

    public void ProcessData(string data)
    {
        _ansiParser.ProcessData(data);
    }

    public TerminalCell GetCell(int row, int col)
    {
        if (row < 0 || row >= _screen.Count || col < 0 || col >= _cols)
        {
            return new TerminalCell();
        }

        EnsureRowHasCells(row);
        if (col >= _screen[row].Count)
        {
            return new TerminalCell();
        }

        return _screen[row][col];
    }

    public TerminalCell GetScrollbackCell(int row, int col)
    {
        if (row < 0 || row >= _scrollback.Count || col < 0 || col >= _cols)
        {
            return new TerminalCell();
        }

        var line = _scrollback[row];
        if (col >= line.Count)
        {
            return new TerminalCell();
        }

        return line[col];
    }

    public string GetLine(int row)
    {
        if (row < 0 || row >= _screen.Count)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var cell in _screen[row])
        {
            sb.Append(cell.Character);
        }
        return sb.ToString().TrimEnd();
    }

    private void InitializeScreen()
    {
        _screen.Clear();
        for (int i = 0; i < _rows; i++)
        {
            var row = new List<TerminalCell>();
            for (int j = 0; j < _cols; j++)
            {
                row.Add(new TerminalCell());
            }
            _screen.Add(row);
        }
    }

    private void OnAnsiCommand(object? sender, AnsiCommand command)
    {
        if (command.Type == AnsiCommandType.Csi)
        {
            ProcessCsiCommand(command);
        }
        else if (command.Type == AnsiCommandType.SingleChar)
        {
            ProcessSingleCharCommand(command);
        }
        else if (command.Type == AnsiCommandType.Osc)
        {
            ProcessOscCommand(command);
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
                if (oscString.Contains(';'))
                {
                    var parts = oscString.Split(';', 2);
                    if (parts.Length > 1)
                    {
                        string title = parts[1];
                        TitleChanged?.Invoke(this, title);
                    }
                }
                break;
        }
    }

    private void ProcessSingleCharCommand(AnsiCommand command)
    {
        switch (command.FinalChar)
        {
            case '7':
                SaveCursorPosition();
                break;
            case '8':
                RestoreCursorPosition();
                break;
            case 'N':
            case 'O':
            case 'X':
            case '^':
            case '_':
                break;
            case '3':
            case '4':
            case '5':
            case '6':
                break;
        }
    }

    private void ProcessCsiCommand(AnsiCommand command)
    {
        var p = command.Parameters;
        var final = command.FinalChar;

        switch (final)
        {
            case 'A':
                MoveCursorUp(GetCursorMoveAmount(p));
                break;
            case 'B':
                MoveCursorDown(GetCursorMoveAmount(p));
                break;
            case 'C':
                MoveCursorRight(GetCursorMoveAmount(p));
                break;
            case 'D':
                MoveCursorLeft(GetCursorMoveAmount(p));
                break;
            case 'E':
                MoveCursorNextLine(GetCursorMoveAmount(p));
                break;
            case 'F':
                MoveCursorPreviousLine(GetCursorMoveAmount(p));
                break;
            case 'G':
                var colPos = p.Count > 0 && p[0] > 0 ? p[0] - 1 : 0;
                MoveCursorToColumn(colPos);
                break;
            case 'H':
            case 'f':
                var targetRow = p.Count > 0 && p[0] > 0 ? p[0] - 1 : 0;
                var targetCol = p.Count > 1 && p[1] > 0 ? p[1] - 1 : 0;
                if (targetRow >= _rows)
                {
                    targetRow = _rows - 1;
                }
                MoveCursor(targetRow, targetCol);
                break;
            case 'J':
                ClearScreen(p.Count > 0 ? p[0] : 0);
                break;
            case 'K':
                var clearMode = p.Count > 0 ? p[0] : 0;
                ClearLine(clearMode);
                CursorMoved?.Invoke(this, EventArgs.Empty);
                break;
            case 'S':
                ScrollUp(GetCursorMoveAmount(p));
                break;
            case 'T':
                ScrollDown(GetCursorMoveAmount(p));
                break;
            case 'L':
                InsertLines(GetCursorMoveAmount(p));
                break;
            case 'M':
                DeleteLines(GetCursorMoveAmount(p));
                break;
            case 'm':
                ProcessSgr(p);
                break;
            case 't':
                // Window manipulation (DECSLPP / xterm window ops) – not supported; ignore.
                break;
            case 'n':
                if (p.Count > 0 && p[0] == 6)
                {
                    ReportCursorPosition();
                }
                break;
            case 'c':
                // Device Attributes (DA) query - ignore silently
                break;
            case 's':
                SaveCursorPosition();
                break;
            case 'u':
                RestoreCursorPosition();
                break;
            case 'h':
            case 'l':
                ProcessModeChange(command, final == 'h');
                break;
            case 'd':
                var rowPos = p.Count > 0 && p[0] > 0 ? p[0] - 1 : 0;
                MoveCursorToRow(rowPos);
                break;
            case 'r':
                if (p.Count >= 2)
                {
                    var top = p[0] > 0 ? p[0] - 1 : 0;
                    var bottom = p[1] > 0 ? p[1] - 1 : _rows - 1;
                    SetScrollingRegion(top, bottom);
                }
                else if (p.Count == 1)
                {
                    var top = p[0] > 0 ? p[0] - 1 : 0;
                    SetScrollingRegion(top, _rows - 1);
                }
                else
                {
                    SetScrollingRegion(0, _rows - 1);
                }
                break;
            case 'X':
                EraseCharacters(GetCursorMoveAmount(p));
                break;
            case 'P':
                DeleteCharacters(GetCursorMoveAmount(p));
                break;
            case '@':
                InsertCharacters(GetCursorMoveAmount(p));
                break;
            default:
                var paramStr = p.Count > 0 ? string.Join(";", p) : "";
                var commandStr = $"\x1B[{paramStr}{final}";
                var escapedStr = AnsiParser.EscapeString(commandStr);
                System.Diagnostics.Debug.WriteLine($"[Vt100Emulator] Unhandled CSI command: {escapedStr}");
                System.Console.WriteLine($"[Vt100Emulator] Unhandled CSI command: {escapedStr}");
                break;
        }
    }

    private void OnCharacter(object? sender, char c)
    {
        switch (c)
        {
            case '\r':
                _cursorCol = 0;
                CursorMoved?.Invoke(this, EventArgs.Empty);
                break;
            case '\n':
                if (_cursorCol != 0)
                {
                    _cursorCol = 0;
                }
                NewLine();
                break;
            case '\t':
                InsertTab();
                break;
            case '\b':
                if (_cursorCol > 0)
                {
                    _cursorCol--;
                    CursorMoved?.Invoke(this, EventArgs.Empty);
                }
                break;
            case '\x07':
                Bell?.Invoke(this, EventArgs.Empty);
                break;
            default:
                if (c >= 32 && c < 127)
                {
                    WriteCharacter(c);
                }
                break;
        }
    }

    private void WriteCharacter(char c)
    {
        if (_cursorCol >= _cols)
        {
            _cursorCol = 0;
            NewLine();
        }

        if (_cursorRow >= 0 && _cursorRow < _rows && _cursorCol >= 0 && _cursorCol < _cols)
        {
            EnsureRowHasCells(_cursorRow);
            var cell = _screen[_cursorRow][_cursorCol];
            cell.Character = c;
            cell.ForegroundColor = _reverse ? _backgroundColor : _foregroundColor;
            cell.BackgroundColor = _reverse ? _foregroundColor : _backgroundColor;
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

            _cursorCol++;
            EnsureCursorInBounds();
            ScreenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EnsureRowHasCells(int row)
    {
        if (row < 0 || row >= _screen.Count)
        {
            return;
        }

        var line = _screen[row];
        while (line.Count < _cols)
        {
            line.Add(new TerminalCell());
        }
    }

    private void NewLine()
    {
        int scrollTop = _scrollTop;
        int scrollBottom = _scrollBottom >= 0 ? _scrollBottom : _rows - 1;
        
        if (_cursorRow >= scrollTop && _cursorRow < scrollBottom)
        {
            if (_cursorRow < scrollBottom)
            {
                _cursorRow++;
            }
            else
            {
                ScrollUpInRegion();
            }
        }
        else if (_cursorRow == scrollBottom)
        {
            ScrollUpInRegion();
        }
        else
        {
            if (_cursorRow < _rows - 1)
            {
                _cursorRow++;
            }
            else
            {
                ScrollUp();
            }
        }
        _cursorCol = 0;
        CursorMoved?.Invoke(this, EventArgs.Empty);
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ScrollUp()
    {
        int scrollTop = _scrollTop;
        int scrollBottom = _scrollBottom >= 0 ? _scrollBottom : _rows - 1;
        
        if (scrollTop == 0 && scrollBottom == _rows - 1)
        {
            var topLine = CopyLine(_screen[scrollTop]);
            _scrollback.Add(topLine);
            TrimScrollback();

            for (int i = scrollTop; i < scrollBottom; i++)
            {
                _screen[i] = CopyLine(_screen[i + 1]);
            }

            _screen[scrollBottom] = new List<TerminalCell>();
            for (int j = 0; j < _cols; j++)
            {
                _screen[scrollBottom].Add(new TerminalCell());
            }
        }
        else
        {
            ScrollUpInRegion();
        }

        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ScrollUpInRegion()
    {
        int scrollTop = _scrollTop;
        int scrollBottom = _scrollBottom >= 0 ? _scrollBottom : _rows - 1;
        
        if (scrollTop == 0)
        {
            var topLine = CopyLine(_screen[scrollTop]);
            _scrollback.Add(topLine);
            TrimScrollback();
        }

        for (int i = scrollTop; i < scrollBottom; i++)
        {
            _screen[i] = CopyLine(_screen[i + 1]);
        }

        _screen[scrollBottom] = new List<TerminalCell>();
        for (int j = 0; j < _cols; j++)
        {
            _screen[scrollBottom].Add(new TerminalCell());
        }
    }

    private List<TerminalCell> CopyLine(List<TerminalCell> source)
    {
        var copy = new List<TerminalCell>(_cols);
        int sourceCount = Math.Min(source.Count, _cols);
        
        for (int i = 0; i < sourceCount; i++)
        {
            var cell = source[i];
            copy.Add(new TerminalCell
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
        
        for (int i = sourceCount; i < _cols; i++)
        {
            copy.Add(new TerminalCell());
        }
        
        return copy;
    }

    private void TrimScrollback()
    {
        while (_scrollback.Count > _scrollbackLimit)
        {
            _scrollback.RemoveAt(0);
        }
    }

    private void MoveCursor(int row, int col)
    {
        _cursorRow = Math.Max(0, Math.Min(row, _rows - 1));
        _cursorCol = Math.Max(0, Math.Min(col, _cols - 1));
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    private void MoveCursorUp(int n)
    {
        int scrollTop = _scrollTop;
        _cursorRow = Math.Max(scrollTop, _cursorRow - n);
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    private void MoveCursorDown(int n)
    {
        int scrollBottom = _scrollBottom >= 0 ? _scrollBottom : _rows - 1;
        _cursorRow = Math.Min(scrollBottom, _cursorRow + n);
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    private void MoveCursorLeft(int n)
    {
        _cursorCol = Math.Max(0, _cursorCol - n);
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    private void MoveCursorRight(int n)
    {
        var oldCol = _cursorCol;
        _cursorCol = Math.Min(_cols - 1, _cursorCol + n);
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    private void MoveCursorToColumn(int col)
    {
        _cursorCol = Math.Max(0, Math.Min(col, _cols - 1));
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    private void MoveCursorToRow(int row)
    {
        _cursorRow = Math.Max(0, Math.Min(row, _rows - 1));
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    private void MoveCursorNextLine(int n)
    {
        _cursorCol = 0;
        MoveCursorDown(n);
    }

    private void MoveCursorPreviousLine(int n)
    {
        _cursorCol = 0;
        MoveCursorUp(n);
    }

    private void ScrollUp(int n)
    {
        for (int i = 0; i < n; i++)
        {
            ScrollUp();
        }
    }

    private void SetScrollingRegion(int top, int bottom)
    {
        _scrollTop = Math.Max(0, Math.Min(top, _rows - 1));
        _scrollBottom = Math.Max(_scrollTop, Math.Min(bottom, _rows - 1));
        _cursorRow = _scrollTop;
        _cursorCol = 0;
        CursorMoved?.Invoke(this, EventArgs.Empty);
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ScrollDown(int n)
    {
        int scrollTop = _scrollTop;
        int scrollBottom = _scrollBottom >= 0 ? _scrollBottom : _rows - 1;
        
        for (int i = 0; i < n; i++)
        {
            if (scrollTop == 0 && scrollBottom == _rows - 1)
            {
                var bottomLine = CopyLine(_screen[scrollBottom]);
                _scrollback.Insert(0, bottomLine);
                TrimScrollback();
            }

            for (int j = scrollBottom; j > scrollTop; j--)
            {
                _screen[j] = CopyLine(_screen[j - 1]);
            }

            _screen[scrollTop] = new List<TerminalCell>();
            for (int k = 0; k < _cols; k++)
            {
                _screen[scrollTop].Add(new TerminalCell());
            }
        }
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveCursorPosition()
    {
        _savedCursorRow = _cursorRow;
        _savedCursorCol = _cursorCol;
        _savedForegroundColor = _foregroundColor;
        _savedBackgroundColor = _backgroundColor;
        _savedBold = _bold;
        _savedFaint = _faint;
        _savedItalic = _italic;
        _savedUnderline = _underline;
        _savedBlink = _blink;
        _savedReverse = _reverse;
        _savedConceal = _conceal;
        _savedCrossedOut = _crossedOut;
        _savedDoubleUnderline = _doubleUnderline;
        _savedOverline = _overline;
    }

    private void RestoreCursorPosition()
    {
        _cursorRow = _savedCursorRow;
        _cursorCol = _savedCursorCol;
        _foregroundColor = _savedForegroundColor;
        _backgroundColor = _savedBackgroundColor;
        _bold = _savedBold;
        _faint = _savedFaint;
        _italic = _savedItalic;
        _underline = _savedUnderline;
        _blink = _savedBlink;
        _reverse = _savedReverse;
        _conceal = _savedConceal;
        _crossedOut = _savedCrossedOut;
        _doubleUnderline = _savedDoubleUnderline;
        _overline = _savedOverline;
        EnsureCursorInBounds();
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    private void ReportCursorPosition()
    {
        var response = $"\x1B[{_cursorRow + 1};{_cursorCol + 1}R";
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool _cursorVisible = true;
    private bool _alternativeScreen = false;
    private bool _bracketedPaste = false;
    private int _scrollTop = 0;
    private int _scrollBottom = -1;

    private void ProcessModeChange(AnsiCommand command, bool set)
    {
        var parameters = command.Parameters;
        if (parameters.Count == 0) return;

        var param = parameters[0];
        var isPrivate = command.IsPrivate;
        
        if (isPrivate)
        {
            if (param == 25)
            {
                _cursorVisible = set;
            }
            else if (param == 12)
            {
            }
            else if (param == 1049)
            {
                _alternativeScreen = set;
            }
            else if (param == 2004)
            {
                _bracketedPaste = set;
            }
        }
    }

    private static int GetCursorMoveAmount(List<int> parameters)
    {
        if (parameters.Count == 0)
        {
            return 1;
        }
        var value = parameters[0];
        return value == 0 ? 1 : value;
    }

    private void EraseCharacters(int count)
    {
        var endCol = Math.Min(_cursorCol + count, _cols);
        for (int i = _cursorCol; i < endCol; i++)
        {
            var cell = _screen[_cursorRow][i];
            cell.Character = ' ';
            cell.ForegroundColor = 7;
            cell.BackgroundColor = 0;
            cell.Bold = false;
            cell.Underline = false;
            cell.Reverse = false;
        }
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteCharacters(int count)
    {
        var endCol = Math.Min(_cursorCol + count, _cols);
        for (int i = _cursorCol; i < _cols - count; i++)
        {
            if (i + count < _cols)
            {
                var sourceCell = _screen[_cursorRow][i + count];
                var targetCell = _screen[_cursorRow][i];
                targetCell.Character = sourceCell.Character;
                targetCell.ForegroundColor = sourceCell.ForegroundColor;
                targetCell.BackgroundColor = sourceCell.BackgroundColor;
                targetCell.Bold = sourceCell.Bold;
                targetCell.Underline = sourceCell.Underline;
                targetCell.Reverse = sourceCell.Reverse;
            }
            else
            {
                var cell = _screen[_cursorRow][i];
                cell.Character = ' ';
                cell.ForegroundColor = 7;
                cell.BackgroundColor = 0;
                cell.Bold = false;
                cell.Underline = false;
                cell.Reverse = false;
            }
        }
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InsertCharacters(int count)
    {
        for (int i = _cols - 1; i >= _cursorCol + count; i--)
        {
            if (i - count >= _cursorCol)
            {
                var sourceCell = _screen[_cursorRow][i - count];
                var targetCell = _screen[_cursorRow][i];
                targetCell.Character = sourceCell.Character;
                targetCell.ForegroundColor = sourceCell.ForegroundColor;
                targetCell.BackgroundColor = sourceCell.BackgroundColor;
                targetCell.Bold = sourceCell.Bold;
                targetCell.Underline = sourceCell.Underline;
                targetCell.Reverse = sourceCell.Reverse;
            }
        }
        for (int i = _cursorCol; i < _cursorCol + count && i < _cols; i++)
        {
            var cell = _screen[_cursorRow][i];
            cell.Character = ' ';
            cell.ForegroundColor = 7;
            cell.BackgroundColor = 0;
            cell.Bold = false;
            cell.Underline = false;
            cell.Reverse = false;
        }
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearScreen(int mode)
    {
        switch (mode)
        {
            case 0:
                for (int i = _cursorRow; i < _rows; i++)
                {
                    if (i == _cursorRow)
                {
                    ClearLine(i, 0);
                    }
                    else
                    {
                        ClearLine(i, 2);
                    }
                }
                break;
            case 1:
                for (int i = 0; i <= _cursorRow; i++)
                {
                    if (i == _cursorRow)
                    {
                        ClearLine(i, 1);
                    }
                    else
                    {
                        ClearLine(i, 2);
                    }
                }
                break;
            case 2:
                InitializeScreen();
                _cursorRow = 0;
                _cursorCol = 0;
                break;
        }
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearLine(int mode)
    {
        ClearLine(_cursorRow, mode);
    }

    private void ClearLine(int row, int mode)
    {
        if (row < 0 || row >= _rows)
        {
            return;
        }

        switch (mode)
        {
            case 0:
                for (int i = _cursorCol; i < _cols; i++)
                {
                    var cell = _screen[row][i];
                    cell.Character = ' ';
                    cell.ForegroundColor = 7;
                    cell.BackgroundColor = 0;
                    cell.Bold = false;
                    cell.Underline = false;
                    cell.Reverse = false;
                }
                ScreenChanged?.Invoke(this, EventArgs.Empty);
                break;
            case 1:
                for (int i = 0; i <= _cursorCol; i++)
                {
                    var cell = _screen[row][i];
                    cell.Character = ' ';
                    cell.ForegroundColor = 7;
                    cell.BackgroundColor = 0;
                    cell.Bold = false;
                    cell.Underline = false;
                    cell.Reverse = false;
                }
                ScreenChanged?.Invoke(this, EventArgs.Empty);
                break;
            case 2:
                for (int i = 0; i < _cols; i++)
                {
                    var cell = _screen[row][i];
                    cell.Character = ' ';
                    cell.ForegroundColor = 7;
                    cell.BackgroundColor = 0;
                    cell.Bold = false;
                    cell.Underline = false;
                    cell.Reverse = false;
                }
                ScreenChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void InsertTab()
    {
        var tabStop = 8;
        var nextTab = ((_cursorCol / tabStop) + 1) * tabStop;
        _cursorCol = Math.Min(nextTab, _cols - 1);
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

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
            
            if (param == 0)
            {
                ResetAttributes();
                i++;
            }
            else if (param == 1)
            {
                _bold = true;
                _faint = false;
                i++;
            }
            else if (param == 2)
            {
                _faint = true;
                _bold = false;
                i++;
            }
            else if (param == 3)
            {
                _italic = true;
                i++;
            }
            else if (param == 4)
            {
                _underline = true;
                _doubleUnderline = false;
                i++;
            }
            else if (param == 5)
            {
                _blink = true;
                i++;
            }
            else if (param == 6)
            {
                _blink = true;
                i++;
            }
            else if (param == 7)
            {
                _reverse = true;
                i++;
            }
            else if (param == 8)
            {
                _conceal = true;
                i++;
            }
            else if (param == 9)
            {
                _crossedOut = true;
                i++;
            }
            else if (param >= 10 && param <= 19)
            {
                i++;
            }
            else if (param == 20)
            {
                i++;
            }
            else if (param == 21)
            {
                _bold = false;
                _doubleUnderline = false;
                i++;
            }
            else if (param == 22)
            {
                _bold = false;
                _faint = false;
                i++;
            }
            else if (param == 23)
            {
                _italic = false;
                i++;
            }
            else if (param == 24)
            {
                _underline = false;
                _doubleUnderline = false;
                i++;
            }
            else if (param == 25)
            {
                _blink = false;
                i++;
            }
            else if (param == 27)
            {
                _reverse = false;
                i++;
            }
            else if (param == 28)
            {
                _conceal = false;
                i++;
            }
            else if (param == 29)
            {
                _crossedOut = false;
                i++;
            }
            else if (param >= 30 && param <= 37)
            {
                _foregroundColor = param - 30;
                i++;
            }
            else if (param == 38)
            {
                if (i + 1 < parameters.Count)
                {
                    var colorType = parameters[i + 1];
                    if (colorType == 5 && i + 2 < parameters.Count)
                    {
                        var colorIndex = parameters[i + 2];
                        _foregroundColor = colorIndex;
                        i += 3;
                    }
                    else if (colorType == 2 && i + 4 < parameters.Count)
                    {
                        var r = parameters[i + 2];
                        var g = parameters[i + 3];
                        var b = parameters[i + 4];
                        _foregroundColor = ConvertRgbTo256Color(r, g, b);
                        i += 5;
                    }
                    else
                    {
                        i++;
                    }
                }
                else
                {
                    i++;
                }
            }
            else if (param == 39)
            {
                _foregroundColor = 7;
                i++;
            }
            else if (param >= 40 && param <= 47)
            {
                _backgroundColor = param - 40;
                i++;
            }
            else if (param == 48)
            {
                if (i + 1 < parameters.Count)
                {
                    var colorType = parameters[i + 1];
                    if (colorType == 5 && i + 2 < parameters.Count)
                    {
                        var colorIndex = parameters[i + 2];
                        _backgroundColor = colorIndex;
                        i += 3;
                    }
                    else if (colorType == 2 && i + 4 < parameters.Count)
                    {
                        var r = parameters[i + 2];
                        var g = parameters[i + 3];
                        var b = parameters[i + 4];
                        _backgroundColor = ConvertRgbTo256Color(r, g, b);
                        i += 5;
                    }
                    else
                    {
                        i++;
                    }
                }
                else
                {
                    i++;
                }
            }
            else if (param == 49)
            {
                _backgroundColor = 0;
                i++;
            }
            else if (param >= 50 && param <= 59)
            {
                i++;
            }
            else if (param >= 60 && param <= 65)
            {
                i++;
            }
            else if (param >= 73 && param <= 75)
            {
                i++;
            }
            else if (param >= 90 && param <= 97)
            {
                _foregroundColor = param - 90 + 8;
                i++;
            }
            else if (param >= 100 && param <= 107)
            {
                _backgroundColor = param - 100 + 8;
                i++;
            }
            else
            {
                i++;
            }
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

    private void EnsureCursorInBounds()
    {
        int scrollTop = _scrollTop;
        int scrollBottom = _scrollBottom >= 0 ? _scrollBottom : _rows - 1;
        _cursorRow = Math.Max(scrollTop, Math.Min(_cursorRow, scrollBottom));
        _cursorCol = Math.Max(0, Math.Min(_cursorCol, _cols - 1));
    }

    private void InsertLines(int count)
    {
        int scrollTop = _scrollTop;
        int scrollBottom = _scrollBottom >= 0 ? _scrollBottom : _rows - 1;
        
        for (int i = 0; i < count; i++)
        {
            for (int j = scrollBottom; j > _cursorRow; j--)
            {
                _screen[j] = CopyLine(_screen[j - 1]);
            }

            _screen[_cursorRow] = new List<TerminalCell>();
            for (int k = 0; k < _cols; k++)
            {
                _screen[_cursorRow].Add(new TerminalCell());
            }
        }
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteLines(int count)
    {
        int scrollTop = _scrollTop;
        int scrollBottom = _scrollBottom >= 0 ? _scrollBottom : _rows - 1;
        
        for (int i = 0; i < count; i++)
        {
            for (int j = _cursorRow; j < scrollBottom; j++)
            {
                _screen[j] = CopyLine(_screen[j + 1]);
            }

            _screen[scrollBottom] = new List<TerminalCell>();
            for (int k = 0; k < _cols; k++)
            {
                _screen[scrollBottom].Add(new TerminalCell());
            }
        }
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }
}

