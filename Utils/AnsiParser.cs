using System.Diagnostics;
using System.Text;

namespace TabbySSH.Utils;

public class AnsiParser
{
    private const char ESC = '\x1B';
    private const char BEL = '\x07';
    private const char BS = '\x08';
    private const char HT = '\x09';
    private const char LF = '\x0A';
    private const char VT = '\x0B';
    private const char FF = '\x0C';
    private const char CR = '\x0D';

    private readonly StringBuilder _buffer = new();
    private AnsiState _state = AnsiState.Normal;
    private bool _oscEscSeen = false;

    public event EventHandler<AnsiCommand>? CommandReceived;
    public event EventHandler<char>? CharacterReceived;

    public void ProcessData(string data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        var span = data.AsSpan();
        int i = 0;
        
        while (i < span.Length)
        {
            if (_state == AnsiState.Normal)
            {
                int normalStart = i;
                while (i < span.Length && span[i] != ESC && span[i] >= 32 && span[i] != 127)
                {
                    i++;
                }
                
                if (i > normalStart)
                {
                    var normalText = span.Slice(normalStart, i - normalStart);
                    if (CharacterReceived != null)
                    {
                        foreach (var c in normalText)
                        {
                            CharacterReceived.Invoke(this, c);
                        }
                    }
                }
                
                if (i < span.Length)
        {
                    ProcessCharacter(span[i]);
                    i++;
                }
            }
            else
            {
                ProcessCharacter(span[i]);
                i++;
            }
        }
    }

