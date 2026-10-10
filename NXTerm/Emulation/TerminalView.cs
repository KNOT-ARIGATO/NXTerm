using System.Text;

namespace NXTerm.Emulation;

/// <summary>Renders a <see cref="VtTerminal"/> and turns keyboard/mouse input into bytes for the host.</summary>
public sealed class TerminalView : Control
{
    const int Pad = 3;
    const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                               TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping;

    public VtTerminal Term { get; }

    readonly VScrollBar _sb = new() { Dock = DockStyle.Right };
    readonly System.Windows.Forms.Timer _blink = new() { Interval = 530 };
    readonly Color[] _palette = BuildPalette();
    readonly StringBuilder _run = new();

    Font _font = null!, _fontBold = null!, _fontItalic = null!, _fontBoldItalic = null!, _fontThai = null!;
    int _cw = 8, _ch = 16;
    bool _runSafe = true;
    int _viewOffset;
    bool _blinkOn = true;

    (int Line, int Col) _selA, _selB;
    bool _hasSel, _selecting;

    public Color DefaultFore = Color.FromArgb(0xE0, 0xE0, 0xE0);
    public Color DefaultBack = Color.Black;
    public Color CursorColor = Color.FromArgb(0x00, 0xE0, 0x00);
    public CursorShape CursorShape = CursorShape.Block;
    public bool CursorBlink = true;
    public bool CopyOnSelect = true;
    public bool RightClickPaste = true;
    public bool BoldAsBright = true;
    public bool BackspaceSendsDel = true;
    public string EnterSends = "\r";
    public ContextMenuStrip? PopupMenu;

    /// <summary>Text the user typed or pasted, to be sent to the host.</summary>
    public event Action<string>? Input;
    public event Action<int, int>? TerminalResized;

