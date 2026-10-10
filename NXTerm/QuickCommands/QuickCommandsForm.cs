using System.Text.Json;
using NXTerm.Dialogs;

namespace NXTerm.QuickCommands;

public enum QuickMode { Hidden, Docked, Floating }

/// <summary>Tabs of command buttons. Hosted either at the bottom of the main window or in a <see cref="QuickCommandsForm"/>.</summary>
public sealed class QuickCommandsPanel : UserControl
{
    readonly QuickCommandSet _set;
    readonly Action<QuickCommand> _execute;
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Multiline = true };
    readonly ToolStrip _tools = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(2, 0, 2, 0) };
    readonly ToolTip _tip = new() { InitialDelay = 400, AutoPopDelay = 15000 };
    readonly ContextMenuStrip _tabMenu = new(), _cmdMenu = new();
    int _menuGroup = -1;

    public readonly ToolStripButton DockButton = new() { Alignment = ToolStripItemAlignment.Right };
    public readonly ToolStripButton CloseButton = new("✕") { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Hide quick commands (Alt+K)" };
    public readonly ToolStripButton OnTopButton = new("Always on top") { CheckOnClick = true, Alignment = ToolStripItemAlignment.Right, ToolTipText = "Keep this window above all other programs" };

    /// <summary>Raised after any change was saved, so the main window can refresh its menu.</summary>
    public event Action? CommandsChanged;

    public int SelectedGroup => _tabs.SelectedIndex;

    public QuickCommandsPanel(QuickCommandSet set, int selectedGroup, Action<QuickCommand> execute)
    {
        _set = set;
        _execute = execute;
        Font = SystemFonts.MessageBoxFont;

        _tools.Items.Add(new ToolStripLabel("Quick commands") { Font = new Font(Font, FontStyle.Bold) });
        _tools.Items.Add(new ToolStripSeparator());
        _tools.Items.Add(new ToolStripButton("+ Group", null, (_, _) => AddGroup()) { ToolTipText = "Add a new tab" });
        _tools.Items.Add(new ToolStripButton("+ Command", null, (_, _) => AddCommand(SelectedGroup)) { ToolTipText = "Add a command to the current tab" });
        _tools.Items.Add(new ToolStripSeparator());
        _tools.Items.Add(new ToolStripButton("Import...", null, (_, _) => Import()));
        _tools.Items.Add(new ToolStripButton("Export...", null, (_, _) => Export()));
        _tools.Items.Add(CloseButton);
        _tools.Items.Add(DockButton);
        _tools.Items.Add(OnTopButton);

        _tabMenu.Items.AddRange(
        [
            new ToolStripMenuItem("+ Command...", null, (_, _) => AddCommand(_menuGroup)),
            new ToolStripMenuItem("Rename group...", null, (_, _) => RenameGroup(_menuGroup)),
            new ToolStripMenuItem("Move left", null, (_, _) => MoveGroup(_menuGroup, -1)),
            new ToolStripMenuItem("Move right", null, (_, _) => MoveGroup(_menuGroup, 1)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Delete group", null, (_, _) => DeleteGroup(_menuGroup)),
        ]);
        _cmdMenu.Items.AddRange(
        [
            new ToolStripMenuItem("Send", null, (_, _) => WithCommand((g, i) => _execute(_set.Groups[g].Commands[i]))),
            new ToolStripMenuItem("Edit...", null, (_, _) => WithCommand(EditCommand)),
            new ToolStripMenuItem("Duplicate", null, (_, _) => WithCommand(DuplicateCommand)),
            new ToolStripMenuItem("Move left", null, (_, _) => WithCommand((g, i) => MoveCommand(g, i, -1))),
            new ToolStripMenuItem("Move right", null, (_, _) => WithCommand((g, i) => MoveCommand(g, i, 1))),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Delete", null, (_, _) => WithCommand(DeleteCommand)),
        ]);
        _tabs.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            _menuGroup = TabAt(e.Location);
            if (_menuGroup >= 0) _tabMenu.Show(_tabs, e.Location);
        };
        _tabs.MouseDoubleClick += (_, e) =>
        {
            int g = TabAt(e.Location);
            if (g >= 0) RenameGroup(g);
        };

        Controls.Add(_tabs);
        Controls.Add(_tools);
        Rebuild(selectedGroup);
    }

    IWin32Window? Owner => FindForm();

    int TabAt(Point p)
    {
        for (int i = 0; i < _tabs.TabCount; i++)
            if (_tabs.GetTabRect(i).Contains(p)) return i < _set.Groups.Count ? i : -1;
        return -1;
    }

    void WithCommand(Action<int, int> action)
    {
        if (_cmdMenu.SourceControl?.Tag is (int g, int i)) action(g, i);
    }

    // ------------------------------------------------------------------ layout

    void Rebuild(int select)
    {
        _tabs.SuspendLayout();
        foreach (TabPage p in _tabs.TabPages) p.Dispose();
        _tabs.TabPages.Clear();
        for (int g = 0; g < _set.Groups.Count; g++)
        {
            var group = _set.Groups[g];
            var page = new TabPage(group.Name) { UseVisualStyleBackColor = true, Padding = new Padding(2) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
            for (int i = 0; i < group.Commands.Count; i++) flow.Controls.Add(MakeButton(g, i, group.Commands[i]));
            int gi = g;
            var add = new Button { Text = "+", Size = new Size(34, 30), Margin = new Padding(3) };
            add.Click += (_, _) => AddCommand(gi);
            _tip.SetToolTip(add, "Add a command to this group");
            flow.Controls.Add(add);
            page.Controls.Add(flow);
            _tabs.TabPages.Add(page);
        }
        if (_set.Groups.Count == 0)
        {
            var page = new TabPage("(empty)") { UseVisualStyleBackColor = true };
            page.Controls.Add(new Label { Text = "Click \"+ Group\" to create your first group of commands.", AutoSize = true, Location = new Point(12, 12) });
            _tabs.TabPages.Add(page);
        }
        _tabs.SelectedIndex = Math.Clamp(select, 0, _tabs.TabCount - 1);
        _tabs.ResumeLayout();
    }

    Button MakeButton(int g, int i, QuickCommand c)
    {
        var b = new Button
        {
            Text = c.Name,
            UseMnemonic = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(72, 30),
            Margin = new Padding(3),
            Padding = new Padding(6, 0, 6, 0),
            Tag = (g, i),
            ContextMenuStrip = _cmdMenu,
        };
        if (c.Color.Length > 0)
        {
            var col = AppSettings.ParseColor(c.Color, SystemColors.Control);
            b.BackColor = col;
            b.ForeColor = col.GetBrightness() > 0.5 ? Color.Black : Color.White;
            b.UseVisualStyleBackColor = false;
        }
        b.Click += (_, _) => _execute(c);
        var preview = c.Text.Length > 400 ? c.Text[..400] + "…" : c.Text;
        _tip.SetToolTip(b, (i < 9 ? $"[Alt+{i + 1}]  " : "") + (c.Kind == QuickCommandKind.Macro ? "macro" : "send") +
                           (c.Confirm ? " (asks first)" : "") + "\n" + preview + "\n\nRight click to edit");
        return b;
    }

    // ------------------------------------------------------------------ editing

    void Changed(int select)
    {
        try { _set.Save(QuickCommandSet.DefaultPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Owner, "Cannot save quick commands:\n" + ex.Message, "NXTerm", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        Rebuild(select);
        CommandsChanged?.Invoke();
    }

    IList<string> GroupNames => _set.Groups.Select(g => g.Name).ToList();

    public void AddGroup()
    {
        var name = InputDialog.Show(Owner, "New group", "Group (tab) name:", $"Group {_set.Groups.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        _set.Groups.Add(new QuickCommandGroup { Name = name.Trim() });
        Changed(_set.Groups.Count - 1);
    }

    void RenameGroup(int g)
    {
        if (g < 0 || g >= _set.Groups.Count) return;
        var name = InputDialog.Show(Owner, "Rename group", "Group (tab) name:", _set.Groups[g].Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        _set.Groups[g].Name = name.Trim();
        Changed(g);
    }

    void MoveGroup(int g, int delta)
    {
        int to = g + delta;
        if (g < 0 || to < 0 || to >= _set.Groups.Count) return;
        (_set.Groups[g], _set.Groups[to]) = (_set.Groups[to], _set.Groups[g]);
        Changed(to);
    }

    void DeleteGroup(int g)
    {
        if (g < 0 || g >= _set.Groups.Count) return;
        var group = _set.Groups[g];
        if (group.Commands.Count > 0 &&
            MessageBox.Show(Owner, $"Delete group \"{group.Name}\" and its {group.Commands.Count} command(s)?", "NXTerm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        _set.Groups.RemoveAt(g);
        Changed(g - 1);
    }

    public void AddCommand(int g)
    {
        if (_set.Groups.Count == 0) _set.Groups.Add(new QuickCommandGroup { Name = "General" });
        g = Math.Clamp(g, 0, _set.Groups.Count - 1);
        using var dlg = new QuickCommandDialog(new QuickCommand(), GroupNames, g, isNew: true);
        if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
        _set.Groups[dlg.GroupIndex].Commands.Add(dlg.Result);
        Changed(dlg.GroupIndex);
    }

    void EditCommand(int g, int i)
    {
        using var dlg = new QuickCommandDialog(_set.Groups[g].Commands[i].Clone(), GroupNames, g, isNew: false);
        if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
        if (dlg.GroupIndex == g) _set.Groups[g].Commands[i] = dlg.Result;
        else
        {
            _set.Groups[g].Commands.RemoveAt(i);
            _set.Groups[dlg.GroupIndex].Commands.Add(dlg.Result);
        }
        Changed(dlg.GroupIndex);
    }

    void DuplicateCommand(int g, int i)
    {
        var copy = _set.Groups[g].Commands[i].Clone();
        copy.Name += " (copy)";
        _set.Groups[g].Commands.Insert(i + 1, copy);
        Changed(g);
    }

    void MoveCommand(int g, int i, int delta)
    {
        var list = _set.Groups[g].Commands;
        int to = i + delta;
        if (to < 0 || to >= list.Count) return;
        (list[i], list[to]) = (list[to], list[i]);
        Changed(g);
    }

    void DeleteCommand(int g, int i)
    {
        var c = _set.Groups[g].Commands[i];
        if (MessageBox.Show(Owner, $"Delete \"{c.Name}\"?", "NXTerm", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        _set.Groups[g].Commands.RemoveAt(i);
        Changed(g);
    }

    void Import()
    {
        using var ofd = new OpenFileDialog { Title = "Import quick commands", Filter = "Quick commands (*.json)|*.json|All files (*.*)|*.*" };
        if (ofd.ShowDialog(Owner) != DialogResult.OK) return;
        QuickCommandSet loaded;
        try { loaded = QuickCommandSet.Load(ofd.FileName); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            MessageBox.Show(Owner, "Cannot read file:\n" + ex.Message, "NXTerm", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        var answer = MessageBox.Show(Owner,
            $"The file has {loaded.Groups.Count} group(s).\n\nYes = add them to your groups\nNo = replace all your groups",
            "NXTerm", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return;
        if (answer == DialogResult.No) _set.Groups.Clear();
        int first = _set.Groups.Count;
        _set.Groups.AddRange(loaded.Groups);
        Changed(first);
    }

    void Export()
    {
        using var sfd = new SaveFileDialog { Title = "Export quick commands", Filter = "Quick commands (*.json)|*.json", FileName = "QuickCommands.json" };
        if (sfd.ShowDialog(Owner) != DialogResult.OK) return;
        try { _set.Save(sfd.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Owner, "Cannot save file:\n" + ex.Message, "NXTerm", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

/// <summary>Floating window that hosts the quick commands panel.</summary>
public sealed class QuickCommandsForm : Form
{
    readonly Action<int> _runIndex;

    public QuickCommandsForm(Action<int> runIndex)
    {
        _runIndex = runIndex;
        Text = "NXTerm: Quick commands";
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(560, 220);
        MinimumSize = new Size(320, 140);
    }

    /// <summary>Raised when the user closes the window; the owner decides what that means.</summary>
    public event Action? CloseRequested;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if ((keyData & Keys.Modifiers) == Keys.Alt && key >= Keys.D1 && key <= Keys.D9)
        {
            _runIndex(key - Keys.D1);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            CloseRequested?.Invoke();
        }
        base.OnFormClosing(e);
    }
}
