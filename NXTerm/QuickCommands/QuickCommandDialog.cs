using NXTerm.Dialogs;
using NXTerm.Macro;

namespace NXTerm.QuickCommands;

public sealed class QuickCommandDialog : DialogBase
{
    readonly TextBox _name, _text;
    readonly ComboBox _group, _tab, _kind;
    readonly QuickCommandSet _set;
    readonly CheckBox _enter, _escapes, _confirm;
    readonly NumericUpDown _delay;
    readonly Button _color;
    string _colorHex;

    public QuickCommand Result { get; private set; } = new();
    /// <summary>Where the button goes: an existing group and tab, or new names typed by the user.</summary>
    public string TargetGroup { get; private set; } = "";
    public string TargetTab { get; private set; } = "";

    public QuickCommandDialog(QuickCommand c, QuickCommandSet set, int group, int tab, bool isNew)
        : base(isNew ? "NXTerm: New quick command" : "NXTerm: Edit quick command")
    {
        _set = set;
        _colorHex = c.Color;
        _name = Row("Button label:", new TextBox { Text = c.Name });
        // Choosing another group or tab here moves the button there; a name that does not exist yet is created.
        _group = Row("Group:", Combo(set.Groups.Select(g => (object)g.Name), null, editable: true));
        _tab = Row("Tab:", Combo([], null, editable: true));
        if (group >= 0 && group < set.Groups.Count) _group.Text = set.Groups[group].Name;
        else _group.Text = QuickCommandSet.DefaultGroupName;
        FillTabs();
        var tabs = set.Groups.ElementAtOrDefault(group)?.Tabs;
        _tab.Text = tabs != null && tab >= 0 && tab < tabs.Count ? tabs[tab].Name : _tab.Items.Count > 0 ? (string)_tab.Items[0]! : "General";
        _group.TextChanged += (_, _) =>
        {
            FillTabs();
            _tab.Text = _tab.Items.Count > 0 ? (string)_tab.Items[0]! : "General";
        };
        _kind = Row("Type:", Combo(["Text to send", "Macro (script)"], null));
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
                   "Macro: script commands, e.g.  sendln 'ls'   wait '$ '   pause 1",
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

    /// <summary>Lists the tabs of the group named in the Group box.</summary>
    void FillTabs()
    {
        _tab.Items.Clear();
        var g = _set.FindGroup(_group.Text.Trim());
        if (g != null)
            foreach (var t in g.Tabs) _tab.Items.Add(t.Name);
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
        if (_group.Text.Trim().Length == 0) return Fail("Please choose or type a group.", _group);
        if (_tab.Text.Trim().Length == 0) return Fail("Please choose or type a tab.", _tab);
        TargetGroup = _group.Text.Trim();
        TargetTab = _tab.Text.Trim();
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

/// <summary>Edits a tab: its name and the group it belongs to (choosing another group moves the tab with its buttons).</summary>
public sealed class QuickTabDialog : DialogBase
{
    readonly TextBox _name;
    readonly ComboBox _group;

    public string TabName => _name.Text.Trim();
    public string GroupName => _group.Text.Trim();

    public QuickTabDialog(QuickCommandSet set, string tabName, int group) : base("NXTerm: Edit tab")
    {
        _name = Row("Tab name:", new TextBox { Text = tabName });
        _group = Row("Group:", Combo(set.Groups.Select(g => (object)g.Name), null, editable: true));
        _group.Text = group >= 0 && group < set.Groups.Count ? set.Groups[group].Name : QuickCommandSet.DefaultGroupName;
        Full(new Label
        {
            Text = "Choose another group to move this tab and its buttons there.\r\nType a new name to create a new group.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });
        AddButtons();
        ActiveControl = _name;
    }

    protected override bool ValidateInput()
    {
        if (TabName.Length == 0) return Fail("Please enter a tab name.", _name);
        if (GroupName.Length == 0) return Fail("Please choose or type a group.", _group);
        return true;
    }
}
