using System.Text;

namespace NXTerm.Logging;

/// <summary>Writes the received stream to a file: raw bytes, raw text, or plain text with escape sequences removed.</summary>
public sealed class SessionLogger : IDisposable
{
    readonly FileStream _fs;
    readonly StreamWriter? _writer;
    readonly bool _binary, _plain, _timestamp;
    readonly StringBuilder _sb = new();
    bool _lineStart = true;
    int _esc;  // 0 text, 1 after ESC, 2 CSI, 3 string (OSC/DCS), 4 string+ESC, 5 one more char

    public string FilePath { get; }

    public SessionLogger(string path, bool binary, bool plain, bool timestamp, bool append)
    {
        FilePath = path;
        _binary = binary;
        _plain = plain;
        _timestamp = timestamp;
        _fs = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
        if (!binary) _writer = new StreamWriter(_fs, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void WriteRaw(byte[] data)
    {
        if (!_binary) return;
        _fs.Write(data, 0, data.Length);
        _fs.Flush();
    }

    public void WriteText(string s)
    {
        if (_binary || _writer == null) return;
        _sb.Clear();
        foreach (char c in s)
        {
            if (_plain && !Plain(c)) continue;
            if (_timestamp && _lineStart && c != '\n')
            {
                _sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
                _lineStart = false;
            }
            if (c == '\n')
            {
                _sb.Append(_plain ? Environment.NewLine : "\n");
                _lineStart = true;
            }
            else _sb.Append(c);
        }
        _writer.Write(_sb);
    }

    /// <summary>Note written by the user or a macro.</summary>
    public void WriteNote(string s) => _writer?.Write(s);

    bool Plain(char c)
    {
        switch (_esc)
        {
            case 1:
                _esc = c switch
                {
                    '[' => 2,
                    ']' or 'P' or 'X' or '^' or '_' => 3,
                    '(' or ')' or '*' or '+' or '#' or ' ' or '%' => 5,
                    _ => 0,
                };
                return false;
            case 2:
                if (c >= '@' && c <= '~') _esc = 0;
                return false;
            case 3:
                if (c == '\a') _esc = 0;
                else if (c == '\x1b') _esc = 4;
                return false;
            case 4:
                _esc = c == '\\' ? 0 : 3;
                return false;
            case 5:
                _esc = 0;
                return false;
        }
        if (c == '\x1b') { _esc = 1; return false; }
        if (c == '\n' || c == '\t') return true;
        return c >= 0x20 && c != 0x7f;
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _fs.Dispose();
    }
}
