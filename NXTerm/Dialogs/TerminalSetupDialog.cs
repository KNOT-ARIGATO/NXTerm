namespace NXTerm.Dialogs;

public sealed class TerminalSetupDialog : DialogBase
{
    public static readonly (string Name, string Label)[] Encodings =
    [
        ("utf-8", "UTF-8"),
        ("windows-874", "Thai (TIS-620 / Windows-874)"),
        ("shift_jis", "Japanese (Shift_JIS)"),
        ("euc-jp", "Japanese (EUC-JP)"),
        ("gb2312", "Chinese Simplified (GB2312)"),
        ("big5", "Chinese Traditional (Big5)"),
        ("ks_c_5601-1987", "Korean (KS C 5601)"),
        ("windows-1252", "Western (Windows-1252)"),
        ("iso-8859-1", "Latin-1 (ISO-8859-1)"),
        ("us-ascii", "ASCII"),
    ];

    static readonly string[] RecvNames = ["CR (normal)", "LF => CR+LF (auto add CR)", "CR => CR+LF (auto add LF)"];
    static readonly string[] SendNames = ["CR", "CR+LF", "LF"];

    readonly AppSettings _s;
    readonly NumericUpDown _cols, _rows, _scroll;
    readonly ComboBox _termType, _recv, _send, _enc, _bs, _cursor;
    readonly CheckBox _echo, _blink, _copySel, _rightPaste, _boldBright;
    readonly Button _fg, _bg, _cur;

    public TerminalSetupDialog(AppSettings settings) : base("NXTerm: Terminal setup")
    {
        _s = settings;
        _cols = Row("Columns:", new NumericUpDown { Minimum = 10, Maximum = 500, Value = Math.Clamp(_s.Cols, 10, 500) });
        _rows = Row("Rows:", new NumericUpDown { Minimum = 4, Maximum = 300, Value = Math.Clamp(_s.Rows, 4, 300) });
        _termType = Row("Terminal ID (TERM):", Combo(["xterm-256color", "xterm", "vt220", "vt100", "linux", "ansi"], _s.TermType, editable: true));
        _recv = Row("Receive new-line:", Combo(RecvNames, RecvNames[(int)_s.ReceiveNewline]));
        _send = Row("Transmit new-line:", Combo(SendNames, SendNames[(int)_s.TransmitNewline]));
        _enc = Row("Character encoding:", Combo(Encodings.Select(e => e.Label), Encodings.FirstOrDefault(e => e.Name == _s.EncodingName).Label ?? "UTF-8"));
        _bs = Row("Backspace key sends:", Combo(["DEL (0x7F)", "BS (0x08)"], _s.BackspaceSendsDel ? "DEL (0x7F)" : "BS (0x08)"));
        _scroll = Row("Scroll-back lines:", new NumericUpDown { Minimum = 0, Maximum = 1000000, Increment = 1000, Value = Math.Clamp(_s.ScrollbackLines, 0, 1000000) });
        _cursor = Row("Cursor shape:", Combo(Enum.GetNames<CursorShape>(), _s.CursorShape.ToString()));
        _echo = Full(new CheckBox { Text = "Local echo", Checked = _s.LocalEcho, AutoSize = true });
        _blink = Full(new CheckBox { Text = "Blinking cursor", Checked = _s.CursorBlink, AutoSize = true });
        _copySel = Full(new CheckBox { Text = "Copy to clipboard on selection", Checked = _s.CopyOnSelect, AutoSize = true });
        _rightPaste = Full(new CheckBox { Text = "Right click pastes (otherwise shows a menu)", Checked = _s.RightClickPaste, AutoSize = true });
        _boldBright = Full(new CheckBox { Text = "Show bold text in bright colors", Checked = _s.BoldAsBright, AutoSize = true });

        var colors = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        _fg = ColorButton("Text", _s.ForeColor);
        _bg = ColorButton("Background", _s.BackColor);
        _cur = ColorButton("Cursor", _s.CursorColor);
        colors.Controls.AddRange([_fg, _bg, _cur, SmallButton("Defaults", ResetColors)]);
        Row("Colors:", colors);
        AddButtons();
    }

    Button ColorButton(string text, string hex)
    {
        var b = new Button { Text = text, AutoSize = true, Tag = AppSettings.ParseColor(hex, Color.Gray) };
        PaintSwatch(b);
        b.Click += (_, _) =>
        {
            using var cd = new ColorDialog { Color = (Color)b.Tag!, FullOpen = true };
            if (cd.ShowDialog(this) != DialogResult.OK) return;
            b.Tag = cd.Color;
            PaintSwatch(b);
        };
        return b;
    }

    static void PaintSwatch(Button b)
    {
        var c = (Color)b.Tag!;
        b.BackColor = c;
        b.ForeColor = c.GetBrightness() > 0.5 ? Color.Black : Color.White;
        b.UseVisualStyleBackColor = false;
    }

    void ResetColors()
    {
        var d = new AppSettings();
        foreach (var (b, hex) in new[] { (_fg, d.ForeColor), (_bg, d.BackColor), (_cur, d.CursorColor) })
        {
            b.Tag = AppSettings.ParseColor(hex, Color.Gray);
            PaintSwatch(b);
        }
    }

    protected override bool ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(_termType.Text)) return Fail("Terminal ID must not be empty.", _termType);
        _s.Cols = (int)_cols.Value;
        _s.Rows = (int)_rows.Value;
        _s.TermType = _termType.Text.Trim();
        _s.ReceiveNewline = (ReceiveNewline)_recv.SelectedIndex;
        _s.TransmitNewline = (TransmitNewline)_send.SelectedIndex;
        _s.EncodingName = Encodings[_enc.SelectedIndex].Name;
        _s.BackspaceSendsDel = _bs.SelectedIndex == 0;
        _s.ScrollbackLines = (int)_scroll.Value;
        _s.CursorShape = Enum.Parse<CursorShape>((string)_cursor.SelectedItem!);
        _s.LocalEcho = _echo.Checked;
        _s.CursorBlink = _blink.Checked;
        _s.CopyOnSelect = _copySel.Checked;
        _s.RightClickPaste = _rightPaste.Checked;
        _s.BoldAsBright = _boldBright.Checked;
        _s.ForeColor = AppSettings.ToHex((Color)_fg.Tag!);
        _s.BackColor = AppSettings.ToHex((Color)_bg.Tag!);
        _s.CursorColor = AppSettings.ToHex((Color)_cur.Tag!);
        return true;
    }
}
