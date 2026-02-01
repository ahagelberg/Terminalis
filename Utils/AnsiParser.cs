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

        // Process character by character in strict order - no batching, no reordering
        // This ensures commands and text are processed in the exact order they appear
        var span = data.AsSpan();
        int i = 0;
        
        // Optimize: cache event handler to avoid null check on every character
        var characterHandler = CharacterReceived;
        
        while (i < span.Length)
        {
            char c = span[i];
            
            if (_state == AnsiState.Normal)
            {
                // In normal state, emit printable characters immediately
                // Don't batch them - process each one as it's encountered
                if (c == ESC)
                {
                    // Start of escape sequence - process it
                    ProcessCharacter(c);
                }
                else if (characterHandler != null)
                {
                    // Printable or control character - emit immediately
                    characterHandler.Invoke(this, c);
                }
            }
            else
            {
                // In escape sequence state - process character to continue/complete sequence
                ProcessCharacter(c);
            }
            
            i++;
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
                FinalChar = '7',
                RawText = "\x1B7"
            };
            CommandReceived?.Invoke(this, command);
            _state = AnsiState.Normal;
        }
        else if (c == '8')
        {
            var command = new AnsiCommand
            {
                Type = AnsiCommandType.SingleChar,
                FinalChar = '8',
                RawText = "\x1B8"
            };
            CommandReceived?.Invoke(this, command);
            _state = AnsiState.Normal;
        }
        else if (c == 'N')
        {
            var command = new AnsiCommand { Type = AnsiCommandType.SingleChar, FinalChar = 'N', RawText = "\x1BN" };
            CommandReceived?.Invoke(this, command);
            _state = AnsiState.Normal;
        }
        else if (c == 'O')
        {
            var command = new AnsiCommand { Type = AnsiCommandType.SingleChar, FinalChar = 'O', RawText = "\x1BO" };
            CommandReceived?.Invoke(this, command);
            _state = AnsiState.Normal;
        }
        else if (c >= 0x20 && c <= 0x2F)
        {
            // Intermediate character - used for character set designation and other commands
            // Examples: ESC ( B (Designate G0 Character Set), ESC ) B (Designate G1 Character Set)
            _state = AnsiState.EscapeIntermediate;
            _buffer.Clear();
            _buffer.Append(c);
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
                var rawText = $"\x1B[{(isPrivate ? "?" : "")}{paramString}{c}";
                var command = new AnsiCommand
                {
                    Type = AnsiCommandType.Csi,
                    Parameters = ParseParameters(paramString),
                    FinalChar = c,
                    IsPrivate = isPrivate,
                    RawText = rawText
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
            // BEL terminator - emit OSC command and consume it
            var oscString = _buffer.ToString();
            EmitOscCommand(oscString);
            _state = AnsiState.Normal;
            _oscEscSeen = false;
            _buffer.Clear();
            // BEL is consumed, not emitted as character
        }
        else if (c == 0x1B)
        {
            _oscEscSeen = true;
        }
        else if (c >= 0x20 && c <= 0x7E)
        {
            // Valid OSC string character - accumulate in buffer
            _buffer.Append(c);
        }
        else if (c == '\r' || c == '\n')
        {
            // CR/LF terminator - emit OSC command and consume it
            var oscString = _buffer.ToString();
            EmitOscCommand(oscString);
            _state = AnsiState.Normal;
            _oscEscSeen = false;
            _buffer.Clear();
            // CR/LF are consumed, not emitted as characters
        }
        else
        {
            // Invalid character in OSC sequence - abort and consume everything
            // Reset to normal state and clear buffer (don't emit accumulated text)
            _state = AnsiState.Normal;
            _oscEscSeen = false;
            _buffer.Clear();
            // Invalid character is also consumed, not emitted
        }
    }

    private void EmitOscCommand(string oscString)
    {
        if (string.IsNullOrEmpty(oscString))
        {
            return;
        }
        var rawText = $"\x1B]{oscString}\x07";
        var command = new AnsiCommand
        {
            Type = AnsiCommandType.Osc,
            Parameters = new List<int>(),
            OscString = oscString,
            RawText = rawText
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
        var escapedOsc = EscapeString(oscString);
        Debug.WriteLine($"[AnsiParser] Emitting OSC command: code={command.Parameters[0]}, string=\"{escapedOsc}\"");
        System.Console.WriteLine($"[AnsiParser] Emitting OSC command: code={command.Parameters[0]}, string=\"{escapedOsc}\"");
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
        // Character set designation commands: ESC ( B, ESC ) B, ESC * B, ESC + B, etc.
        // These are used to select character sets and should be silently ignored (no-op)
        // The intermediate character (like '(') is in the buffer, and the final character (like 'B') is c
        // Just reset to normal state - we don't need to process these commands
        if (c == 0x1B)
        {
            _state = AnsiState.Escape;
            _buffer.Clear();
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
    public string RawText { get; set; } = string.Empty;
}

public enum AnsiCommandType
{
    Csi,
    Osc,
    Dcs,
    SingleChar
}

