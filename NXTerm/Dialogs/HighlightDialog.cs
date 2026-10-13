using NXTerm.Emulation;

namespace NXTerm.Dialogs;

/// <summary>Setup > Highlight keywords: a list of words or patterns, each with a colour.</summary>
public sealed class HighlightDialog : DialogBase
{
    const int ColPattern = 0, ColColor = 1, ColWholeLine = 2, ColRegex = 3;

    readonly CheckBox _enabled;
    readonly DataGridView _grid;

    public bool HighlightEnabled => _enabled.Checked;
    public List<HighlightRule> Rules { get; private set; } = [];

    public HighlightDialog(bool enabled, IEnumerable<HighlightRule> rules) : base("NXTerm: Highlight keywords")
    {
        _enabled = Full(new CheckBox { Text = "Colour these keywords on the terminal screen", Checked = enabled, AutoSize = true });
        _grid = Full(new DataGridView
        {
            Size = new Size(640, 240),
            AllowUserToAddRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = SystemColors.Window,
            EditMode = DataGridViewEditMode.EditOnEnter,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Word or pattern", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Colour (click)", Width = 100, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Whole line", Width = 80 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Regex", Width = 60 });
        foreach (var r in rules) AddRow(r);
        _grid.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == ColColor) PickColor(_grid.Rows[e.RowIndex]);
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        buttons.Controls.AddRange(
        [
            SmallButton("Add", () =>
            {
                AddRow(new HighlightRule { Color = "#FF5555" });
                _grid.CurrentCell = _grid.Rows[^1].Cells[ColPattern];
                _grid.BeginEdit(true);
            }),
            SmallButton("Remove", () =>
            {
                if (_grid.CurrentRow != null) _grid.Rows.Remove(_grid.CurrentRow);
            }),
            SmallButton("Restore defaults", () =>
            {
                _grid.Rows.Clear();
                foreach (var r in HighlightRule.Defaults()) AddRow(r);
            }),
        ]);
        Full(buttons);
        Full(new Label
        {
            Text = "Plain words match whole words and ignore upper/lower case: FAIL also matches \"fail\" but not \"failover\".\n" +
                   "Rules higher in the list win. Tick \"Whole line\" to colour the entire line that contains the word.\n" +
                   "Only the display changes: copied text and log files keep the original text.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });
        AddButtons();
    }

    void AddRow(HighlightRule r)
    {
        int i = _grid.Rows.Add(r.Pattern, r.Color, r.WholeLine, r.IsRegex);
        PaintSwatch(_grid.Rows[i], r.Color);
    }

    static void PaintSwatch(DataGridViewRow row, string hex)
    {
        var color = AppSettings.ParseColor(hex, Color.Red);
        var cell = row.Cells[ColColor];
        cell.Value = hex;
        cell.Style.BackColor = cell.Style.SelectionBackColor = color;
        cell.Style.ForeColor = cell.Style.SelectionForeColor = color.GetBrightness() > 0.5 ? Color.Black : Color.White;
    }

    void PickColor(DataGridViewRow row)
    {
        using var cd = new ColorDialog { FullOpen = true, Color = AppSettings.ParseColor(row.Cells[ColColor].Value as string ?? "", Color.Red) };
        if (cd.ShowDialog(this) == DialogResult.OK) PaintSwatch(row, AppSettings.ToHex(cd.Color));
    }

    protected override bool ValidateInput()
    {
        _grid.EndEdit();
        var list = new List<HighlightRule>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var rule = new HighlightRule
            {
                Pattern = (row.Cells[ColPattern].Value as string ?? "").Trim(),
                Color = row.Cells[ColColor].Value as string ?? "#FF5555",
                WholeLine = row.Cells[ColWholeLine].Value is true,
                IsRegex = row.Cells[ColRegex].Value is true,
            };
            if (rule.Pattern.Length == 0) continue;
            try { rule.ToRegex(); }
            catch (ArgumentException ex)
            {
                _grid.CurrentCell = row.Cells[ColPattern];
                return Fail($"\"{rule.Pattern}\" is not a valid regular expression:\n{ex.Message}\n\nUntick \"Regex\" to match it as plain text.", _grid);
            }
            list.Add(rule);
        }
        Rules = list;
        return true;
    }
}