    public TerminalView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        Term = new VtTerminal(80, 24);
        Cursor = Cursors.IBeam;
        Controls.Add(_sb);
        _sb.Cursor = Cursors.Default;
        _sb.Scroll += (_, _) =>
        {
            _viewOffset = Term.Scrollback.Count - _sb.Value;
            Invalidate();
        };
        Term.LineScrolledOff += () =>
        {
            if (_viewOffset > 0) _viewOffset++;
        };
        Term.OldestLineDropped += () =>
        {
            if (!_hasSel) return;
            _selA.Line--;
            _selB.Line--;
            if (_selA.Line < 0 || _selB.Line < 0) _hasSel = false;
        };
        Term.ScrollbackCleared += () =>
        {
            _viewOffset = 0;
            _hasSel = false;
        };
        _blink.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            InvalidateCursor();
        };
        SetFont(new Font("Consolas", 11f));
    }

    public int CellWidth => _cw;
    public int CellHeight => _ch;

    public void SetFont(Font f)
    {
        var old = new[] { _font, _fontBold, _fontItalic, _fontBoldItalic, _fontThai };
        _font = f;
        _fontBold = new Font(f, FontStyle.Bold);
        _fontItalic = new Font(f, FontStyle.Italic);
        _fontBoldItalic = new Font(f, FontStyle.Bold | FontStyle.Italic);
        // Monospace fonts have no Thai glyphs and GDI's automatic fallback is cramped; Tahoma reads well at terminal sizes.
        _fontThai = new Font("Tahoma", f.SizeInPoints);
        foreach (var o in old) o?.Dispose();

        const string probe = "MMMMMMMMMMMMMMMMMMMM";
        var sz = TextRenderer.MeasureText(probe, f, Size.Empty, TF);
        _cw = Math.Max(1, (int)Math.Round(sz.Width / (double)probe.Length));
        _ch = Math.Max(1, sz.Height);
        // Draw whole runs of ASCII only if the font is truly monospaced in every style we use.
        _runSafe = sz.Width == _cw * probe.Length
                   && TextRenderer.MeasureText("il|.W@", f, Size.Empty, TF).Width == _cw * 6
                   && TextRenderer.MeasureText(probe, _fontBold, Size.Empty, TF).Width == sz.Width;
        RecalcSize();
        Invalidate();
    }

    /// <summary>Client size needed to show exactly cols x rows cells.</summary>
    public Size ClientSizeFor(int cols, int rows) => new(cols * _cw + Pad * 2 + _sb.Width, rows * _ch + Pad * 2);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (ClientSize.Width > 0 && ClientSize.Height > 0) RecalcSize();
    }

    void RecalcSize()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        int cols = Math.Max(2, (ClientSize.Width - _sb.Width - Pad * 2) / _cw);
        int rows = Math.Max(2, (ClientSize.Height - Pad * 2) / _ch);
        if (cols != Term.Cols || rows != Term.Rows)
        {
            Term.Resize(cols, rows);
            TerminalResized?.Invoke(cols, rows);
        }
        SyncScroll();
        Invalidate();
    }

    // ------------------------------------------------------------------ output

    public void Feed(string text)
    {
        Term.Feed(text);
        SyncScroll();
        Invalidate();
    }

    void SyncScroll()
    {
        int sc = Term.Scrollback.Count;
        _viewOffset = Math.Clamp(_viewOffset, 0, sc);
        _sb.Minimum = 0;
        _sb.SmallChange = 1;
        _sb.LargeChange = Math.Max(1, Term.Rows);
        _sb.Maximum = sc + Term.Rows - 1;
        _sb.Value = sc - _viewOffset;
    }

    public void ScrollView(int lines)
    {
        _viewOffset = Math.Clamp(_viewOffset + lines, 0, Term.Scrollback.Count);
        SyncScroll();
        Invalidate();
    }

    public void ClearScreen()
    {
        Term.ClearScreen();
        _hasSel = false;
        SyncScroll();
        Invalidate();
    }

    public void ClearBuffer()
    {
        Term.ClearScrollback();
        Term.ClearScreen();
        SyncScroll();
        Invalidate();
    }

    public void ResetTerminal()
    {
        Term.Reset();
        _hasSel = false;
        SyncScroll();
        Invalidate();
    }

    // ------------------------------------------------------------------ painting

    static Color[] BuildPalette()
    {
        var p = new Color[256];
        int[] basic =
        [
            0x000000, 0xCD0000, 0x00CD00, 0xCDCD00, 0x0000EE, 0xCD00CD, 0x00CDCD, 0xE5E5E5,
            0x7F7F7F, 0xFF0000, 0x00FF00, 0xFFFF00, 0x5C5CFF, 0xFF00FF, 0x00FFFF, 0xFFFFFF,
        ];
        for (int i = 0; i < 16; i++) p[i] = Color.FromArgb(unchecked((int)0xFF000000) | basic[i]);
        int[] lv = [0, 95, 135, 175, 215, 255];
        for (int i = 0; i < 216; i++) p[16 + i] = Color.FromArgb(lv[i / 36], lv[i / 6 % 6], lv[i % 6]);
        for (int i = 0; i < 24; i++)
        {
            int v = 8 + i * 10;
            p[232 + i] = Color.FromArgb(v, v, v);
        }
        return p;
    }

    Color ColorOf(int i) => i >= 0x1000000
        ? Color.FromArgb((i >> 16) & 255, (i >> 8) & 255, i & 255)
        : _palette[i & 255];

    (Color fg, Color bg) Resolve(in Cell c, bool selected)
    {
        int fi = c.Fg;
        if (BoldAsBright && (c.Attr & CellAttr.Bold) != 0 && fi >= 0 && fi < 8) fi += 8;
        Color fg = fi < 0 ? DefaultFore : ColorOf(fi);
        Color bg = c.Bg < 0 ? DefaultBack : ColorOf(c.Bg);
        bool rev = ((c.Attr & CellAttr.Reverse) != 0) ^ Term.ReverseVideo ^ selected;
        if (rev) (fg, bg) = (bg, fg);
        if ((c.Attr & CellAttr.Dim) != 0)
            fg = Color.FromArgb((fg.R + bg.R) / 2, (fg.G + bg.G) / 2, (fg.B + bg.B) / 2);
        if ((c.Attr & CellAttr.Invisible) != 0) fg = bg;
        return (fg, bg);
    }

    Font FontFor(in Cell c) => c.Ch >= 0x0E00 && c.Ch <= 0x0E7F ? _fontThai : FontFor(c.Attr);

    Font FontFor(CellAttr a) => (a & (CellAttr.Bold | CellAttr.Italic)) switch
    {
        CellAttr.Bold => _fontBold,
        CellAttr.Italic => _fontItalic,
        CellAttr.Bold | CellAttr.Italic => _fontBoldItalic,
        _ => _font,
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Color defBack = Term.ReverseVideo ? DefaultFore : DefaultBack;
        g.Clear(defBack);

        int top = Term.Scrollback.Count - _viewOffset;
        var (sa, sb) = OrderedSelection();
        for (int r = 0; r < Term.Rows; r++)
        {
            int abs = top + r;
            if (abs < 0 || abs >= Term.TotalLines) continue;
            int y = Pad + r * _ch;
            if (y > e.ClipRectangle.Bottom || y + _ch < e.ClipRectangle.Top) continue;
            DrawLine(g, Term.GetLine(abs), abs, y, sa, sb, defBack);
        }
        if (_viewOffset == 0 && Term.CursorVisible) DrawCursor(g);
    }

    static bool IsSimple(in Cell c) =>
        c.Comb == null && (c.Attr & (CellAttr.Wide | CellAttr.WideCont)) == 0 && c.Ch >= 0x20 && c.Ch < 0x7f;

    void DrawLine(Graphics g, TermLine line, int abs, int y, (int L, int C) sa, (int L, int C) sb, Color defBack)
    {
        var cells = line.Cells;
        int n = Math.Min(cells.Length, Term.Cols);
        int i = 0;
        while (i < n)
        {
            ref var c = ref cells[i];
            if ((c.Attr & CellAttr.WideCont) != 0) { i++; continue; }
            bool sel = IsSelected(abs, i, sa, sb);
            var (fg, bg) = Resolve(c, sel);
            int start = i;
            string text;
            bool simple = _runSafe && IsSimple(c);
            if (simple)
            {
                _run.Clear();
                _run.Append(c.Ch);
                i++;
                while (i < n)
                {
                    ref var d = ref cells[i];
                    if (!IsSimple(d) || d.Fg != c.Fg || d.Bg != c.Bg || d.Attr != c.Attr || IsSelected(abs, i, sa, sb) != sel) break;
                    _run.Append(d.Ch);
                    i++;
                }
                text = _run.ToString();
            }
            else
            {
                i += (c.Attr & CellAttr.Wide) != 0 ? 2 : 1;
                text = c.Comb == null ? c.Ch.ToString() : c.Ch + c.Comb;
            }

            int x = Pad + start * _cw, w = (i - start) * _cw;
            if (bg != defBack)
            {
                using var b = new SolidBrush(bg);
                g.FillRectangle(b, x, y, w, _ch);
            }
            if (fg != bg && !string.IsNullOrWhiteSpace(text))
                TextRenderer.DrawText(g, text, simple ? FontFor(c.Attr) : FontFor(c), new Point(x, y), fg, TF);
            if ((c.Attr & (CellAttr.Underline | CellAttr.Strike)) != 0 && fg != bg)
            {
                using var p = new Pen(fg);
                if ((c.Attr & CellAttr.Underline) != 0) g.DrawLine(p, x, y + _ch - 1, x + w - 1, y + _ch - 1);
                if ((c.Attr & CellAttr.Strike) != 0) g.DrawLine(p, x, y + _ch / 2, x + w - 1, y + _ch / 2);
            }
        }
    }

    void DrawCursor(Graphics g)
    {
        int cx = Term.CursorX, cy = Term.CursorY;
        int x = Pad + cx * _cw, y = Pad + cy * _ch;
        var cell = Term.GetLine(Term.Scrollback.Count + cy).Cells[cx];
        int w = (cell.Attr & CellAttr.Wide) != 0 ? _cw * 2 : _cw;
        if (!Focused)
        {
            using var p = new Pen(CursorColor);
            g.DrawRectangle(p, x, y, w - 1, _ch - 1);
            return;
        }
        if (CursorBlink && !_blinkOn) return;
        using var b = new SolidBrush(CursorColor);
        switch (Term.CursorShapeOverride ?? CursorShape)
        {
            case CursorShape.Underline:
                g.FillRectangle(b, x, y + _ch - 2, w, 2);
                break;
            case CursorShape.Bar:
                g.FillRectangle(b, x, y, 2, _ch);
                break;
            default:
                g.FillRectangle(b, x, y, w, _ch);
                if (cell.Ch != ' ' && (cell.Attr & CellAttr.WideCont) == 0)
                {
                    string t = cell.Comb == null ? cell.Ch.ToString() : cell.Ch + cell.Comb;
                    TextRenderer.DrawText(g, t, FontFor(cell), new Point(x, y), DefaultBack, TF);
                }
                break;
        }
    }

    void InvalidateCursor() =>
        Invalidate(new Rectangle(Pad + Term.CursorX * _cw - 1, Pad + Term.CursorY * _ch - 1, _cw * 2 + 2, _ch + 2));

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _blinkOn = true;
        if (CursorBlink) _blink.Start();
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _blink.Stop();
        Invalidate();
    }

    // ------------------------------------------------------------------ selection & clipboard

    ((int L, int C) a, (int L, int C) b) OrderedSelection()
    {
        if (!_hasSel) return ((-1, 0), (-1, 0));
        var a = _selA;
        var b = _selB;
        if (a.Line > b.Line || (a.Line == b.Line && a.Col > b.Col)) (a, b) = (b, a);
        return ((a.Line, a.Col), (b.Line, b.Col));
    }

    bool IsSelected(int line, int col, (int L, int C) a, (int L, int C) b) =>
        _hasSel
        && (line > a.L || (line == a.L && col >= a.C))
        && (line < b.L || (line == b.L && col < b.C));

    public string SelectedText
    {
        get
        {
            if (!_hasSel) return "";
            var (a, b) = OrderedSelection();
            return Term.GetText(a.L, a.C, b.L, b.C);
        }
    }

    public void CopySelection()
    {
        var text = SelectedText;
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.ExternalException) { }
    }

    public void SelectAll()
    {
        _selA = (0, 0);
        _selB = (Term.TotalLines - 1, Term.Cols);
        _hasSel = true;
        Invalidate();
    }

    public void PasteClipboard(string suffix = "")
    {
        string? t = null;
        try
        {
            if (Clipboard.ContainsText()) t = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
        if (!string.IsNullOrEmpty(t)) PasteText(t + suffix);
    }

    public void PasteText(string t)
    {
        t = t.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", EnterSends);
        if (Term.BracketedPaste) t = "\x1b[200~" + t + "\x1b[201~";
        SendInput(t);
    }

    (int Line, int Col) HitTest(Point p, bool round)
    {
        int row = (int)Math.Floor((p.Y - Pad) / (double)_ch);
        row = Math.Clamp(row, 0, Term.Rows - 1);
        double cx = (p.X - Pad) / (double)_cw;
        int col = round ? (int)Math.Round(cx) : (int)Math.Floor(cx);
        col = Math.Clamp(col, 0, round ? Term.Cols : Term.Cols - 1);
        return (Term.Scrollback.Count - _viewOffset + row, col);
    }

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || "_-./~:@?=&%+#".Contains(c);

    void SelectWord(Point p)
    {
        var (line, col) = HitTest(p, false);
        var cells = Term.GetLine(line).Cells;
        if (col >= cells.Length) return;
        int s = col, e = col;
        if (IsWordChar(cells[col].Ch))
        {
            while (s > 0 && IsWordChar(cells[s - 1].Ch)) s--;
            while (e < cells.Length - 1 && IsWordChar(cells[e + 1].Ch)) e++;
        }
        _selA = (line, s);
        _selB = (line, e + 1);
        _hasSel = true;
        Invalidate();
    }

    // ------------------------------------------------------------------ mouse

    bool MouseReport(MouseEventArgs e, bool press, bool motion = false, int wheel = 0)
    {
        if (Term.MouseTracking == 0 || (ModifierKeys & Keys.Shift) != 0) return false;
        if (motion)
        {
            if (Term.MouseTracking == 1000) return true;
            if (Term.MouseTracking == 1002 && e.Button == MouseButtons.None) return true;
        }
        int btn = wheel != 0 ? (wheel > 0 ? 64 : 65) : e.Button switch
        {
            MouseButtons.Left => 0,
            MouseButtons.Middle => 1,
            MouseButtons.Right => 2,
            _ => 3,
        };
        if (motion) btn += 32;
        if ((ModifierKeys & Keys.Alt) != 0) btn |= 8;
        if ((ModifierKeys & Keys.Control) != 0) btn |= 16;
        int col = Math.Clamp((e.X - Pad) / _cw, 0, Term.Cols - 1) + 1;
        int row = Math.Clamp((e.Y - Pad) / _ch, 0, Term.Rows - 1) + 1;
        string s;
        if (Term.MouseSgr) s = $"\x1b[<{btn};{col};{row}{(press ? 'M' : 'm')}";
        else
        {
            if (!press) btn = (btn & ~3) | 3;
            s = "\x1b[M" + (char)(32 + btn) + (char)(32 + Math.Min(col, 95)) + (char)(32 + Math.Min(row, 95));
        }
        Input?.Invoke(s);
        return true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (MouseReport(e, true)) return;
        switch (e.Button)
        {
            case MouseButtons.Left:
                if (e.Clicks == 2)
                {
                    SelectWord(e.Location);
                    if (CopyOnSelect) CopySelection();
                    return;
                }
                var p = HitTest(e.Location, true);
                if ((ModifierKeys & Keys.Shift) != 0 && _hasSel) _selB = p;
                else
                {
                    _selA = _selB = p;
                    _hasSel = false;
                }
                _selecting = true;
                Capture = true;
                Invalidate();
                break;
            case MouseButtons.Right:
                if (RightClickPaste) PasteClipboard();
                else PopupMenu?.Show(this, e.Location);
                break;
            case MouseButtons.Middle:
                PasteClipboard();
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting)
        {
            if (Term.MouseTracking >= 1002) MouseReport(e, true, motion: true);
            return;
        }
        if (e.Y < 0) ScrollView(1);
        else if (e.Y > ClientSize.Height) ScrollView(-1);
        _selB = HitTest(e.Location, true);
        _hasSel = _selA != _selB;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_selecting)
        {
            MouseReport(e, false);
            return;
        }
        _selecting = false;
        Capture = false;
        if (_hasSel && CopyOnSelect) CopySelection();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (MouseReport(e, true, wheel: e.Delta)) return;
        ScrollView(e.Delta / 120 * 3);
    }

    // ------------------------------------------------------------------ keyboard

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.Alt) == 0;

    void SendInput(string s)
    {
        if (_viewOffset != 0)
        {
            _viewOffset = 0;
            SyncScroll();
            Invalidate();
        }
        _blinkOn = true;
        Input?.Invoke(s);
    }

    string CursorKey(char f, int mod) =>
        mod == 1 ? (Term.AppCursorKeys ? $"\x1bO{f}" : $"\x1b[{f}") : $"\x1b[1;{mod}{f}";

    static string Tilde(int code, int mod) => mod == 1 ? $"\x1b[{code}~" : $"\x1b[{code};{mod}~";

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool shift = e.Shift, ctrl = e.Control, alt = e.Alt;
        if (alt) return;

        // Local actions
        if (ctrl && shift && e.KeyCode == Keys.C) { CopySelection(); Handled(e); return; }
        if (ctrl && shift && e.KeyCode == Keys.V) { PasteClipboard(); Handled(e); return; }
        if (ctrl && e.KeyCode == Keys.Insert) { CopySelection(); Handled(e); return; }
        if (shift && e.KeyCode == Keys.Insert) { PasteClipboard(); Handled(e); return; }
        if (shift && e.KeyCode == Keys.PageUp) { ScrollView(Term.Rows - 1); Handled(e); return; }
        if (shift && e.KeyCode == Keys.PageDown) { ScrollView(-(Term.Rows - 1)); Handled(e); return; }

        int mod = 1 + (shift ? 1 : 0) + (ctrl ? 4 : 0);
        string? seq = e.KeyCode switch
        {
            Keys.Up => CursorKey('A', mod),
            Keys.Down => CursorKey('B', mod),
            Keys.Right => CursorKey('C', mod),
            Keys.Left => CursorKey('D', mod),
            Keys.Home => CursorKey('H', mod),
            Keys.End => CursorKey('F', mod),
            Keys.Insert => Tilde(2, mod),
            Keys.Delete => Tilde(3, mod),
            Keys.PageUp => Tilde(5, mod),
            Keys.PageDown => Tilde(6, mod),
            Keys.F1 => mod == 1 ? "\x1bOP" : $"\x1b[1;{mod}P",
            Keys.F2 => mod == 1 ? "\x1bOQ" : $"\x1b[1;{mod}Q",
            Keys.F3 => mod == 1 ? "\x1bOR" : $"\x1b[1;{mod}R",
            Keys.F4 => mod == 1 ? "\x1bOS" : $"\x1b[1;{mod}S",
            Keys.F5 => Tilde(15, mod),
            Keys.F6 => Tilde(17, mod),
            Keys.F7 => Tilde(18, mod),
            Keys.F8 => Tilde(19, mod),
            Keys.F9 => Tilde(20, mod),
            Keys.F10 => Tilde(21, mod),
            Keys.F11 => Tilde(23, mod),
            Keys.F12 => Tilde(24, mod),
            Keys.Enter => EnterSends,
            Keys.Back => BackspaceSendsDel ^ ctrl ? "\x7f" : "\b",
            Keys.Tab => shift ? "\x1b[Z" : "\t",
            Keys.Escape => "\x1b",
            Keys.Space when ctrl => "\0",
            Keys.D2 when ctrl => "\0",
            Keys.D6 when ctrl => "\x1e",
            Keys.OemMinus when ctrl => "\x1f",
            _ => null,
        };
        if (seq != null)
        {
            SendInput(seq);
            Handled(e);
        }
    }

    static void Handled(KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (e.Handled) return;
        SendInput(e.KeyChar.ToString());
        e.Handled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _blink.Dispose();
            _font?.Dispose();
            _fontBold?.Dispose();
            _fontItalic?.Dispose();
            _fontBoldItalic?.Dispose();
            _fontThai?.Dispose();
        }
        base.Dispose(disposing);
    }
}
