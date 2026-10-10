using NXTerm.Dialogs;
using NXTerm.Macro;

namespace NXTerm.QuickCommands;

public sealed class QuickCommandDialog : DialogBase
{
    readonly TextBox _name, _text;
    readonly ComboBox _group, _kind;
    readonly CheckBox _enter, _escapes, _confirm;
    readonly NumericUpDown _delay;
    readonly Button _color;
    string _colorHex;

    public QuickCommand Result { get; private set; } = new();
    public int GroupIndex => _group.SelectedIndex;

    public QuickCommandDialog(QuickCommand c, IList<string> groups, int groupIndex, bool isNew)
        : base(isNew ? "NXTerm: New quick command" : "NXTerm: Edit quick command")
    {
        _colorHex = c.Color;
        _name = Row("Button label:", new TextBox { Text = c.Name });
        _group = Row("Group (tab):", Combo(groups, null));
        _group.SelectedIndex = Math.Clamp(groupIndex, 0, groups.Count - 1);
        _kind = Row("Type:", Combo(["Text to send", "Macro (TTL)"], null));
        _kind.SelectedIndex = (int)c.Kind;
        _text = Row("Command:", new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 10f),
            Width = 460,
            Height = 150,
            Text = c.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
        });
        Full(new Label
        {
            Text = "Text: one command per line, each line is sent in order.\r\n" +
                   "Macro: TTL commands, e.g.  sendln 'ls'   wait '$ '   pause 1",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });
        _enter = Full(new CheckBox { Text = "Press Enter after each line", Checked = c.AppendEnter, AutoSize = true });
        _escapes = Full(new CheckBox { Text = @"Interpret escape codes  \r \n \t \e \xHH  (e.g. \x03 = Ctrl+C)", Checked = c.Escapes, AutoSize = true });
        _delay = Row("Delay between lines (ms):", new NumericUpDown { Maximum = 60000, Increment = 100, Value = Math.Clamp(c.LineDelayMs, 0, 60000) });
        _confirm = Full(new CheckBox { Text = "Ask before sending (for dangerous commands like reboot)", Checked = c.Confirm, AutoSize = true });

        var colors = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        _color = new Button { Text = "Choose...", AutoSize = true };
        _color.Click += (_, _) => PickColor();
        colors.Controls.AddRange([_color, SmallButton("Default", () => SetColor(""))]);
        Row("Button color:", colors);
        SetColor(_colorHex);

        _kind.SelectedIndexChanged += (_, _) => UpdateEnabled();
        UpdateEnabled();
        AddButtons();
        ActiveControl = isNew ? _text : _name;
    }

    void UpdateEnabled()
    {
        bool text = _kind.SelectedIndex == 0;
        _enter.Enabled = _escapes.Enabled = _delay.Enabled = text;
    }

    void PickColor()
    {
        using var cd = new ColorDialog { FullOpen = true, Color = AppSettings.ParseColor(_colorHex, SystemColors.Control) };
        if (cd.ShowDialog(this) == DialogResult.OK) SetColor(AppSettings.ToHex(cd.Color));
    }

    void SetColor(string hex)
    {
        _colorHex = hex;
        if (hex.Length == 0)
        {
            _color.UseVisualStyleBackColor = true;
            _color.ForeColor = SystemColors.ControlText;
            return;
        }
        var c = AppSettings.ParseColor(hex, SystemColors.Control);
        _color.BackColor = c;
        _color.ForeColor = c.GetBrightness() > 0.5 ? Color.Black : Color.White;
        _color.UseVisualStyleBackColor = false;
    }

    protected override bool ValidateInput()
    {
        var text = _text.Text.TrimEnd();
        if (text.Length == 0) return Fail("Please enter the command to send.", _text);
        var kind = (QuickCommandKind)_kind.SelectedIndex;
        if (kind == QuickCommandKind.Macro)
        {
            try { _ = new MacroRunner(text.Split('\n'), null!); }
            catch (MacroException ex) { return Fail($"Macro line {ex.Line}: {ex.Message}", _text); }
        }
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            name = text.Split('\n')[0].Trim();
            if (name.Length > 24) name = name[..24] + "…";
        }
        Result = new QuickCommand
        {
            Name = name,
            Text = text,
            Kind = kind,
            AppendEnter = _enter.Checked,
            Escapes = _escapes.Checked,
            LineDelayMs = (int)_delay.Value,
            Confirm = _confirm.Checked,
            Color = _colorHex,
        };
        return true;
    }
}
