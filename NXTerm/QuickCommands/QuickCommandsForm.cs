using System.Text.Json;
using NXTerm.Dialogs;

namespace NXTerm.QuickCommands;

/// <summary>Where the quick command buttons are shown. Values are stored by name in the settings file.</summary>
public enum QuickMode { Hidden, Docked, Floating, DockedTop }

/// <summary>
/// Quick command buttons in three levels: a group (chosen in the toolbar), its tabs, and the command buttons.
/// Hosted under or above the terminal in the main window, or in a <see cref="QuickCommandsForm"/>.
/// </summary>
public sealed class QuickCommandsPanel : UserControl
{
    readonly QuickCommandSet _set;
    readonly Action<QuickCommand> _execute;
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Multiline = true };
    readonly ToolStrip _tools = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(2, 0, 2, 0) };
    readonly ToolStripComboBox _groupBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 170, ToolTipText = "Group of tabs. Right click to rename, move or delete it." };
    readonly ToolTip _tip = new() { InitialDelay = 400, AutoPopDelay = 15000 };
    readonly ContextMenuStrip _groupMenu = new(), _tabMenu = new(), _cmdMenu = new();
    int _group, _menuTab = -1;
    bool _filling;

    public readonly ToolStripButton DockButton = new() { Alignment = ToolStripItemAlignment.Right };
    public readonly ToolStripButton CloseButton = new("✕") { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Hide quick commands (Alt+K)" };
    public readonly ToolStripButton OnTopButton = new("Always on top") { CheckOnClick = true, Alignment = ToolStripItemAlignment.Right, ToolTipText = "Keep this window above all other programs" };

    /// <summary>Raised after any change was saved, so the main window can refresh its menu.</summary>
    public event Action? CommandsChanged;

    /// <summary>Index of the group shown, or -1 when there is none.</summary>
    public int SelectedGroup => _set.Groups.Count == 0 ? -1 : _group;

    /// <summary>Index of the tab shown in the current group, or -1 when there is none.</summary>
    public int SelectedTab => Tabs.Count == 0 ? -1 : _tabs.SelectedIndex;

    QuickCommandGroup? Group => _group >= 0 && _group < _set.Groups.Count ? _set.Groups[_group] : null;
    List<QuickCommandTab> Tabs => Group?.Tabs ?? [];

    public QuickCommandsPanel(QuickCommandSet set, int selectedGroup, int selectedTab, Action<QuickCommand> execute)
    {
        _set = set;
        _execute = execute;
        Font = SystemFonts.MessageBoxFont;

        _tools.Items.Add(new ToolStripLabel("Group:") { Font = new Font(Font, FontStyle.Bold) });
        _tools.Items.Add(_groupBox);
        _tools.Items.Add(new ToolStripSeparator());
        _tools.Items.Add(new ToolStripButton("+ Group", null, (_, _) => AddGroup()) { ToolTipText = "Add a new group of tabs" });
        _tools.Items.Add(new ToolStripButton("+ Tab", null, (_, _) => AddTab()) { ToolTipText = "Add a new tab to this group" });
        _tools.Items.Add(new ToolStripButton("+ Command", null, (_, _) => AddCommand(SelectedTab)) { ToolTipText = "Add a command to the current tab" });
        _tools.Items.Add(new ToolStripSeparator());
        _tools.Items.Add(new ToolStripButton("Import...", null, (_, _) => Import()));
        _tools.Items.Add(new ToolStripButton("Export...", null, (_, _) => Export()));
        _tools.Items.Add(CloseButton);
        _tools.Items.Add(DockButton);
        _tools.Items.Add(OnTopButton);

        _groupBox.SelectedIndexChanged += (_, _) =>
        {
            if (_filling || _groupBox.SelectedIndex < 0) return;
            _group = _groupBox.SelectedIndex;
            Rebuild(0);
            CommandsChanged?.Invoke();
        };
        _groupMenu.Items.AddRange(
        [
            new ToolStripMenuItem("Rename group...", null, (_, _) => RenameGroup()),
            new ToolStripMenuItem("Move up", null, (_, _) => MoveGroup(-1)),
            new ToolStripMenuItem("Move down", null, (_, _) => MoveGroup(1)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Delete group", null, (_, _) => DeleteGroup()),
        ]);
        _groupBox.ComboBox.ContextMenuStrip = _groupMenu;

        _tabMenu.Items.AddRange(
        [
            new ToolStripMenuItem("+ Command...", null, (_, _) => AddCommand(_menuTab)),
            new ToolStripMenuItem("Edit tab (name, group)...", null, (_, _) => EditTab(_menuTab)),
            new ToolStripMenuItem("Move left", null, (_, _) => MoveTab(_menuTab, -1)),
            new ToolStripMenuItem("Move right", null, (_, _) => MoveTab(_menuTab, 1)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Delete tab", null, (_, _) => DeleteTab(_menuTab)),
        ]);
        _cmdMenu.Items.AddRange(
        [
            new ToolStripMenuItem("Send", null, (_, _) => WithCommand((t, i) => _execute(Tabs[t].Commands[i]))),
            new ToolStripMenuItem("Edit (also move to another group or tab)...", null, (_, _) => WithCommand(EditCommand)),
            new ToolStripMenuItem("Duplicate", null, (_, _) => WithCommand(DuplicateCommand)),
            new ToolStripMenuItem("Move left", null, (_, _) => WithCommand((t, i) => MoveCommand(t, i, -1))),
            new ToolStripMenuItem("Move right", null, (_, _) => WithCommand((t, i) => MoveCommand(t, i, 1))),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Delete", null, (_, _) => WithCommand(DeleteCommand)),
        ]);
        _tabs.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            _menuTab = TabAt(e.Location);
            if (_menuTab >= 0) _tabMenu.Show(_tabs, e.Location);
        };
        _tabs.MouseDoubleClick += (_, e) =>
        {
            int t = TabAt(e.Location);
            if (t >= 0) EditTab(t);
        };

        Controls.Add(_tabs);
        Controls.Add(_tools);
        _group = Math.Clamp(selectedGroup, 0, Math.Max(0, _set.Groups.Count - 1));
        Rebuild(selectedTab);
    }

    IWin32Window? Owner => FindForm();

    int TabAt(Point p)
    {
        for (int i = 0; i < _tabs.TabCount; i++)
            if (_tabs.GetTabRect(i).Contains(p)) return i < Tabs.Count ? i : -1;
        return -1;
    }

    void WithCommand(Action<int, int> action)
    {
        if (_cmdMenu.SourceControl?.Tag is (int t, int i)) action(t, i);
    }

    // ------------------------------------------------------------------ layout

    void FillGroupBox()
    {
        _filling = true;
        _groupBox.Items.Clear();
        foreach (var g in _set.Groups) _groupBox.Items.Add(g.Name);
        if (_set.Groups.Count > 0) _groupBox.SelectedIndex = Math.Clamp(_group, 0, _set.Groups.Count - 1);
        _filling = false;
    }

    void Rebuild(int selectTab)
    {
        if (_set.Groups.Count > 0) _group = Math.Clamp(_group, 0, _set.Groups.Count - 1);
        FillGroupBox();
        _tabs.SuspendLayout();
        foreach (TabPage p in _tabs.TabPages) p.Dispose();
        _tabs.TabPages.Clear();
        var tabs = Tabs;
        for (int t = 0; t < tabs.Count; t++)
        {
            var tab = tabs[t];
            var page = new TabPage(tab.Name) { UseVisualStyleBackColor = true, Padding = new Padding(2) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
            for (int i = 0; i < tab.Commands.Count; i++) flow.Controls.Add(MakeButton(t, i, tab.Commands[i]));
            int ti = t;
            var add = new Button { Text = "+", Size = new Size(34, 30), Margin = new Padding(3) };
            add.Click += (_, _) => AddCommand(ti);
            _tip.SetToolTip(add, "Add a command to this tab");
            flow.Controls.Add(add);
            page.Controls.Add(flow);
            _tabs.TabPages.Add(page);
        }
        if (tabs.Count == 0)
        {
            var page = new TabPage("(empty)") { UseVisualStyleBackColor = true };
            page.Controls.Add(new Label
            {
                Text = _set.Groups.Count == 0
                    ? "Click \"+ Group\" to create your first group, then \"+ Tab\" and \"+ Command\"."
                    : "Click \"+ Tab\" to add the first tab to this group.",
                AutoSize = true,
                Location = new Point(12, 12),
            });
            _tabs.TabPages.Add(page);
        }
        _tabs.SelectedIndex = Math.Clamp(selectTab, 0, _tabs.TabCount - 1);
        _tabs.ResumeLayout();
    }

    Button MakeButton(int t, int i, QuickCommand c)
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
            Tag = (t, i),
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

    void Changed(int selectTab)
    {
        try { _set.Save(QuickCommandSet.DefaultPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Owner, "Cannot save quick commands:\n" + ex.Message, "NXTerm", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        Rebuild(selectTab);
        CommandsChanged?.Invoke();
    }

    public void AddGroup()
    {
        var name = InputDialog.Show(Owner, "New group", "Group name (for example a product or device):", $"Group {_set.Groups.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        _set.Groups.Add(new QuickCommandGroup { Name = name.Trim(), Tabs = [new QuickCommandTab { Name = "General" }] });
        _group = _set.Groups.Count - 1;
        Changed(0);
    }

    void RenameGroup()
    {
        if (Group is not { } g) return;
        var name = InputDialog.Show(Owner, "Rename group", "Group name:", g.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        g.Name = name.Trim();
        Changed(SelectedTab);
    }

    void MoveGroup(int delta)
    {
        int to = _group + delta;
        if (Group == null || to < 0 || to >= _set.Groups.Count) return;
        (_set.Groups[_group], _set.Groups[to]) = (_set.Groups[to], _set.Groups[_group]);
        _group = to;
        Changed(SelectedTab);
    }

    void DeleteGroup()
    {
        if (Group is not { } g) return;
        int count = g.Tabs.Sum(t => t.Commands.Count);
        if (MessageBox.Show(Owner, $"Delete group \"{g.Name}\" with its {g.Tabs.Count} tab(s) and {count} command(s)?", "NXTerm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        _set.Groups.RemoveAt(_group);
        _group = Math.Max(0, _group - 1);
        Changed(0);
    }

    public void AddTab()
    {
        if (_set.Groups.Count == 0)
        {
            AddGroup();
            return;
        }
        var name = InputDialog.Show(Owner, "New tab", "Tab name:", $"Tab {Tabs.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        Group!.Tabs.Add(new QuickCommandTab { Name = name.Trim() });
        Changed(Tabs.Count - 1);
    }

    /// <summary>Renames a tab, or moves it with all its buttons to another (or a new) group.</summary>
    void EditTab(int t)
    {
        if (t < 0 || t >= Tabs.Count) return;
        var tab = Tabs[t];
        using var dlg = new QuickTabDialog(_set, tab.Name, _group);
        if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
        tab.Name = dlg.TabName;
        int target = _set.GroupIndex(dlg.GroupName);
        if (target == _group)
        {
            Changed(t);
            return;
        }
        Group!.Tabs.Remove(tab);
        _set.Groups[target].Tabs.Add(tab);
        _group = target;  // show the tab where it went
        Changed(_set.Groups[target].Tabs.Count - 1);
    }

    void MoveTab(int t, int delta)
    {
        var tabs = Tabs;
        int to = t + delta;
        if (t < 0 || to < 0 || to >= tabs.Count) return;
        (tabs[t], tabs[to]) = (tabs[to], tabs[t]);
        Changed(to);
    }

    void DeleteTab(int t)
    {
        if (t < 0 || t >= Tabs.Count) return;
        var tab = Tabs[t];
        if (tab.Commands.Count > 0 &&
            MessageBox.Show(Owner, $"Delete tab \"{tab.Name}\" and its {tab.Commands.Count} command(s)?", "NXTerm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        Tabs.RemoveAt(t);
        Changed(t - 1);
    }

    public void AddCommand(int t)
    {
        using var dlg = new QuickCommandDialog(new QuickCommand(), _set, SelectedGroup, Math.Max(0, t), isNew: true);
        if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
        var (g2, t2) = _set.TabIndex(dlg.TargetGroup, dlg.TargetTab);
        _set.Groups[g2].Tabs[t2].Commands.Add(dlg.Result);
        _group = g2;
        Changed(t2);
    }

    /// <summary>Edits a button; choosing another group or tab in the dialog moves it there (to the end).</summary>
    void EditCommand(int t, int i)
    {
        var tab = Tabs[t];
        using var dlg = new QuickCommandDialog(tab.Commands[i].Clone(), _set, _group, t, isNew: false);
        if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
        var (g2, t2) = _set.TabIndex(dlg.TargetGroup, dlg.TargetTab);
        var target = _set.Groups[g2].Tabs[t2];
        if (target == tab) tab.Commands[i] = dlg.Result;
        else
        {
            tab.Commands.RemoveAt(i);
            target.Commands.Add(dlg.Result);
        }
        _group = g2;  // show the button where it went
        Changed(t2);
    }

    void DuplicateCommand(int t, int i)
    {
        var copy = Tabs[t].Commands[i].Clone();
        copy.Name += " (copy)";
        Tabs[t].Commands.Insert(i + 1, copy);
        Changed(t);
    }

    void MoveCommand(int t, int i, int delta)
    {
        var list = Tabs[t].Commands;
        int to = i + delta;
        if (to < 0 || to >= list.Count) return;
        (list[i], list[to]) = (list[to], list[i]);
        Changed(t);
    }

    void DeleteCommand(int t, int i)
    {
        var c = Tabs[t].Commands[i];
        if (MessageBox.Show(Owner, $"Delete \"{c.Name}\"?", "NXTerm", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        Tabs[t].Commands.RemoveAt(i);
        Changed(t);
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
        _group = Math.Min(first, Math.Max(0, _set.Groups.Count - 1));
        Changed(0);
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
