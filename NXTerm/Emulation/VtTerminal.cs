using System.Globalization;
using System.Text;

namespace NXTerm.Emulation;

/// <summary>
/// VT100/VT220/xterm compatible screen model and escape-sequence parser.
/// All members must be used from a single (UI) thread.
/// </summary>
public sealed class VtTerminal
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int MaxScrollback { get; set; } = 10000;
    public LineRing Scrollback { get; } = new();
    public bool AltScreen { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }

    // Modes
    public bool AutoWrap = true;
    public bool OriginMode;
    public bool InsertMode;
    public bool CursorVisible = true;
    public bool AppCursorKeys;
    public bool AppKeypad;
    public bool BracketedPaste;
    public bool ReverseVideo;
    public bool NewLineMode;
    public int MouseTracking;   // 0, 1000, 1002, 1003
    public bool MouseSgr;
    public CursorShape? CursorShapeOverride;

    // Receive newline handling (Tera Term "Receive: CR / LF / AUTO")
    public bool LfImpliesCr;
    public bool CrImpliesLf;

    public event Action<string>? Response;
    public event Action<string>? TitleChanged;
    public event Action? Bell;
    /// <summary>Raised when a line moves from the screen into scrollback.</summary>
    public event Action? LineScrolledOff;
    /// <summary>Raised when the oldest scrollback line is discarded.</summary>
    public event Action? OldestLineDropped;
    public event Action? ScrollbackCleared;

    TermLine[] _main, _alt, _lines;
    bool _wrapPending;
    int _top, _bottom;
    int _fg = -1, _bg = -1;
    CellAttr _attr;
    bool[] _tabs = [];
    char _g0 = 'B', _g1 = 'B';
    int _gl;
    SavedCursor _saved;
    char _lastChar = ' ';
    char _pendingHigh;

    record struct SavedCursor(int X, int Y, int Fg, int Bg, CellAttr Attr, bool Origin, bool WrapPending, char G0, char G1, int Gl);

    enum State { Ground, Esc, EscInter, Csi, Osc, OscEsc, Dcs, DcsEsc, Charset }
    State _state;
    readonly StringBuilder _param = new(), _inter = new(), _osc = new();
    char _prefix, _charsetSlot;

    const string DecSpecial =
        " ◆▒␉␌␍␊°±␤␋┘┐┌└┼" +
        "⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

    public VtTerminal(int cols, int rows)
    {
        Cols = Math.Max(2, cols);
        Rows = Math.Max(2, rows);
        _main = NewBuffer();
        _alt = NewBuffer();
        _lines = _main;
        _bottom = Rows - 1;
        ResetTabs();
        _saved = new(0, 0, -1, -1, CellAttr.None, false, false, 'B', 'B', 0);
    }

    TermLine[] NewBuffer()
    {
        var b = new TermLine[Rows];
        for (int i = 0; i < Rows; i++) b[i] = new TermLine(Cols, -1);
        return b;
    }

    public int TotalLines => Scrollback.Count + Rows;

    /// <summary>Line by absolute index: 0..Scrollback.Count-1 is history, then the screen.</summary>
    public TermLine GetLine(int abs) => abs < Scrollback.Count ? Scrollback[abs] : _lines[abs - Scrollback.Count];

    public string GetText(int l1, int c1, int l2, int c2)
    {
        var sb = new StringBuilder();
        for (int l = Math.Max(0, l1); l <= l2 && l < TotalLines; l++)
        {
            var line = GetLine(l);
            int from = l == l1 ? c1 : 0;
            int to = l == l2 ? c2 : line.Cells.Length;
            string t = line.GetText(from, to);
            bool continues = line.Wrapped && to >= line.Cells.Length && l < l2;
            sb.Append(continues ? t : t.TrimEnd(' '));
            if (l < l2 && !continues) sb.Append("\r\n");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ input

    public void Feed(string s)
    {
        foreach (char c in s) Process(c);
    }

    void Process(char c)
    {
        switch (_state)
        {
            case State.Osc:
                if (c == '\x07') { OscDispatch(); _state = State.Ground; }
                else if (c == '\x1b') _state = State.OscEsc;
                else if (c is '\x18' or '\x1a') _state = State.Ground;
                else if (_osc.Length < 4096) _osc.Append(c);
                return;
            case State.OscEsc:
                OscDispatch();
                _state = State.Ground;
                if (c != '\\') { _state = State.Esc; _inter.Clear(); Process(c); }
                return;
            case State.Dcs:
                if (c == '\x1b') _state = State.DcsEsc;
                else if (c is '\x18' or '\x1a') _state = State.Ground;
                return;
            case State.DcsEsc:
                _state = State.Ground;
                if (c != '\\') { _state = State.Esc; _inter.Clear(); Process(c); }
                return;
        }

        if (c == '\x1b') { _state = State.Esc; _inter.Clear(); return; }
        if (c is '\x18' or '\x1a') { _state = State.Ground; return; }
        if (c < 0x20) { Control(c); return; }
        if (c == '\x7f') return;

        switch (_state)
        {
            case State.Ground:
                Print(c);
                break;
            case State.Esc:
                EscDispatch(c);
                break;
            case State.EscInter:
                if (c >= 0x20 && c <= 0x2f) _inter.Append(c);
                else
                {
                    if (_inter.ToString() == "#" && c == '8') Decaln();
                    _state = State.Ground;
                }
                break;
            case State.Charset:
                if (_charsetSlot == '(') _g0 = c;
                else if (_charsetSlot == ')') _g1 = c;
                _state = State.Ground;
                break;
            case State.Csi:
                if (c >= '0' && c <= '?')
                {
                    if (c is '<' or '=' or '>' or '?' && _param.Length == 0 && _prefix == '\0') _prefix = c;
                    else if (_param.Length < 256) _param.Append(c);
                }
                else if (c >= ' ' && c <= '/') _inter.Append(c);
                else if (c >= '@' && c <= '~') { _state = State.Ground; CsiDispatch(c); }
                else _state = State.Ground;
                break;
        }
    }

    void Control(char c)
    {
        switch (c)
        {
            case '\a': Bell?.Invoke(); break;
            case '\b':
                _wrapPending = false;
                if (CursorX > 0) CursorX--;
                break;
            case '\t': Tab(1); break;
            case '\n':
            case '\v':
            case '\f':
                if (LfImpliesCr || NewLineMode) CarriageReturn();
                Index();
                break;
            case '\r':
                CarriageReturn();
                if (CrImpliesLf) Index();
                break;
            case '\x0e': _gl = 1; break;
            case '\x0f': _gl = 0; break;
        }
    }

    void EscDispatch(char c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[': _state = State.Csi; _param.Clear(); _inter.Clear(); _prefix = '\0'; break;
            case ']': _state = State.Osc; _osc.Clear(); break;
            case 'P': case 'X': case '^': case '_': _state = State.Dcs; break;
            case '(': case ')': case '*': case '+': _charsetSlot = c; _state = State.Charset; break;
            case '#': case ' ': case '%': _inter.Clear(); _inter.Append(c); _state = State.EscInter; break;
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': Index(); break;
            case 'E': CarriageReturn(); Index(); break;
            case 'M': ReverseIndex(); break;
            case 'H': _tabs[CursorX] = true; break;
            case 'c': Reset(); break;
            case '=': AppKeypad = true; break;
            case '>': AppKeypad = false; break;
            case 'Z': Response?.Invoke("\x1b[?62;22c"); break;
        }
    }

    void OscDispatch()
    {
        var s = _osc.ToString();
        int semi = s.IndexOf(';');
        if (semi < 0) return;
        var code = s[..semi];
        if (code is "0" or "2") TitleChanged?.Invoke(s[(semi + 1)..]);
    }

    List<int> ParseParams()
    {
        var list = new List<int>();
        if (_param.Length == 0) return list;
        foreach (var part in _param.ToString().Split(';'))
        {
            var p = part;
            int colon = p.IndexOf(':');
            if (colon >= 0) p = p[..colon];
            list.Add(int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? Math.Min(v, 65535) : -1);
        }
        return list;
    }

    void CsiDispatch(char f)
    {
        var ps = ParseParams();
        int P(int i, int def) => i < ps.Count && ps[i] > 0 ? ps[i] : def;
        int P0(int i) => i < ps.Count && ps[i] >= 0 ? ps[i] : 0;
        string inter = _inter.ToString();

        if (_prefix == '?')
        {
            switch (f)
            {
                case 'h': foreach (var m in ps) SetPrivateMode(m, true); break;
                case 'l': foreach (var m in ps) SetPrivateMode(m, false); break;
                case 'J': EraseDisplay(P0(0)); break;
                case 'K': EraseLine(P0(0)); break;
            }
            return;
        }
        if (_prefix == '>')
        {
            if (f == 'c') Response?.Invoke("\x1b[>1;10;0c");
            return;
        }
        if (_prefix != '\0') return;
        if (inter == " ")
        {
            if (f == 'q')
                CursorShapeOverride = P0(0) switch
                {
                    1 or 2 => CursorShape.Block,
                    3 or 4 => CursorShape.Underline,
                    5 or 6 => CursorShape.Bar,
                    _ => null,
                };
            return;
        }
        if (inter == "!")
        {
            if (f == 'p') SoftReset();
            return;
        }
        if (inter.Length > 0) return;

        switch (f)
        {
            case '@': InsertChars(P(0, 1)); break;
            case 'A': CursorUp(P(0, 1)); break;
            case 'B': case 'e': CursorDown(P(0, 1)); break;
            case 'C': case 'a': MoveX(CursorX + P(0, 1)); break;
            case 'D': MoveX(CursorX - P(0, 1)); break;
            case 'E': CursorDown(P(0, 1)); CursorX = 0; break;
            case 'F': CursorUp(P(0, 1)); CursorX = 0; break;
            case 'G': case '`': MoveX(P(0, 1) - 1); break;
            case 'H': case 'f': SetCursor(P(1, 1) - 1, (OriginMode ? _top : 0) + P(0, 1) - 1); break;
            case 'I': Tab(P(0, 1)); break;
            case 'J': EraseDisplay(P0(0)); break;
            case 'K': EraseLine(P0(0)); break;
            case 'L': InsertLines(P(0, 1)); break;
            case 'M': DeleteLines(P(0, 1)); break;
            case 'P': DeleteChars(P(0, 1)); break;
            case 'S': ScrollUp(P(0, 1)); break;
            case 'T': if (ps.Count <= 1) ScrollDown(P(0, 1)); break;
            case 'X': EraseChars(P(0, 1)); break;
            case 'Z': BackTab(P(0, 1)); break;
            case 'b':
                for (int i = Math.Min(P(0, 1), Cols * Rows); i > 0; i--) Print(_lastChar);
                break;
            case 'c': if (P0(0) == 0) Response?.Invoke("\x1b[?62;22c"); break;
            case 'd': SetCursor(CursorX, (OriginMode ? _top : 0) + P(0, 1) - 1); break;
            case 'g':
                if (P0(0) == 0) _tabs[CursorX] = false;
                else if (P0(0) == 3) Array.Clear(_tabs);
                break;
            case 'h':
            case 'l':
                foreach (var m in ps)
                {
                    if (m == 4) InsertMode = f == 'h';
                    else if (m == 20) NewLineMode = f == 'h';
                }
                break;
            case 'm': Sgr(); break;
            case 'n':
                if (P0(0) == 5) Response?.Invoke("\x1b[0n");
                else if (P0(0) == 6) Response?.Invoke($"\x1b[{CursorY + 1 - (OriginMode ? _top : 0)};{CursorX + 1}R");
                break;
            case 'r':
                {
                    int t = P(0, 1) - 1, b = Math.Min(P(1, Rows), Rows) - 1;
                    if (t < b)
                    {
                        _top = t;
                        _bottom = b;
                        SetCursor(0, OriginMode ? _top : 0);
                    }
                    break;
                }
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 't': if (P0(0) == 18) Response?.Invoke($"\x1b[8;{Rows};{Cols}t"); break;
        }
    }

    void SetPrivateMode(int m, bool on)
    {
        switch (m)
        {
            case 1: AppCursorKeys = on; break;
            case 5: ReverseVideo = on; break;
            case 6: OriginMode = on; SetCursor(0, on ? _top : 0); break;
            case 7: AutoWrap = on; break;
            case 25: CursorVisible = on; break;
            case 47: SwitchScreen(on, false); break;
            case 1047:
                if (!on && AltScreen) ClearBuffer(_alt);
                SwitchScreen(on, false);
                break;
            case 1048:
                if (on) SaveCursor(); else RestoreCursor();
                break;
            case 1049:
                if (on) { SaveCursor(); SwitchScreen(true, true); }
                else { SwitchScreen(false, false); RestoreCursor(); }
                break;
            case 1000: case 1002: case 1003: MouseTracking = on ? m : 0; break;
            case 1006: MouseSgr = on; break;
            case 2004: BracketedPaste = on; break;
        }
    }

    void Sgr()
    {
        var groups = new List<int[]>();
        foreach (var g in _param.ToString().Split(';'))
            groups.Add(g.Split(':').Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : -1).ToArray());

        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            int p = Math.Max(0, g[0]);
            switch (p)
            {
                case 0: _fg = _bg = -1; _attr = CellAttr.None; break;
                case 1: _attr |= CellAttr.Bold; break;
                case 2: _attr |= CellAttr.Dim; break;
                case 3: _attr |= CellAttr.Italic; break;
                case 4:
                    if (g.Length > 1 && g[1] == 0) _attr &= ~CellAttr.Underline;
                    else _attr |= CellAttr.Underline;
                    break;
                case 5: case 6: _attr |= CellAttr.Blink; break;
                case 7: _attr |= CellAttr.Reverse; break;
                case 8: _attr |= CellAttr.Invisible; break;
                case 9: _attr |= CellAttr.Strike; break;
                case 21: _attr |= CellAttr.Underline; break;
                case 22: _attr &= ~(CellAttr.Bold | CellAttr.Dim); break;
                case 23: _attr &= ~CellAttr.Italic; break;
                case 24: _attr &= ~CellAttr.Underline; break;
                case 25: _attr &= ~CellAttr.Blink; break;
                case 27: _attr &= ~CellAttr.Reverse; break;
                case 28: _attr &= ~CellAttr.Invisible; break;
                case 29: _attr &= ~CellAttr.Strike; break;
                case >= 30 and <= 37: _fg = p - 30; break;
                case 39: _fg = -1; break;
                case >= 40 and <= 47: _bg = p - 40; break;
                case 49: _bg = -1; break;
                case >= 90 and <= 97: _fg = p - 90 + 8; break;
                case >= 100 and <= 107: _bg = p - 100 + 8; break;
                case 38:
                case 48:
                    {
                        int color = g.Length > 1 ? ExtColorColon(g) : ExtColorSemi(groups, ref i);
                        if (color != int.MinValue)
                        {
                            if (p == 38) _fg = color; else _bg = color;
                        }
                        break;
                    }
            }
        }
    }

    static int ExtColorColon(int[] g)
    {
        if (g[1] == 5 && g.Length > 2) return Math.Clamp(g[2], 0, 255);
        if (g[1] == 2)
        {
            int o = g.Length >= 6 ? 3 : 2;  // 38:2::r:g:b or 38:2:r:g:b
            if (g.Length >= o + 3) return Rgb(g[o], g[o + 1], g[o + 2]);
        }
        return int.MinValue;
    }

    static int ExtColorSemi(List<int[]> groups, ref int i)
    {
        if (i + 1 >= groups.Count) return int.MinValue;
        int mode = groups[i + 1][0];
        if (mode == 5 && i + 2 < groups.Count)
        {
            i += 2;
            return Math.Clamp(groups[i][0], 0, 255);
        }
        if (mode == 2 && i + 4 < groups.Count)
        {
            int r = groups[i + 2][0], g = groups[i + 3][0], b = groups[i + 4][0];
            i += 4;
            return Rgb(r, g, b);
        }
        return int.MinValue;
    }

    static int Rgb(int r, int g, int b) =>
        0x1000000 | (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);

    // ------------------------------------------------------------------ printing

    void Print(char c)
    {
        string? low = null;
        if (char.IsHighSurrogate(c)) { _pendingHigh = c; return; }
        if (char.IsLowSurrogate(c))
        {
            if (_pendingHigh == '\0') return;
            low = c.ToString();
            c = _pendingHigh;
        }
        _pendingHigh = '\0';

        char cs = _gl == 0 ? _g0 : _g1;
        if (cs == '0' && c >= 0x5f && c <= 0x7e) c = DecSpecial[c - 0x5f];

        if (low == null && IsCombining(c))
        {
            AttachCombining(c);
            return;
        }

        int w = low != null || IsWide(c) ? 2 : 1;
        if (_wrapPending)
        {
            if (AutoWrap)
            {
                _lines[CursorY].Wrapped = true;
                CarriageReturn();
                Index();
            }
            _wrapPending = false;
        }
        if (w == 2 && CursorX == Cols - 1)
        {
            if (AutoWrap)
            {
                _lines[CursorY].Clear(CursorX, Cols, _bg);
                _lines[CursorY].Wrapped = true;
                CarriageReturn();
                Index();
            }
            else CursorX = Cols - 2;
        }
        if (InsertMode) InsertChars(w);

        var cells = _lines[CursorY].Cells;
        BreakWide(cells, CursorX);
        if (w == 2) BreakWide(cells, CursorX + 1);
        cells[CursorX] = new Cell { Ch = c, Comb = low, Fg = _fg, Bg = _bg, Attr = _attr | (w == 2 ? CellAttr.Wide : 0) };
        if (w == 2) cells[CursorX + 1] = new Cell { Ch = ' ', Fg = _fg, Bg = _bg, Attr = _attr | CellAttr.WideCont };
        if (low == null) _lastChar = c;

        int nx = CursorX + w;
        if (nx >= Cols)
        {
            CursorX = Cols - 1;
            _wrapPending = AutoWrap;
        }
        else CursorX = nx;
    }

    void AttachCombining(char c)
    {
        int x = _wrapPending ? CursorX : CursorX - 1;
        if (x < 0) return;
        var cells = _lines[CursorY].Cells;
        if ((cells[x].Attr & CellAttr.WideCont) != 0 && x > 0) x--;
        if (cells[x].Comb == null || cells[x].Comb!.Length < 8) cells[x].Comb += c;
    }

    void BreakWide(Cell[] cells, int x)
    {
        if (x >= cells.Length) return;
        var a = cells[x].Attr;
        if ((a & CellAttr.WideCont) != 0 && x > 0) cells[x - 1] = Cell.Blank(cells[x - 1].Bg);
        if ((a & CellAttr.Wide) != 0 && x + 1 < cells.Length) cells[x + 1] = Cell.Blank(cells[x + 1].Bg);
    }

    static bool IsCombining(char c)
    {
        if (c < 0x300) return false;
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format;
    }

    static bool IsWide(char c) =>
        c >= 0x1100 && (c <= 0x115f
        || (c >= 0x2e80 && c <= 0xa4cf && c != 0x303f)
        || (c >= 0xac00 && c <= 0xd7a3)
        || (c >= 0xf900 && c <= 0xfaff)
        || (c >= 0xfe30 && c <= 0xfe4f)
        || (c >= 0xff00 && c <= 0xff60)
        || (c >= 0xffe0 && c <= 0xffe6));

    // ------------------------------------------------------------------ cursor

    void CarriageReturn()
    {
        CursorX = 0;
        _wrapPending = false;
    }

    void Index()
    {
        _wrapPending = false;
        if (CursorY == _bottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    void ReverseIndex()
    {
        _wrapPending = false;
        if (CursorY == _top) ScrollDown(1);
        else if (CursorY > 0) CursorY--;
    }

    void SetCursor(int x, int y)
    {
        _wrapPending = false;
        CursorX = Math.Clamp(x, 0, Cols - 1);
        int minY = OriginMode ? _top : 0, maxY = OriginMode ? _bottom : Rows - 1;
        CursorY = Math.Clamp(y, minY, maxY);
    }

    void MoveX(int x)
    {
        _wrapPending = false;
        CursorX = Math.Clamp(x, 0, Cols - 1);
    }

    void CursorUp(int n)
    {
        _wrapPending = false;
        int min = CursorY >= _top ? _top : 0;
        CursorY = Math.Max(CursorY - n, min);
    }

    void CursorDown(int n)
    {
        _wrapPending = false;
        int max = CursorY <= _bottom ? _bottom : Rows - 1;
        CursorY = Math.Min(CursorY + n, max);
    }

    void Tab(int n)
    {
        for (; n > 0; n--)
        {
            int x = CursorX + 1;
            while (x < Cols - 1 && !_tabs[x]) x++;
            CursorX = Math.Min(x, Cols - 1);
        }
    }

    void BackTab(int n)
    {
        _wrapPending = false;
        for (; n > 0; n--)
        {
            int x = CursorX - 1;
            while (x > 0 && !_tabs[x]) x--;
            CursorX = Math.Max(x, 0);
        }
    }

    void ResetTabs()
    {
        _tabs = new bool[Cols];
        for (int i = 8; i < Cols; i += 8) _tabs[i] = true;
    }

    void SaveCursor() => _saved = new(CursorX, CursorY, _fg, _bg, _attr, OriginMode, _wrapPending, _g0, _g1, _gl);

    void RestoreCursor()
    {
        var s = _saved;
        CursorX = Math.Min(s.X, Cols - 1);
        CursorY = Math.Min(s.Y, Rows - 1);
        _fg = s.Fg; _bg = s.Bg; _attr = s.Attr;
        OriginMode = s.Origin;
        _wrapPending = s.WrapPending;
        _g0 = s.G0; _g1 = s.G1; _gl = s.Gl;
    }

    // ------------------------------------------------------------------ scrolling & editing

    void PushScrollback(TermLine line)
    {
        if (MaxScrollback <= 0) return;
        Scrollback.Add(line);
        LineScrolledOff?.Invoke();
        while (Scrollback.Count > MaxScrollback)
        {
            Scrollback.RemoveFirst();
            OldestLineDropped?.Invoke();
        }
    }

    void ScrollUp(int n)
    {
        n = Math.Clamp(n, 0, _bottom - _top + 1);
        for (int i = 0; i < n; i++)
        {
            var first = _lines[_top];
            Array.Copy(_lines, _top + 1, _lines, _top, _bottom - _top);
            if (!AltScreen && _top == 0)
            {
                PushScrollback(first);
                first = new TermLine(Cols, _bg);
            }
            else
            {
                first.Clear(0, Cols, _bg);
                first.Wrapped = false;
            }
            _lines[_bottom] = first;
        }
    }

    void ScrollDown(int n)
    {
        n = Math.Clamp(n, 0, _bottom - _top + 1);
        for (int i = 0; i < n; i++)
        {
            var last = _lines[_bottom];
            Array.Copy(_lines, _top, _lines, _top + 1, _bottom - _top);
            last.Clear(0, Cols, _bg);
            last.Wrapped = false;
            _lines[_top] = last;
        }
    }

    void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                _lines[CursorY].Clear(CursorX, Cols, _bg);
                for (int y = CursorY + 1; y < Rows; y++) ClearLine(y);
                break;
            case 1:
                for (int y = 0; y < CursorY; y++) ClearLine(y);
                _lines[CursorY].Clear(0, CursorX + 1, _bg);
                break;
            case 2:
                for (int y = 0; y < Rows; y++) ClearLine(y);
                break;
            case 3:
                Scrollback.Clear();
                ScrollbackCleared?.Invoke();
                break;
        }
    }

    void EraseLine(int mode)
    {
        switch (mode)
        {
            case 0: _lines[CursorY].Clear(CursorX, Cols, _bg); _lines[CursorY].Wrapped = false; break;
            case 1: _lines[CursorY].Clear(0, CursorX + 1, _bg); break;
            case 2: ClearLine(CursorY); break;
        }
    }

    void ClearLine(int y)
    {
        _lines[y].Clear(0, Cols, _bg);
        _lines[y].Wrapped = false;
    }

    void InsertChars(int n)
    {
        _wrapPending = false;
        var cells = _lines[CursorY].Cells;
        n = Math.Min(n, Cols - CursorX);
        Array.Copy(cells, CursorX, cells, CursorX + n, Cols - CursorX - n);
        for (int i = CursorX; i < CursorX + n; i++) cells[i] = Cell.Blank(_bg);
    }

    void DeleteChars(int n)
    {
        _wrapPending = false;
        var cells = _lines[CursorY].Cells;
        n = Math.Min(n, Cols - CursorX);
        Array.Copy(cells, CursorX + n, cells, CursorX, Cols - CursorX - n);
        for (int i = Cols - n; i < Cols; i++) cells[i] = Cell.Blank(_bg);
    }

    void EraseChars(int n)
    {
        _wrapPending = false;
        _lines[CursorY].Clear(CursorX, CursorX + n, _bg);
    }

    void InsertLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        n = Math.Min(n, _bottom - CursorY + 1);
        for (int i = 0; i < n; i++)
        {
            var line = _lines[_bottom];
            Array.Copy(_lines, CursorY, _lines, CursorY + 1, _bottom - CursorY);
            line.Clear(0, Cols, _bg);
            line.Wrapped = false;
            _lines[CursorY] = line;
        }
        CarriageReturn();
    }

    void DeleteLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        n = Math.Min(n, _bottom - CursorY + 1);
        for (int i = 0; i < n; i++)
        {
            var line = _lines[CursorY];
            Array.Copy(_lines, CursorY + 1, _lines, CursorY, _bottom - CursorY);
            line.Clear(0, Cols, _bg);
            line.Wrapped = false;
            _lines[_bottom] = line;
        }
        CarriageReturn();
    }

    void Decaln()
    {
        foreach (var l in _lines)
            for (int x = 0; x < Cols; x++) l.Cells[x] = new Cell { Ch = 'E', Fg = -1, Bg = -1 };
        SetCursor(0, 0);
    }

    void SwitchScreen(bool alt, bool clear)
    {
        if (alt == AltScreen) return;
        AltScreen = alt;
        _lines = alt ? _alt : _main;
        if (alt && clear) ClearBuffer(_alt);
        _wrapPending = false;
    }

    void ClearBuffer(TermLine[] buf)
    {
        foreach (var l in buf)
        {
            l.Clear(0, Cols, -1);
            l.Wrapped = false;
        }
    }

    // ------------------------------------------------------------------ public operations

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(cols, 2);
        rows = Math.Max(rows, 2);
        if (cols == Cols && rows == Rows) return;

        int mainKeep = AltScreen ? _saved.Y : CursorY;
        _main = ResizeBuffer(_main, cols, rows, mainKeep, true, out int removedMain);
        _alt = ResizeBuffer(_alt, cols, rows, AltScreen ? CursorY : 0, false, out int removedAlt);
        if (AltScreen)
        {
            CursorY -= removedAlt;
            _saved = _saved with { Y = Math.Max(0, _saved.Y - removedMain) };
        }
        else CursorY -= removedMain;
        _lines = AltScreen ? _alt : _main;

        var tabs = new bool[cols];
        for (int i = 0; i < cols; i++) tabs[i] = i < _tabs.Length ? _tabs[i] : i % 8 == 0 && i > 0;
        _tabs = tabs;

        Cols = cols;
        Rows = rows;
        CursorX = Math.Clamp(CursorX, 0, cols - 1);
        CursorY = Math.Clamp(CursorY, 0, rows - 1);
        _saved = _saved with { X = Math.Min(_saved.X, cols - 1), Y = Math.Min(_saved.Y, rows - 1) };
        _top = 0;
        _bottom = rows - 1;
        _wrapPending = false;
    }

    TermLine[] ResizeBuffer(TermLine[] buf, int cols, int rows, int keepRow, bool toScrollback, out int removedTop)
    {
        foreach (var l in buf) l.Resize(cols);
        removedTop = Math.Clamp(keepRow - rows + 1, 0, buf.Length);
        if (toScrollback)
            for (int i = 0; i < removedTop; i++) PushScrollback(buf[i]);
        var n = new TermLine[rows];
        for (int i = 0; i < rows; i++)
        {
            int src = i + removedTop;
            n[i] = src < buf.Length ? buf[src] : new TermLine(cols, -1);
        }
        return n;
    }

    public void Reset()
    {
        _fg = _bg = -1;
        _attr = CellAttr.None;
        AutoWrap = true;
        OriginMode = InsertMode = AppCursorKeys = AppKeypad = BracketedPaste = ReverseVideo = NewLineMode = false;
        CursorVisible = true;
        MouseTracking = 0;
        MouseSgr = false;
        CursorShapeOverride = null;
        _g0 = _g1 = 'B';
        _gl = 0;
        _top = 0;
        _bottom = Rows - 1;
        ResetTabs();
        SwitchScreen(false, false);
        ClearBuffer(_main);
        ClearBuffer(_alt);
        CursorX = CursorY = 0;
        _wrapPending = false;
        _saved = new(0, 0, -1, -1, CellAttr.None, false, false, 'B', 'B', 0);
        _state = State.Ground;
    }

    void SoftReset()
    {
        InsertMode = OriginMode = AppCursorKeys = AppKeypad = false;
        AutoWrap = CursorVisible = true;
        _top = 0;
        _bottom = Rows - 1;
        _fg = _bg = -1;
        _attr = CellAttr.None;
        _g0 = _g1 = 'B';
        _gl = 0;
        _saved = new(0, 0, -1, -1, CellAttr.None, false, false, 'B', 'B', 0);
    }

    public void ClearScreen()
    {
        _bg = -1;
        EraseDisplay(2);
        SetCursor(0, 0);
    }

    public void ClearScrollback()
    {
        Scrollback.Clear();
        ScrollbackCleared?.Invoke();
    }
}
