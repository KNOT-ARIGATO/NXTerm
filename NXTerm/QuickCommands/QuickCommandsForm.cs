using System.Text.Json;
using NXTerm.Dialogs;

namespace NXTerm.QuickCommands;

public enum QuickMode { Hidden, Docked, Floating }

/// <summary>
/// Groups as tabs and their command buttons. Hosted under the terminal in the main window or in a
/// <see cref="QuickCommandsForm"/>. One row of tabs on top, the buttons of the chosen group below;
/// everything that changes the commands is in the "Manage" menu or on right click, so daily use is just clicking.
/// </summary>
public sealed class QuickCommandsPanel : UserControl
{
    const int MaxButtonRows = 3;  // docked: taller groups scroll instead of eating the terminal

    readonly QuickCommandSet _set;
    readonly Action<QuickCommand> _execute;
    readonly ToolStrip _bar = new()
    {
        GripStyle = ToolStripGripStyle.Hidden,
        Padding = new Padding(4, 2, 2, 0),
        Dock = DockStyle.Top,
        CanOverflow = true,
        ShowItemToolTips = true,
    };
    readonly FlowLayoutPanel _flow = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(3, 2, 3, 3) };
    readonly ToolStripLabel _notConnected = new("Not connected: buttons will not send")
    {
        Alignment = ToolStripItemAlignment.Right,
        ForeColor = Color.Firebrick,
        Visible = false,
    };
    readonly ToolStripDropDownButton _more = new("Manage") { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Add commands or groups, import, export, separate window" };
    readonly ToolStripButton _hide = new("✕") { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Hide quick commands (Alt+K)" };
    readonly ToolStripMenuItem _miDock = new(), _miOnTop = new("Always on top") { CheckOnClick = true };
    readonly ToolTip _tip = new() { InitialDelay = 300, AutoPopDelay = 15000 };
    readonly ContextMenuStrip _tabMenu = new(), _cmdMenu = new();
    readonly Font _badgeFont;
    int _group, _menuGroup = -1;
    bool _fitting;

    /// <summary>Raised after any change was saved, so the main window can refresh its menu.</summary>
    public event Action? CommandsChanged;
    public event Action? HideRequested, DockToggleRequested;
    public event Action<bool>? OnTopChanged;

    public int SelectedGroup => _set.Groups.Count == 0 ? -1 : _group;

    public QuickCommandsPanel(QuickCommandSet set, int selectedGroup, Action<QuickCommand> execute)
    {
        _set = set;
        _execute = execute;
        Font = SystemFonts.MessageBoxFont;
        _badgeFont = new Font(Font.FontFamily, Font.Size * 0.75f);
        _bar.Renderer = new TabRenderer();

        _miDock.Click += (_, _) => DockToggleRequested?.Invoke();
        _miOnTop.CheckedChanged += (_, _) => OnTopChanged?.Invoke(_miOnTop.Checked);
        _more.DropDownItems.AddRange(
        [
            new ToolStripMenuItem("Add command...", null, (_, _) => AddCommand(SelectedGroup)),
            new ToolStripMenuItem("Add group (tab)...", null, (_, _) => AddGroup()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Import...", null, (_, _) => Import()),
            new ToolStripMenuItem("Export...", null, (_, _) => Export()),
            new ToolStripSeparator(),
            _miDock,
            _miOnTop,
            new ToolStripMenuItem("Hide", null, (_, _) => HideRequested?.Invoke()) { ShortcutKeyDisplayString = "Alt+K" },
        ]);
        _hide.Click += (_, _) => HideRequested?.Invoke();

        _tabMenu.Items.AddRange(
        [
            new ToolStripMenuItem("Add command...", null, (_, _) => AddCommand(_menuGroup)),
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

        Controls.Add(_flow);
        Controls.Add(_bar);
        _flow.ClientSizeChanged += (_, _) => FitHeight();
        _group = Math.Max(0, selectedGroup);
        Rebuild(_group);
        SetMode(QuickMode.Docked);
    }

    IWin32Window? Owner => FindForm();

    /// <summary>Updates the "Manage" menu for where the panel is shown.</summary>
    public void SetMode(QuickMode mode)
    {
        _miDock.Text = mode == QuickMode.Floating ? "Dock under the terminal" : "Move to a separate window";
        _miOnTop.Visible = mode == QuickMode.Floating;
        _hide.Visible = mode != QuickMode.Floating;  // the floating window has its own close button
        FitHeight();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool OnTop
    {
        get => _miOnTop.Checked;
        set => _miOnTop.Checked = value;
    }

    /// <summary>Shows a warning in the tab row while nothing is connected, so a click that sends nothing is no surprise.</summary>
    public void SetConnected(bool connected) => _notConnected.Visible = !connected;

    void WithCommand(Action<int, int> action)
    {
        if (_cmdMenu.SourceControl?.Tag is (int g, int i)) action(g, i);
    }

    public void SelectGroup(int g)
    {
        if (_set.Groups.Count == 0) return;
        g = Math.Clamp(g, 0, _set.Groups.Count - 1);
        if (g == _group && _flow.Controls.Count > 0) return;
        _group = g;
        foreach (ToolStripItem item in _bar.Items)
            if (item is ToolStripButton { Tag: int tg } tab) tab.Checked = tg == g;
        FillButtons();
    }

    // ------------------------------------------------------------------ layout

    void Rebuild(int select)
    {
        _bar.SuspendLayout();
        _bar.Items.Clear();
        _group = _set.Groups.Count == 0 ? 0 : Math.Clamp(select, 0, _set.Groups.Count - 1);
        for (int g = 0; g < _set.Groups.Count; g++)
        {
            int gi = g;
            var tab = new ToolStripButton(_set.Groups[g].Name.Replace("&", "&&"))
            {
                Tag = g,
                Checked = g == _group,
                Padding = new Padding(8, 2, 8, 2),
                ToolTipText = "Right click: rename, move or delete this group",
            };
            tab.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Right) ShowTabMenu(gi, tab);
            };
            tab.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) SelectGroup(gi);
            };
            tab.DoubleClick += (_, _) => RenameGroup(gi);
            tab.DoubleClickEnabled = true;
            _bar.Items.Add(tab);
        }
        _bar.Items.Add(new ToolStripButton("+", null, (_, _) => AddGroup()) { ToolTipText = "Add a group (tab)" });
        _bar.Items.Add(_hide);
        _bar.Items.Add(_more);
        _bar.Items.Add(_notConnected);
        _bar.ResumeLayout();
        FillButtons();
    }

    void ShowTabMenu(int g, ToolStripItem tab)
    {
        _menuGroup = g;
        _tabMenu.Show(_bar, new Point(tab.Bounds.Left, tab.Bounds.Bottom));
    }

    void FillButtons()
    {
        _flow.SuspendLayout();
        foreach (Control c in _flow.Controls.Cast<Control>().ToList()) c.Dispose();
        _flow.Controls.Clear();
        if (_set.Groups.Count == 0)
        {
            _flow.Controls.Add(Hint("No quick commands yet."));
            _flow.Controls.Add(AddButton("+ Add group", AddGroup));
        }
        else
        {
            var group = _set.Groups[_group];
            for (int i = 0; i < group.Commands.Count; i++) _flow.Controls.Add(MakeButton(_group, i, group.Commands[i]));
            if (group.Commands.Count == 0) _flow.Controls.Add(Hint("This group is empty."));
            int gi = _group;
            _flow.Controls.Add(AddButton("+ Add command", () => AddCommand(gi)));
        }
        _flow.ResumeLayout();
        FitHeight();
    }

    Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(6, 10, 6, 3),
    };

    Button AddButton(string text, Action onClick)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 30),
            Margin = new Padding(3),
            FlatStyle = FlatStyle.Flat,
            ForeColor = SystemColors.GrayText,
        };
        b.FlatAppearance.BorderColor = SystemColors.ControlDark;
        b.Click += (_, _) => onClick();
        return b;
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
            Padding = new Padding(i < 9 ? 12 : 6, 0, 6, 0),
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
        if (i < 9)
        {
            // Small Alt+number badge in the corner, so the keyboard shortcut is visible without a tooltip.
            string badge = (i + 1).ToString();
            b.Paint += (_, e) =>
                TextRenderer.DrawText(e.Graphics, badge, _badgeFont, new Point(4, 3),
                    b.UseVisualStyleBackColor ? SystemColors.GrayText : Color.FromArgb(150, b.ForeColor));
        }
        b.Click += (_, _) =>
        {
            Flash(b);
            _execute(c);
        };
        var preview = c.Text.Length > 400 ? c.Text[..400] + "…" : c.Text;
        var what = c.Kind == QuickCommandKind.Macro ? "Runs macro" : c.AppendEnter ? "Sends, then Enter" : "Sends (no Enter)";
        _tip.SetToolTip(b, $"{what}{(c.Confirm ? ", asks first" : "")}:\n{preview}\n\n" +
                           (i < 9 ? $"Shortcut: Alt+{i + 1}\n" : "") + "Right click to edit, move or delete");
        return b;
    }

    /// <summary>Briefly marks a clicked button, so it is clear the click was taken.</summary>
    static void Flash(Button b)
    {
        var (back, fore, visual) = (b.BackColor, b.ForeColor, b.UseVisualStyleBackColor);
        b.BackColor = SystemColors.Highlight;
        b.ForeColor = SystemColors.HighlightText;
        var t = new System.Windows.Forms.Timer { Interval = 180 };
        t.Tick += (_, _) =>
        {
            t.Dispose();
            if (b.IsDisposed) return;
            b.BackColor = back;
            b.ForeColor = fore;
            b.UseVisualStyleBackColor = visual;
        };
        t.Start();
    }

    /// <summary>
    /// Docked: tall enough for the buttons of the fullest group (up to <see cref="MaxButtonRows"/> rows), so there is
    /// no empty space and the terminal does not change size when another tab is picked.
    /// </summary>
    void FitHeight()
    {
        if (_fitting || Dock != DockStyle.Bottom || _flow.ClientSize.Width <= 0) return;
        _fitting = true;
        try
        {
            const int rowHeight = 36;  // 30 px button + margins
            int rows = 1;
            for (int g = 0; g < _set.Groups.Count; g++) rows = Math.Max(rows, RowsNeeded(_set.Groups[g]));
            int want = _bar.Height + Math.Min(rows, MaxButtonRows) * rowHeight + _flow.Padding.Vertical;
            if (Height != want) Height = want;
        }
        finally { _fitting = false; }
    }

    readonly Button _measure = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, UseMnemonic = false };

    /// <summary>How many rows the buttons of <paramref name="group"/> take at the current width (they wrap like the real ones).</summary>
    int RowsNeeded(QuickCommandGroup group)
    {
        int width = _flow.ClientSize.Width - _flow.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        int rows = 1, x = 0;
        void Place(int w)
        {
            w += 6;  // margins
            if (x > 0 && x + w > width) { rows++; x = 0; }
            x += w;
        }
        for (int i = 0; i < group.Commands.Count; i++)
        {
            _measure.Font = Font;
            _measure.Text = group.Commands[i].Name;
            _measure.Padding = new Padding(i < 9 ? 12 : 6, 0, 6, 0);
            _measure.MinimumSize = new Size(72, 30);
            Place(_measure.GetPreferredSize(Size.Empty).Width);
        }
        _measure.Padding = Padding.Empty;
        _measure.MinimumSize = Size.Empty;
        _measure.Text = group.Commands.Count == 0 ? "This group is empty.   + Add command" : "+ Add command";
        Place(_measure.GetPreferredSize(Size.Empty).Width);
        return rows;
    }

    protected override void OnDockChanged(EventArgs e)
    {
        base.OnDockChanged(e);
        FitHeight();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _badgeFont.Dispose();
            _measure.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Draws the group buttons as tabs: the chosen one is bold with an accent line under it.</summary>
    sealed class TabRenderer : ToolStripProfessionalRenderer
    {
        public TabRenderer() { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) =>
            e.Graphics.Clear(SystemColors.Control);

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(SystemColors.ControlDark);
            e.Graphics.DrawLine(pen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not ToolStripButton { Tag: int } tab)
            {
                base.OnRenderButtonBackground(e);
                return;
            }
            var r = new Rectangle(Point.Empty, e.Item.Size);
            if (tab.Checked)
            {
                using var back = new SolidBrush(SystemColors.Window);
                e.Graphics.FillRectangle(back, r);
                using var accent = new SolidBrush(SystemColors.Highlight);
                e.Graphics.FillRectangle(accent, 0, r.Bottom - 3, r.Width, 3);
            }
            else if (tab.Selected)
            {
                using var hover = new SolidBrush(ControlPaint.Light(SystemColors.Control));
                e.Graphics.FillRectangle(hover, r);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item is ToolStripButton { Tag: int, Checked: true })
            {
                using var bold = new Font(e.TextFont ?? e.Item.Font, FontStyle.Bold);
                e.TextFont = bold;
                e.TextColor = SystemColors.ControlText;
                base.OnRenderItemText(e);
                return;
            }
            if (e.Item is ToolStripButton { Tag: int }) e.TextColor = SystemColors.GrayText;
            base.OnRenderItemText(e);
        }
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
        Size = new Size(560, 180);
        MinimumSize = new Size(320, 120);
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
