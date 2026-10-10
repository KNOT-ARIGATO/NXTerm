namespace NXTerm.Dialogs;

/// <summary>Auto-sized dialog laid out as a label/control/extra grid with OK and Cancel buttons.</summary>
public class DialogBase : Form
{
    protected readonly TableLayoutPanel Table;

    public DialogBase(string title)
    {
        Text = title;
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        Table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            Location = new Point(10, 10),
        };
        Controls.Add(Table);
    }

    protected T Row<T>(string label, T ctl, Control? extra = null) where T : Control
    {
        int r = Table.RowCount++;
        if (label.Length > 0)
            Table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 3) }, 0, r);
        if (ctl is TextBox or ComboBox or NumericUpDown && ctl.Width < 260) ctl.Width = 260;
        ctl.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Table.Controls.Add(ctl, 1, r);
        if (extra != null) Table.Controls.Add(extra, 2, r);
        return ctl;
    }

    protected T Full<T>(T ctl) where T : Control
    {
        int r = Table.RowCount++;
        Table.Controls.Add(ctl, 0, r);
        Table.SetColumnSpan(ctl, 3);
        return ctl;
    }

    protected static Button SmallButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        b.Click += (_, _) => onClick();
        return b;
    }

    protected static ComboBox Combo(IEnumerable<object> items, object? selected, bool editable = false)
    {
        var c = new ComboBox { DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList };
        foreach (var i in items) c.Items.Add(i);
        if (editable) c.Text = selected?.ToString() ?? "";
        else c.SelectedItem = selected;
        if (!editable && c.SelectedIndex < 0 && c.Items.Count > 0) c.SelectedIndex = 0;
        return c;
    }

    protected void AddButtons(string okText = "OK")
    {
        var ok = new Button { Text = okText, DialogResult = DialogResult.OK, MinimumSize = new Size(80, 0), AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, MinimumSize = new Size(80, 0), AutoSize = true };
        ok.Click += (_, _) =>
        {
            if (!ValidateInput()) DialogResult = DialogResult.None;
        };
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 12, 0, 0),
        };
        flow.Controls.Add(cancel);
        flow.Controls.Add(ok);
        Full(flow);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected virtual bool ValidateInput() => true;

    protected bool Fail(string message, Control focus)
    {
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        focus.Focus();
        return false;
    }
}

public sealed class InputDialog : DialogBase
{
    readonly TextBox _box;

    InputDialog(string title, string prompt, string def, bool password) : base(title)
    {
        Full(new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(3, 3, 3, 6) });
        _box = Full(new TextBox { Text = def, Width = 380, UseSystemPasswordChar = password });
        AddButtons();
    }

    public static string? Show(IWin32Window? owner, string title, string prompt, string def = "", bool password = false)
    {
        using var d = new InputDialog(title, prompt, def, password);
        return d.ShowDialog(owner) == DialogResult.OK ? d._box.Text : null;
    }
}
