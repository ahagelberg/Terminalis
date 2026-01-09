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

    public int Rows => _rows;
    public int Cols => _cols;
    public int LineCount => _lines.Count;
    public bool BracketedPasteMode => _bracketedPasteMode;
    
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
        else
        {
            // Warn about unhandled ANSI commands
            string commandDesc = command.Type switch
            {
                AnsiCommandType.SingleChar => $"SingleChar escape: ESC{command.FinalChar}",
                AnsiCommandType.Dcs => "DCS (Device Control String)",
                _ => $"Unknown command type: {command.Type}"
            };
            Debug.WriteLine($"[Vt100Emulator] Unhandled ANSI command: {commandDesc}");
            System.Console.WriteLine($"[Vt100Emulator] Unhandled ANSI command: {commandDesc}");
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
                        TitleChanged?.Invoke(this, title);
                    }
                }
                break;
            default:
                // Other OSC commands not implemented yet
                Debug.WriteLine($"[Vt100Emulator] Unhandled OSC command: {oscString} (code: {oscCode})");
                System.Console.WriteLine($"[Vt100Emulator] Unhandled OSC command: {oscString} (code: {oscCode})");
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
            ProcessSgr(p);
        }
        // Process mode change commands (h/l) for private parameters
        else if ((final == 'h' || final == 'l') && command.IsPrivate && p.Count > 0)
        {
            ProcessModeChange(p[0], final == 'h');
        }
        else
        {
            // Warn about unhandled CSI commands
            var paramStr = p.Count > 0 ? string.Join(";", p) : "";
            var isPrivate = command.IsPrivate ? "?" : "";
            var commandStr = $"\x1B[{isPrivate}{paramStr}{final}";
            var escapedStr = AnsiParser.EscapeString(commandStr);
            Debug.WriteLine($"[Vt100Emulator] Unhandled CSI command: {escapedStr} (final char: '{final}' (0x{(int)final:X2}), params: [{paramStr}])");
            System.Console.WriteLine($"[Vt100Emulator] Unhandled CSI command: {escapedStr} (final char: '{final}' (0x{(int)final:X2}), params: [{paramStr}])");
        }
    }

    private void ProcessModeChange(int mode, bool set)
    {
        switch (mode)
        {
            case 2004:
                // Bracketed paste mode
                // When enabled (set=true), pasted text should be wrapped with \x1B[200~ and \x1B[201~
                // When disabled (set=false), pasted text is sent as-is
                _bracketedPasteMode = set;
                Debug.WriteLine($"[Vt100Emulator] Bracketed paste mode: {(set ? "enabled" : "disabled")}");
                break;
            default:
                // Other mode changes not implemented yet
                Debug.WriteLine($"[Vt100Emulator] Unhandled mode change: mode={mode}, set={set}");
                System.Console.WriteLine($"[Vt100Emulator] Unhandled mode change: mode={mode}, set={set}");
                break;
        }
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
                    Debug.WriteLine($"[Vt100Emulator] Unhandled control character: {charName} (0x{charCode:X2}, '\\u{charCode:X4}')");
                    System.Console.WriteLine($"[Vt100Emulator] Unhandled control character: {charName} (0x{charCode:X2}, '\\u{charCode:X4}')");
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
            
            ScreenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void WriteCharacter(char c, bool suppressScreenChanged = false)
    {
        // If we've reached terminal width, start a new line
        if (_writeCol >= _cols && _cols > 0)
        {
            NewLine();
        }

        // Ensure we have a current line
        if (_currentLineIndex < 0)
        {
            _currentLineIndex = _lines.Count;
            _lines.Add(new TerminalLine());
        }

        var line = _lines[_currentLineIndex];
        
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

        _writeCol++;
        
        if (!suppressScreenChanged)
        {
            CursorMoved?.Invoke(this, EventArgs.Empty);
            ScreenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void NewLine()
    {
        // Start a new line
        _currentLineIndex = _lines.Count;
        _lines.Add(new TerminalLine());
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