    public static string EscapeString(string s)
    {
        var sb = new StringBuilder();
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

    private void ProcessCharacter(char c)
    {
        switch (_state)
        {
            case AnsiState.Normal:
                ProcessNormalCharacter(c);
                break;
            case AnsiState.Escape:
                ProcessEscapeCharacter(c);
                break;
            case AnsiState.Csi:
                ProcessCsiCharacter(c);
                break;
            case AnsiState.Osc:
                ProcessOscCharacter(c);
                break;
            case AnsiState.Dcs:
                ProcessDcsCharacter(c);
                break;
            case AnsiState.Nf:
                ProcessNfCharacter(c);
                break;
            case AnsiState.EscapeIntermediate:
                ProcessEscapeIntermediateCharacter(c);
                break;
        }
    }

    private void ProcessNormalCharacter(char c)
    {
        if (c == ESC)
        {
            _state = AnsiState.Escape;
            _buffer.Clear();
        }
        else
        {
            CharacterReceived?.Invoke(this, c);
        }
    }

    private static string GetControlCharName(char c)
    {
        return c switch
        {
            '\r' => "CR",
            '\n' => "LF",
            '\t' => "TAB",
            '\b' => "BS",
            '\x07' => "BEL",
            '\x1B' => "ESC",
            _ => "UNKNOWN"
        };
    }

    private void ProcessEscapeCharacter(char c)
    {
        if (c == '[')
        {
            _state = AnsiState.Csi;
            _buffer.Clear();
        }
        else if (c == ']')
        {
            _state = AnsiState.Osc;
            _oscEscSeen = false;
            _buffer.Clear();
        }
        else if (c == 'P')
        {
            _state = AnsiState.Dcs;
            _buffer.Clear();
        }
        else if (c == '7')
        {
            var command = new AnsiCommand
            {
                Type = AnsiCommandType.SingleChar,
                FinalChar = '7'
            };
            CommandReceived?.Invoke(this, command);
            _state = AnsiState.Normal;
        }
        else if (c == '8')
        {
            var command = new AnsiCommand
            {
                Type = AnsiCommandType.SingleChar,
                FinalChar = '8'
            };
            CommandReceived?.Invoke(this, command);
            _state = AnsiState.Normal;
        }
        else if (c == 'N')
        {
            var command = new AnsiCommand { Type = AnsiCommandType.SingleChar, FinalChar = 'N' };
            CommandReceived?.Invoke(this, command);
            _state = AnsiState.Normal;
        }
        else if (c == 'O')
        {
            var command = new AnsiCommand { Type = AnsiCommandType.SingleChar, FinalChar = 'O' };
            CommandReceived?.Invoke(this, command);
            _state = AnsiState.Normal;
        }
        else if (c == 'X')
        {
            var command = new AnsiCommand { Type = AnsiCommandType.SingleChar, FinalChar = 'X' };
            CommandReceived?.Invoke(this, command);
            _state = AnsiState.Normal;
        }
        else if (c == '^')
        {
            var command = new AnsiCommand { Type = AnsiCommandType.SingleChar, FinalChar = '^' };
            CommandReceived?.Invoke(this, command);
            _state = AnsiState.Normal;
        }
        else if (c == '_')
        {
            var command = new AnsiCommand { Type = AnsiCommandType.SingleChar, FinalChar = '_' };
            CommandReceived?.Invoke(this, command);
            _state = AnsiState.Normal;
        }
        else if (c == '#')
        {
            _state = AnsiState.Nf;
            _buffer.Clear();
        }
        else if (c == 's' || c == 'u')
        {
            _state = AnsiState.Normal;
        }
        else if (c == '=' || c == '>')
        {
            _state = AnsiState.Normal;
        }
        else if (c >= 0x20 && c <= 0x2F)
        {
            _state = AnsiState.EscapeIntermediate;
            _buffer.Clear();
            _buffer.Append(c);
        }
        else if (c >= 0x40 && c <= 0x5F)
        {
            _state = AnsiState.Normal;
        }
        else
        {
            _state = AnsiState.Normal;
            CharacterReceived?.Invoke(this, ESC);
            CharacterReceived?.Invoke(this, c);
        }
    }

    private void ProcessCsiCharacter(char c)
    {
        if (c >= 0x40 && c <= 0x7E)
        {
            if (IsValidCsiFinalByte(c))
            {
                var paramString = _buffer.ToString();
                var isPrivate = paramString.StartsWith("?");
                var command = new AnsiCommand
                {
                    Type = AnsiCommandType.Csi,
                    Parameters = ParseParameters(paramString),
                    FinalChar = c,
                    IsPrivate = isPrivate
                };
                CommandReceived?.Invoke(this, command);
                _state = AnsiState.Normal;
                _buffer.Clear();
            }
            else
            {
                ResetToNormalAndEmit(c);
            }
        }
        else if (c == 0x07)
        {
            _state = AnsiState.Normal;
            _buffer.Clear();
        }
        else if (c == 0x1B)
        {
            _state = AnsiState.Escape;
            _buffer.Clear();
        }
        else if (c >= 0x20 && c <= 0x2F)
        {
            _buffer.Append(c);
        }
        else if (c >= 0x30 && c <= 0x3F)
        {
            _buffer.Append(c);
        }
        else
        {
            ResetToNormalAndEmit(c);
        }
    }

    private static bool IsValidCsiFinalByte(char c)
    {
        return c switch
        {
            '@' or 'A' or 'B' or 'C' or 'D' or 'E' or 'F' or 'G' or 'H' or 'I' or 'J' or 'K' or 'L' or 'M' or 'P' or 'X' or 'Z' or '`' or 'a' or 'b' or 'c' or 'd' or 'e' or 'f' or 'g' or 'h' or 'i' or 'l' or 'm' or 'n' or 'o' or 'p' or 'q' or 'r' or 's' or 't' or 'u' or 'v' or 'w' or 'x' or 'y' or 'z' => true,
            _ => false
        };
    }

    private void ResetToNormalAndEmit(char c)
    {
        _state = AnsiState.Normal;
        _buffer.Clear();
        CharacterReceived?.Invoke(this, c);
    }

    private void ProcessOscCharacter(char c)
    {
        if (_oscEscSeen)
        {
            if (c == '\\')
            {
                var oscString = _buffer.ToString();
                EmitOscCommand(oscString);
                _state = AnsiState.Normal;
                _oscEscSeen = false;
                _buffer.Clear();
            }
            else
            {
                _buffer.Append('\x1B');
                _oscEscSeen = false;
                ProcessOscCharacter(c);
            }
        }
        else if (c == 0x07)
        {
            var oscString = _buffer.ToString();
            EmitOscCommand(oscString);
            _state = AnsiState.Normal;
            _buffer.Clear();
        }
        else if (c == 0x1B)
        {
            _oscEscSeen = true;
        }
        else if (c >= 0x20 && c <= 0x7E)
        {
            _buffer.Append(c);
        }
        else if (c == '\r' || c == '\n')
        {
            var oscString = _buffer.ToString();
            EmitOscCommand(oscString);
            _state = AnsiState.Normal;
            _buffer.Clear();
        }
        else
        {
            _state = AnsiState.Normal;
            _buffer.Clear();
        }
    }

    private void EmitOscCommand(string oscString)
    {
        if (string.IsNullOrEmpty(oscString))
        {
            return;
        }
        var command = new AnsiCommand
        {
            Type = AnsiCommandType.Osc,
            Parameters = new List<int>(),
            OscString = oscString
        };
        var parts = oscString.Split(';', 2);
        if (parts.Length > 0 && int.TryParse(parts[0], out int oscCode))
        {
            command.Parameters.Add(oscCode);
        }
        else
        {
            command.Parameters.Add(0);
        }
        CommandReceived?.Invoke(this, command);
    }

    private void ProcessDcsCharacter(char c)
    {
        if (c == 0x1B)
        {
            _state = AnsiState.Escape;
        }
        else if (c == '\\')
        {
            _state = AnsiState.Normal;
            _buffer.Clear();
        }
        else if (c >= 0x40 && c <= 0x7E)
        {
            _state = AnsiState.Normal;
            _buffer.Clear();
        }
        else if (c >= 0x20 && c <= 0x3F)
        {
            _buffer.Append(c);
        }
        else
        {
            _state = AnsiState.Normal;
            _buffer.Clear();
        }
    }

    private List<int> ParseParameters(string paramString)
    {
        if (string.IsNullOrEmpty(paramString))
        {
            return new List<int> { 0 };
        }

        var cleanString = paramString;
        if (cleanString.StartsWith("?"))
        {
            cleanString = cleanString.Substring(1);
        }

        var sb = new StringBuilder();
        foreach (var c in cleanString)
        {
            if (c >= 0x30 && c <= 0x3F)
            {
                sb.Append(c);
            }
            else if (c == ';')
            {
                sb.Append(c);
            }
        }

        cleanString = sb.ToString();
        var parts = cleanString.Split(';');
        var parameters = new List<int>();

        foreach (var part in parts)
        {
            if (int.TryParse(part, out int value))
            {
                parameters.Add(value);
            }
            else if (string.IsNullOrEmpty(part))
            {
                parameters.Add(0);
            }
            else
            {
                parameters.Add(0);
            }
        }

        return parameters.Count == 0 ? new List<int> { 0 } : parameters;
    }

    private void ProcessNfCharacter(char c)
    {
        if (c == '3' || c == '4' || c == '5' || c == '6')
        {
            var command = new AnsiCommand
            {
                Type = AnsiCommandType.SingleChar,
                FinalChar = c
            };
            CommandReceived?.Invoke(this, command);
            _state = AnsiState.Normal;
            _buffer.Clear();
        }
        else
        {
            _state = AnsiState.Normal;
            _buffer.Clear();
        }
    }

    private void ProcessEscapeIntermediateCharacter(char c)
    {
        if (c >= 0x30 && c <= 0x7E)
        {
            var command = new AnsiCommand
            {
                Type = AnsiCommandType.SingleChar,
                FinalChar = c
            };
            CommandReceived?.Invoke(this, command);
            _state = AnsiState.Normal;
            _buffer.Clear();
        }
        else if (c == 0x1B)
        {
            _state = AnsiState.Escape;
            _buffer.Clear();
        }
        else if (c >= 0x20 && c <= 0x2F)
        {
            _buffer.Append(c);
        }
        else
        {
            _state = AnsiState.Normal;
            _buffer.Clear();
        }
    }
}

public enum AnsiState
{
    Normal,
    Escape,
    Csi,
    Osc,
    Dcs,
    Nf,
    EscapeIntermediate
}

public class AnsiCommand
{
    public AnsiCommandType Type { get; set; }
    public List<int> Parameters { get; set; } = new();
    public char FinalChar { get; set; }
    public bool IsPrivate { get; set; }
    public string? OscString { get; set; }
}

public enum AnsiCommandType
{
    Csi,
    Osc,
    Dcs,
    SingleChar
}

