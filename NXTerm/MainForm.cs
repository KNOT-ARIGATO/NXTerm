using System.Diagnostics;
using System.Text;
using NXTerm.Connections;
using NXTerm.Dialogs;
using NXTerm.Emulation;
using NXTerm.Logging;
using NXTerm.Macro;
using NXTerm.QuickCommands;
using NXTerm.Transfer;
using NXTerm.Updates;

namespace NXTerm;

public sealed class MainForm : Form, IMacroHost
{
    readonly string[] _args;
    AppSettings _settings;

    readonly TerminalView _view = new() { Dock = DockStyle.Fill };
    readonly MenuStrip _menu = new();
    readonly StatusStrip _status = new();
    readonly ToolStripStatusLabel _stConn = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _stMacro = new(), _stLog = new(), _stSize = new();

    IConnection? _conn;
    ConnectionParams? _connParams;
    Encoding _enc = Encoding.UTF8;
    Decoder _dec = Encoding.UTF8.GetDecoder();
    SessionLogger? _logger;
    MacroRunner? _macro;
    CancellationTokenSource? _macroCts;
    volatile XModem? _xfer;
    string _oscTitle = "";
    long _lastBell;
    readonly QuickCommandSet _quick = QuickCommandSet.LoadDefault();
    readonly QuickCommandsPanel _quickPanel;
    QuickCommandsForm? _quickForm;
    ToolStripMenuItem _quickMenu = null!;

    ToolStripMenuItem _miSessions = null!, _sessionsMenu = null!, _miAutoUpdate = null!;
    ToolStripMenuItem _rtspButton = null!, _miRtspTool = null!;
    readonly ToolStripStatusLabel _stUpdate = new() { IsLink = true, Visible = false };

    SavedSession? _connSession;  // saved session of the current connection, if any
    ReleaseInfo? _availableUpdate;
    string[]? _restartArgs;

    readonly object _rxLock = new();
    readonly List<byte> _rxPending = [];
    bool _rxPosted;

    ToolStripMenuItem _miDisconnect = null!, _miStopLog = null!, _miStopMacro = null!, _miScp = null!, _miSaveSession = null!;
    readonly List<ToolStripItem> _needConnection = [];

    public MainForm(string[] args)
    {
        _args = args;
        _settings = AppSettings.LoadDefault();
        Text = "NXTerm";
        Icon = Program.AppIcon;
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        KeyPreview = false;

        _quickPanel = new QuickCommandsPanel(_quick, _settings.QuickGroup, ExecuteQuick);
        _quickPanel.CommandsChanged += BuildQuickMenu;
        _quickPanel.DockToggleRequested += () => SetQuickMode(_settings.QuickMode == QuickMode.Docked ? QuickMode.Floating : QuickMode.Docked);
        _quickPanel.HideRequested += () => SetQuickMode(QuickMode.Hidden);
        _quickPanel.OnTop = _settings.QuickWindowOnTop;
        _quickPanel.OnTopChanged += onTop =>
        {
            _settings.QuickWindowOnTop = onTop;
            if (_quickForm != null) _quickForm.TopMost = onTop;
        };

        BuildMenu();
        _status.Items.AddRange([_stConn, _stUpdate, _stMacro, _stLog, _stSize]);
        _status.SizingGrip = true;
        _stUpdate.Click += (_, _) =>
        {
            if (_availableUpdate != null) ShowUpdateDialog(_availableUpdate);
        };
        Controls.Add(_view);
        Controls.Add(_menu);
        Controls.Add(_status);
        MainMenuStrip = _menu;

        _view.Input += s => SendText(s);
        _view.TerminalResized += (c, r) =>
        {
            _conn?.Resize(c, r);
            UpdateSizeLabel();
        };
        _view.Term.Response += s => SendText(s, echo: false);
        _view.Term.TitleChanged += t =>
        {
            _oscTitle = t;
            UpdateTitle();
        };
        _view.Term.Bell += () =>
        {
            if (Environment.TickCount64 - _lastBell < 300) return;
            _lastBell = Environment.TickCount64;
            System.Media.SystemSounds.Beep.Play();
        };
        _view.PopupMenu = BuildContextMenu();
        _view.AllowDrop = true;
        _view.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true && _conn != null) e.Effect = DragDropEffects.Copy;
        };
        _view.DragDrop += OnFileDrop;

        ApplySettings(resizeWindow: true);
        UpdateConnState();
        UpdateSizeLabel();
    }

    // ================================================================== menus

    static ToolStripMenuItem Item(string text, Action onClick, Keys keys = Keys.None)
    {
        var mi = new ToolStripMenuItem(text, null, (_, _) => onClick());
        if (keys != Keys.None) mi.ShortcutKeys = keys;
        return mi;
    }

    ToolStripMenuItem NeedsConn(ToolStripMenuItem mi)
    {
        _needConnection.Add(mi);
        return mi;
    }

    void BuildMenu()
    {
        _miDisconnect = NeedsConn(Item("D&isconnect", () => Disconnect(), Keys.Alt | Keys.I));
        _miStopLog = Item("Stop l&og", StopLog);
        _miStopMacro = Item("&Stop macro", StopMacro);
        _miScp = Item("SSH S&CP...", ScpTransfer);
        _miSaveSession = NeedsConn(Item("Sa&ve connection as session...", SaveCurrentAsSession));
        _miSessions = new ToolStripMenuItem("Saved s&essions");
        _miSessions.DropDownOpening += (_, _) => FillSessionItems(_miSessions.DropDownItems);
        FillSessionItems(_miSessions.DropDownItems);

        var xmodem = new ToolStripMenuItem("&XMODEM");
        xmodem.DropDownItems.AddRange(
        [
            Item("&Send...", () => XmodemSend(false)),
            Item("Send (&1K blocks)...", () => XmodemSend(true)),
            Item("&Receive...", XmodemReceive),
        ]);
        NeedsConn(xmodem);

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.AddRange(
        [
            Item("&New connection...", ShowConnectDialog, Keys.Alt | Keys.N),
            _miSessions,
            _miSaveSession,
            Item("&Duplicate session", DuplicateSession, Keys.Alt | Keys.D),
            _miDisconnect,
            new ToolStripSeparator(),
            Item("&Log...", () => StartLog()),
            _miStopLog,
            new ToolStripSeparator(),
            NeedsConn(Item("Send file (&text)...", () => SendFile(false))),
            NeedsConn(Item("Send file (&binary)...", () => SendFile(true))),
            xmodem,
            _miScp,
            new ToolStripSeparator(),
            Item("E&xit", Close, Keys.Alt | Keys.Q),
        ]);

        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.AddRange(
        [
            Item("&Copy", _view.CopySelection, Keys.Alt | Keys.C),
            Item("&Paste", () => _view.PasteClipboard(), Keys.Alt | Keys.V),
            Item("Paste<C&R>", () => _view.PasteClipboard(_settings.NewLineString), Keys.Alt | Keys.R),
            new ToolStripSeparator(),
            Item("Select &all", _view.SelectAll),
            Item("Clear &screen", _view.ClearScreen),
            Item("Clear &buffer", _view.ClearBuffer),
        ]);

        _miRtspTool = Item("&RTSP viewer (camera stream)", () =>
        {
            _settings.ShowRtspTool = !_settings.ShowRtspTool;
            ApplyExtraTools();
            SaveSettings();
        });
        var tools = new ToolStripMenuItem("E&xtra tools") { ToolTipText = "Tick a tool to show its button on the menu bar" };
        tools.DropDownItems.Add(_miRtspTool);
        var setup = new ToolStripMenuItem("&Setup");
        setup.DropDownItems.AddRange(
        [
            Item("&Terminal...", ShowTerminalSetup),
            Item("&Font...", ShowFontDialog),
            Item("&Highlight keywords...", ShowHighlightSetup),
            tools,
            new ToolStripSeparator(),
            Item("&Save setup as...", SaveSetupAs),
            Item("&Restore setup...", RestoreSetup),
        ]);

        var control = new ToolStripMenuItem("&Control");
        control.DropDownItems.AddRange(
        [
            Item("&Reset terminal", _view.ResetTerminal),
            NeedsConn(Item("Send &break", () => _conn?.SendBreak(), Keys.Alt | Keys.B)),
            new ToolStripSeparator(),
            Item("&Macro...", () => RunMacro(), Keys.Alt | Keys.M),
            _miStopMacro,
        ]);

        _miAutoUpdate = Item("Check &automatically", () =>
        {
            _settings.CheckForUpdates = !_settings.CheckForUpdates;
            _miAutoUpdate.Checked = _settings.CheckForUpdates;
            SaveSettings();
        });
        _miAutoUpdate.Checked = _settings.CheckForUpdates;
        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.AddRange(
        [
            Item("Check for &updates...", () => _ = CheckForUpdatesAsync(manual: true)),
            _miAutoUpdate,
            new ToolStripSeparator(),
            Item("&Keyboard && mouse", ShowKeyHelp),
            Item("Open-source &licenses", ShowLicenses),
            Item("&About NXTerm", ShowAbout),
        ]);

        _quickMenu = new ToolStripMenuItem("Q&uick");
        _quickMenu.DropDownOpening += (_, _) => BuildQuickMenu();
        BuildQuickMenu();

        _sessionsMenu = new ToolStripMenuItem("Sessi&ons");
        _sessionsMenu.DropDownOpening += (_, _) => FillSessionItems(_sessionsMenu.DropDownItems);
        FillSessionItems(_sessionsMenu.DropDownItems);

        // Buttons of the extra tools sit at the right end of the menu bar, only when ticked in Setup > Extra tools.
        _rtspButton = new ToolStripMenuItem("RTSP", null, (_, _) => OpenRtspViewer())
        {
            Alignment = ToolStripItemAlignment.Right,
            ToolTipText = "Open an RTSP camera viewer window",
            Visible = false,
        };
        _menu.ShowItemToolTips = true;

        _menu.Items.AddRange([file, edit, _sessionsMenu, setup, control, _quickMenu, help, _rtspButton]);
    }

    void ApplyExtraTools()
    {
        _rtspButton.Visible = _settings.ShowRtspTool;
        _miRtspTool.Checked = _settings.ShowRtspTool;
    }

    void OpenRtspViewer()
    {
        var viewer = new Rtsp.RtspViewerForm(_settings, SaveSettings) { Icon = Program.AppIcon };
        viewer.Show();
    }

    // ================================================================== saved sessions

    void FillSessionItems(ToolStripItemCollection items)
    {
        items.Clear();
        foreach (var s in _settings.Sessions.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var session = s;
            items.Add(new ToolStripMenuItem(s.Name.Replace("&", "&&"), null, async (_, _) => await ConnectSessionAsync(session))
            {
                ToolTipText = s.Summary,
                ShortcutKeyDisplayString = s.Summary,
                Checked = _conn != null && _connSession == s,
            });
        }
        if (_settings.Sessions.Count == 0) items.Add(new ToolStripMenuItem("(no saved sessions yet)") { Enabled = false });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Manage sessions...", null, (_, _) => ShowConnectDialog()));
    }

    Task<bool> ConnectSessionAsync(SavedSession s) => ConnectAsync(s.ToParams(), s);

    void SaveCurrentAsSession()
    {
        var p = _connParams;
        if (p == null || _conn == null) return;
        var name = InputDialog.Show(this, "Save session", "Session name:", _connSession?.Name ?? SavedSession.SuggestName(p))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var existing = _settings.Sessions.FirstOrDefault(x => x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (existing != null && existing != _connSession &&
            MessageBox.Show(this, $"A session named \"{name}\" already exists. Replace it?", "NXTerm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        string? password = null;
        if (p.Type == ConnectionType.Ssh && p.Password.Length > 0 &&
            MessageBox.Show(this, "Remember the password too?\n\nIt is encrypted so only your Windows account on this PC can read it.",
                "NXTerm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            password = p.Password;
        var s = existing ?? new SavedSession();
        s.Name = name;
        s.Params = p.Clone();
        s.Params.Password = "";
        s.SetPassword(password);
        s.LastUsed = DateTime.Now;
        if (existing == null) _settings.Sessions.Add(s);
        _connSession = s;
        _settings.LastSession = s.Name;
        SaveSettings();
        UpdateConnState();
        _stConn.Text = $"Saved session \"{name}\". Reconnect any time from the Sessions menu.";
    }

    void BuildQuickMenu()
    {
        _quickMenu.DropDownItems.Clear();
        var show = Item("Show quick commands", ToggleQuick, Keys.Alt | Keys.K);
        show.Checked = _settings.QuickMode != QuickMode.Hidden;
        _quickMenu.DropDownItems.Add(show);
        _quickMenu.DropDownItems.Add(Item(_settings.QuickMode == QuickMode.Floating ? "Dock in main window" : "Show in separate window",
            () => SetQuickMode(_settings.QuickMode == QuickMode.Floating ? QuickMode.Docked : QuickMode.Floating)));
        _quickMenu.DropDownItems.Add(Item("Add command...", () => _quickPanel.AddCommand(_quickPanel.SelectedGroup)));
        if (_quick.Groups.Count > 0) _quickMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var g in _quick.Groups)
        {
            var sub = new ToolStripMenuItem(g.Name.Replace("&", "&&"));
            foreach (var c in g.Commands)
                sub.DropDownItems.Add(new ToolStripMenuItem(c.Name.Replace("&", "&&"), null, (_, _) => ExecuteQuick(c)));
            if (g.Commands.Count == 0) sub.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false });
            _quickMenu.DropDownItems.Add(sub);
        }
    }

    // ================================================================== quick commands

    void ToggleQuick()
    {
        if (_settings.QuickMode != QuickMode.Hidden) SetQuickMode(QuickMode.Hidden);
        else SetQuickMode(_settings.QuickLastMode == QuickMode.Hidden ? QuickMode.Docked : _settings.QuickLastMode);
    }

    /// <summary>Shows the quick command buttons docked under the terminal, in their own window, or hides them.</summary>
    void SetQuickMode(QuickMode mode)
    {
        bool keepSize = WindowState == FormWindowState.Normal;
        SuspendLayout();
        if (mode != QuickMode.Docked && Controls.Contains(_quickPanel))
        {
            int h = _quickPanel.Height;
            Controls.Remove(_quickPanel);
            if (keepSize) Height -= h;
        }
        if (mode != QuickMode.Floating) _quickForm?.Hide();

        if (mode == QuickMode.Docked && !Controls.Contains(_quickPanel))
        {
            _quickForm?.Controls.Remove(_quickPanel);
            Controls.Add(_quickPanel);
            _quickPanel.Dock = DockStyle.Bottom;  // the panel sizes itself to its buttons
            // WinForms docks the highest child index first (outermost): status bar and menu at the edges,
            // then the quick panel, and the terminal fills what is left.
            Controls.SetChildIndex(_view, 0);
            Controls.SetChildIndex(_quickPanel, 1);
            Controls.SetChildIndex(_menu, 2);
            Controls.SetChildIndex(_status, 3);
            if (keepSize)
            {
                var area = Screen.FromControl(this).WorkingArea;
                Height = Math.Min(Height + _quickPanel.Height, area.Height);
                if (Bottom > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height);
            }
        }
        ResumeLayout(true);

        if (mode == QuickMode.Floating)
        {
            var f = EnsureQuickForm();
            if (!f.Controls.Contains(_quickPanel))
            {
                _quickPanel.Dock = DockStyle.Fill;
                f.Controls.Add(_quickPanel);
            }
            if (!f.Visible) f.Show(this);
        }

        _settings.QuickMode = mode;
        if (mode != QuickMode.Hidden) _settings.QuickLastMode = mode;
        _quickPanel.SetMode(mode);
        if (mode != QuickMode.Floating) _view.Focus();
    }

    QuickCommandsForm EnsureQuickForm()
    {
        if (_quickForm != null) return _quickForm;
        var f = new QuickCommandsForm(RunQuickIndex) { Icon = Program.AppIcon, TopMost = _settings.QuickWindowOnTop };
        f.CloseRequested += () => SetQuickMode(QuickMode.Hidden);
        var b = _settings.QuickWindowBounds;
        var saved = b is { Length: 4 } ? new Rectangle(b[0], b[1], b[2], b[3]) : Rectangle.Empty;
        if (saved.Width > 0 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved)))
            f.Bounds = saved;
        else
        {
            // Default: just below the terminal window, same width.
            var area = Screen.FromControl(this).WorkingArea;
            int y = Bottom + f.Height <= area.Bottom ? Bottom : Math.Max(area.Top, area.Bottom - f.Height);
            f.Location = new Point(Left, y);
            f.Width = Math.Max(f.MinimumSize.Width, Width);
        }
        _quickForm = f;
        return f;
    }

    void RunQuickIndex(int index)
    {
        int g = _quickPanel.SelectedGroup;
        if (g < 0 || g >= _quick.Groups.Count) return;
        var cmds = _quick.Groups[g].Commands;
        if (index < cmds.Count) ExecuteQuick(cmds[index]);
    }

    async void ExecuteQuick(QuickCommand c)
    {
        if (c.Confirm && MessageBox.Show(this, $"Send \"{c.Name}\"?\n\n{c.Text}", "NXTerm: Quick command",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        // Give the keyboard back to the terminal so the user can keep typing.
        Activate();
        _view.Focus();
        if (c.Kind == QuickCommandKind.Macro)
        {
            await RunMacroLines(c.Text.Replace("\r\n", "\n").Split('\n'), c.Name);
            return;
        }
        var conn = _conn;
        if (conn == null)
        {
            _stConn.Text = $"Not connected: \"{c.Name}\" was not sent  (Alt+N: new connection)";
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        var parts = c.Expand(_settings.NewLineString);
        for (int i = 0; i < parts.Count && _conn == conn; i++)
        {
            SendText(parts[i]);
            if (c.LineDelayMs > 0 && i < parts.Count - 1) await Task.Delay(c.LineDelayMs);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if ((keyData & Keys.Modifiers) == Keys.Alt && key >= Keys.D1 && key <= Keys.D9)
        {
            RunQuickIndex(key - Keys.D1);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    ContextMenuStrip BuildContextMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.AddRange(
        [
            Item("&Copy", _view.CopySelection),
            Item("&Paste", () => _view.PasteClipboard()),
            Item("Select &all", _view.SelectAll),
            new ToolStripSeparator(),
            Item("Clear &screen", _view.ClearScreen),
            Item("&New connection...", ShowConnectDialog),
        ]);
        return m;
    }

    // ================================================================== settings

    void ApplySettings(bool resizeWindow)
    {
        var s = _settings;
        _view.SetFont(new Font(s.FontName, s.FontSize));
        var defaults = new AppSettings();
        _view.DefaultFore = AppSettings.ParseColor(s.ForeColor, AppSettings.ParseColor(defaults.ForeColor, Color.Silver));
        _view.DefaultBack = AppSettings.ParseColor(s.BackColor, Color.Black);
        _view.CursorColor = AppSettings.ParseColor(s.CursorColor, Color.Lime);
        _view.CursorShape = s.CursorShape;
        _view.CursorBlink = s.CursorBlink;
        _view.CopyOnSelect = s.CopyOnSelect;
        _view.RightClickPaste = s.RightClickPaste;
        _view.BoldAsBright = s.BoldAsBright;
        _view.BackspaceSendsDel = s.BackspaceSendsDel;
        _view.EnterSends = s.NewLineString;
        _view.Highlights = s.HighlightEnabled && s.Highlights.Count > 0 ? new Highlighter(s.Highlights) : null;
        _view.Term.MaxScrollback = Math.Max(0, s.ScrollbackLines);
        _view.Term.LfImpliesCr = s.ReceiveNewline == ReceiveNewline.LfAsCrLf;
        _view.Term.CrImpliesLf = s.ReceiveNewline == ReceiveNewline.CrAsCrLf;
        SetEncoding(s.EncodingName);
        ApplyExtraTools();
        if (resizeWindow) ResizeToTerminal(s.Cols, s.Rows);
        _view.Invalidate();
    }

    void SetEncoding(string name)
    {
        try { _enc = Encoding.GetEncoding(name); }
        catch (ArgumentException) { _enc = Encoding.UTF8; }
        _dec = _enc.GetDecoder();
    }

    void ResizeToTerminal(int cols, int rows)
    {
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        var sz = _view.ClientSizeFor(cols, rows);
        int extra = _menu.Height + _status.Height +
                    (Controls.Contains(_quickPanel) ? _quickPanel.Height : 0);
        ClientSize = new Size(sz.Width, sz.Height + extra);
    }

    void SaveSettings()
    {
        try { _settings.Save(AppSettings.DefaultPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _stConn.Text = "Could not save settings: " + ex.Message;
        }
    }

    void ShowTerminalSetup()
    {
        var copy = _settings.Clone();
        using var dlg = new TerminalSetupDialog(copy);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        bool resize = copy.Cols != _view.Term.Cols || copy.Rows != _view.Term.Rows;
        bool newTermType = copy.TermType != _settings.TermType;
        _settings = copy;
        ApplySettings(resize);
        SaveSettings();
        if (newTermType && _conn != null)
            MessageBox.Show(this, "The new terminal ID will be used for the next connection.", "NXTerm", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void ShowHighlightSetup()
    {
        using var dlg = new HighlightDialog(_settings.HighlightEnabled, _settings.Highlights);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _settings.HighlightEnabled = dlg.HighlightEnabled;
        _settings.Highlights = dlg.Rules;
        ApplySettings(resizeWindow: false);
        SaveSettings();
    }

    void ShowFontDialog()
    {
        using var fd = new FontDialog
        {
            Font = new Font(_settings.FontName, _settings.FontSize),
            FixedPitchOnly = true,
            ShowEffects = false,
            AllowVerticalFonts = false,
        };
        if (fd.ShowDialog(this) != DialogResult.OK) return;
        _settings.FontName = fd.Font.Name;
        _settings.FontSize = fd.Font.Size;
        _settings.Cols = _view.Term.Cols;
        _settings.Rows = _view.Term.Rows;
        ApplySettings(resizeWindow: true);
        SaveSettings();
    }

    void SaveSetupAs()
    {
        using var sfd = new SaveFileDialog { Title = "Save setup", Filter = "NXTerm setup (*.json)|*.json", FileName = "NXTerm.json" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        _settings.Cols = _view.Term.Cols;
        _settings.Rows = _view.Term.Rows;
        try { _settings.Save(sfd.FileName); }
        catch (Exception ex) { Error("Cannot save setup", ex); }
    }

    void RestoreSetup()
    {
        using var ofd = new OpenFileDialog { Title = "Restore setup", Filter = "NXTerm setup (*.json)|*.json|All files (*.*)|*.*" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var loaded = AppSettings.Load(ofd.FileName);
            loaded.KnownHosts = _settings.KnownHosts;  // never let an imported file replace trusted host keys
            _settings = loaded;
            ApplySettings(resizeWindow: true);
            SaveSettings();
        }
        catch (Exception ex) { Error("Cannot load setup", ex); }
    }

    // ================================================================== connection

    async void ShowConnectDialog()
    {
        using var dlg = new ConnectionDialog(_settings, SaveSettings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await ConnectAsync(dlg.Result, dlg.Session);
    }

    public async Task<bool> ConnectAsync(ConnectionParams p, SavedSession? session = null)
    {
        Disconnect(quiet: true);
        if (p.Type == ConnectionType.Ssh)
        {
            if (string.IsNullOrEmpty(p.User))
            {
                var u = InputDialog.Show(this, "SSH authentication", $"User name for {p.Host}:");
                if (string.IsNullOrEmpty(u)) return false;
                p.User = u;
            }
            if (string.IsNullOrEmpty(p.Password) && string.IsNullOrEmpty(p.KeyFile))
            {
                var pw = InputDialog.Show(this, "SSH authentication", $"Password for {p.User}@{p.Host}:", "", password: true);
                if (pw == null) return false;
                p.Password = pw;
            }
        }

        IConnection c = p.Type switch
        {
            ConnectionType.Serial => new SerialConnection(p),
            ConnectionType.Ssh => new SshConnection(p, _settings.TermType, ConfirmHostKey),
            _ => new TcpConnection(p.Host, p.Port, p.Type == ConnectionType.Telnet, _settings.TermType),
        };
        c.DataReceived += (buf, n) => OnDataBackground(c, buf, n);
        c.Disconnected += reason => BeginInvokeSafe(() => OnRemoteDisconnect(c, reason));

        _conn = c;
        _connParams = p;
        _connSession = session;
        _dec = _enc.GetDecoder();
        _oscTitle = "";
        _stConn.Text = $"Connecting to {session?.Name ?? p.ToString()}...";
        UseWaitCursor = true;
        try
        {
            await c.ConnectAsync(_view.Term.Cols, _view.Term.Rows);
        }
        catch (Exception ex)
        {
            if (_conn == c) _conn = null;
            c.Dispose();
            UseWaitCursor = false;
            UpdateConnState();
            MessageBox.Show(this, $"Cannot connect to {p}:\n\n{ex.Message}", "NXTerm", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        UseWaitCursor = false;
        if (_conn != c) return false;

        _settings.LastConnection = p.Clone();
        _settings.LastSession = session?.Name ?? "";
        if (session != null) session.LastUsed = DateTime.Now;
        if (p.Type != ConnectionType.Serial)
        {
            _settings.HostHistory.RemoveAll(h => h.Equals(p.Host, StringComparison.OrdinalIgnoreCase));
            _settings.HostHistory.Insert(0, p.Host);
            if (_settings.HostHistory.Count > 20) _settings.HostHistory.RemoveRange(20, _settings.HostHistory.Count - 20);
        }
        SaveSettings();
        UpdateConnState();
        _view.Focus();
        return true;
    }

    bool ConfirmHostKey(string hostId, string fingerprint)
    {
        string? known = _settings.KnownHosts.GetValueOrDefault(hostId);
        if (known == fingerprint) return true;
        bool changed = known != null;
        string msg = changed
            ? $"WARNING: the host key for {hostId} has CHANGED!\n\n" +
              "Someone could be intercepting the connection (man-in-the-middle attack), " +
              "or the server may simply have been reinstalled.\n\n" +
              $"Known key:\n{known}\n\nNew key:\n{fingerprint}\n\nTrust the new key and continue?"
            : $"The authenticity of host {hostId} can't be established.\n\nKey fingerprint:\n{fingerprint}\n\n" +
              "Trust this host and add it to the known hosts list?";
        var answer = (DialogResult)Invoke(new Func<DialogResult>(() => MessageBox.Show(this, msg, "NXTerm: Security warning",
            MessageBoxButtons.YesNo, changed ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
            changed ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1)));
        if (answer != DialogResult.Yes) return false;
        Invoke(() =>
        {
            _settings.KnownHosts[hostId] = fingerprint;
            SaveSettings();
        });
        return true;
    }

    void OnDataBackground(IConnection c, byte[] buf, int n)
    {
        var x = _xfer;
        if (x != null)
        {
            x.OnData(buf, n);
            return;
        }
        bool post;
        lock (_rxLock)
        {
            _rxPending.AddRange(new ArraySegment<byte>(buf, 0, n));
            post = !_rxPosted;
            _rxPosted = true;
        }
        if (post) BeginInvokeSafe(() => DrainReceived(c));
    }

    void DrainReceived(IConnection c)
    {
        byte[] data;
        lock (_rxLock)
        {
            data = _rxPending.ToArray();
            _rxPending.Clear();
            _rxPosted = false;
        }
        if (c != _conn || data.Length == 0) return;
        _logger?.WriteRaw(data);
        var chars = new char[_dec.GetCharCount(data, 0, data.Length)];
        int count = _dec.GetChars(data, 0, data.Length, chars, 0);
        if (count == 0) return;
        var text = new string(chars, 0, count);
        _logger?.WriteText(text);
        _macro?.OnReceived(text);
        _view.Feed(text);
    }

    void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); }
        catch (InvalidOperationException) { }
    }

    void OnRemoteDisconnect(IConnection c, string? reason)
    {
        if (c != _conn) return;
        _conn = null;
        c.Dispose();
        _view.Feed($"\r\n\x1b[0m[Disconnected{(string.IsNullOrEmpty(reason) ? "" : ": " + reason)}]\r\n");
        UpdateConnState();
    }

    void Disconnect(bool quiet = false)
    {
        var c = _conn;
        if (c == null) return;
        _conn = null;
        c.Dispose();
        if (!quiet) _view.Feed("\r\n\x1b[0m[Disconnected]\r\n");
        UpdateConnState();
    }

    void SendText(string s) => SendText(s, _settings.LocalEcho);

    void SendText(string s, bool echo)
    {
        var c = _conn;
        if (c == null || s.Length == 0) return;
        c.Send(_enc.GetBytes(s));
        if (echo)
            _view.Feed(s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n").Replace("\x7f", "\b \b"));
    }

    void DuplicateSession()
    {
        var args = _conn == null || _connParams == null ? ""
            : _connSession != null ? $"\"/S={_connSession.Name}\""
            : _connParams.ToCommandLine();
        try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, args) { UseShellExecute = false }); }
        catch (Exception ex) { Error("Cannot start a new window", ex); }
    }

    void UpdateConnState()
    {
        bool on = _conn != null;
        _stConn.Text = on
            ? $"Connected: {(_connSession != null ? _connSession.Name + "  (" + _connParams + ")" : _connParams)}"
            : "Not connected  (Alt+N: new connection, or pick one from the Sessions menu)";
        foreach (var i in _needConnection) i.Enabled = on;
        _quickPanel.SetConnected(on);
        _miScp.Enabled = _conn is SshConnection;
        _miStopLog.Enabled = _logger != null;
        _miStopMacro.Enabled = _macro != null;
        UpdateTitle();
    }

    void UpdateTitle()
    {
        var parts = new List<string>();
        if (_oscTitle.Length > 0) parts.Add(_oscTitle);
        if (_conn != null && _connParams != null) parts.Add(_connSession?.Name ?? _connParams.ToString());
        else parts.Add("disconnected");
        parts.Add("NXTerm");
        Text = string.Join(" - ", parts);
    }

    void UpdateSizeLabel() => _stSize.Text = $"{_view.Term.Cols}x{_view.Term.Rows}";

    void Error(string what, Exception ex) =>
        MessageBox.Show(this, $"{what}:\n\n{ex.Message}", "NXTerm", MessageBoxButtons.OK, MessageBoxIcon.Error);

    // ================================================================== logging

    void StartLog(string? path = null, bool? binary = null, bool? append = null)
    {
        bool plain = _settings.LogPlainText, timestamp = _settings.LogTimestamp;
        bool bin = binary ?? _settings.LogBinary, app = append ?? _settings.LogAppend;
        if (path == null)
        {
            using var dlg = new LogDialog(_settings);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FilePath;
            plain = _settings.LogPlainText = dlg.PlainText;
            timestamp = _settings.LogTimestamp = dlg.Timestamp;
            app = _settings.LogAppend = dlg.Append;
            bin = _settings.LogBinary = dlg.Binary;
            _settings.LogFolder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            SaveSettings();
        }
        StopLog();
        try
        {
            _logger = new SessionLogger(path, bin, plain, timestamp, app);
            _stLog.Text = "LOG: " + Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Error("Cannot open log file", ex);
        }
        UpdateConnState();
    }

    void StopLog()
    {
        _logger?.Dispose();
        _logger = null;
        _stLog.Text = "";
        UpdateConnState();
    }

    // ================================================================== file transfer

    async void SendFile(bool binary, string? path = null)
    {
        var c = _conn;
        if (c == null) return;
        if (path == null)
        {
            using var ofd = new OpenFileDialog { Title = binary ? "Send file (binary)" : "Send file (text)" };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            path = ofd.FileName;
        }
        byte[] data;
        try
        {
            if (binary) data = File.ReadAllBytes(path);
            else
            {
                var text = File.ReadAllText(path);
                text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", _settings.NewLineString);
                data = _enc.GetBytes(text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error("Cannot read file", ex);
            return;
        }
        _stConn.Text = $"Sending {Path.GetFileName(path)} ({data.Length:N0} bytes)...";
        await Task.Run(() =>
        {
            for (int i = 0; i < data.Length && _conn == c; i += 1024)
                c.Send(data[i..Math.Min(i + 1024, data.Length)]);
        });
        UpdateConnState();
    }

    void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files || _conn == null) return;
        var r = MessageBox.Show(this, $"Send \"{Path.GetFileName(files[0])}\"?\n\nYes = as text, No = as binary.", "NXTerm",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Yes) SendFile(false, files[0]);
        else if (r == DialogResult.No) SendFile(true, files[0]);
    }

    async void XmodemSend(bool oneK)
    {
        var c = _conn;
        if (c == null) return;
        using var ofd = new OpenFileDialog { Title = "XMODEM send" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        await RunXmodem($"XMODEM send: {Path.GetFileName(ofd.FileName)}", new FileInfo(ofd.FileName).Length, c,
            (xm, prog, ct) =>
            {
                using var fs = File.OpenRead(ofd.FileName);
                xm.Send(fs, oneK, prog, ct);
            });
    }

    async void XmodemReceive()
    {
        var c = _conn;
        if (c == null) return;
        using var sfd = new SaveFileDialog { Title = "XMODEM receive: save as" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        await RunXmodem($"XMODEM receive: {Path.GetFileName(sfd.FileName)}", 0, c,
            (xm, prog, ct) =>
            {
                using var fs = File.Create(sfd.FileName);
                xm.Receive(fs, prog, ct);
            });
    }

    async Task RunXmodem(string title, long total, IConnection c, Action<XModem, IProgress<long>, CancellationToken> work)
    {
        var xm = new XModem(b => c.Send(b));
        var dlg = new TransferDialog(title, total);
        _xfer = xm;
        dlg.Show(this);
        string? error = null;
        try
        {
            await Task.Run(() => work(xm, dlg.Progress, dlg.Token));
        }
        catch (OperationCanceledException)
        {
            xm.Cancel();
            error = "Cancelled.";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        finally
        {
            _xfer = null;
            dlg.Close();
            dlg.Dispose();
        }
        MessageBox.Show(this, error == null ? "Transfer complete." : "Transfer failed: " + error, title,
            MessageBoxButtons.OK, error == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    async void ScpTransfer()
    {
        if (_conn is not SshConnection ssh) return;
        using var dlg = new ScpDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        bool up = dlg.Upload;
        string local = dlg.LocalPath, remote = dlg.RemotePath;
        var prog = new TransferDialog(up ? $"SCP send: {Path.GetFileName(local)}" : $"SCP receive: {remote}", up ? new FileInfo(local).Length : 0);
        prog.Show(this);
        string? error = null;
        try
        {
            await Task.Run(() =>
            {
                Action<long, long> report = (done, size) =>
                {
                    prog.SetTotal(size);
                    prog.Progress.Report(done);
                };
                if (up) ssh.ScpUpload(local, remote, report, prog.Token);
                else ssh.ScpDownload(remote, local, report, prog.Token);
            });
        }
        catch (Exception ex)
        {
            error = prog.Token.IsCancellationRequested ? "Cancelled." : ex.Message;
        }
        finally
        {
            prog.Close();
            prog.Dispose();
        }
        MessageBox.Show(this, error == null ? "Transfer complete." : "Transfer failed: " + error, "SCP",
            MessageBoxButtons.OK, error == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    // ================================================================== macro

    async void RunMacro(string? path = null)
    {
        if (path == null)
        {
            using var ofd = new OpenFileDialog { Title = "Run macro", Filter = "Macro files (*.ttl)|*.ttl|All files (*.*)|*.*" };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            path = ofd.FileName;
        }
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error("Cannot read macro", ex);
            return;
        }
        await RunMacroLines(lines, Path.GetFileName(path));
    }

    async Task RunMacroLines(IEnumerable<string> lines, string name)
    {
        if (_macro != null)
        {
            MessageBox.Show(this, "A macro is already running.", "NXTerm", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        MacroRunner runner;
        try
        {
            runner = new MacroRunner(lines, this);
        }
        catch (MacroException ex)
        {
            MessageBox.Show(this, $"{name}, line {ex.Line}:\n{ex.Message}", "Macro error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _macro = runner;
        _macroCts = new CancellationTokenSource();
        _stMacro.Text = "MACRO: " + name;
        UpdateConnState();
        try
        {
            await runner.RunAsync(_macroCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (MacroException ex)
        {
            MessageBox.Show(this, $"{name}, line {ex.Line}:\n{ex.Message}", "Macro error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _macro = null;
            _macroCts.Dispose();
            _macroCts = null;
            _stMacro.Text = "";
            UpdateConnState();
        }
    }

    void StopMacro() => _macroCts?.Cancel();

    T OnUi<T>(Func<T> f) => InvokeRequired ? (T)Invoke(f)! : f();
    void OnUi(Action a)
    {
        if (InvokeRequired) Invoke(a);
        else a();
    }

    string IMacroHost.NewLine => _settings.NewLineString;
    bool IMacroHost.IsConnected => OnUi(() => _conn != null);
    void IMacroHost.Send(string text) => OnUi(() => SendText(text));

    bool IMacroHost.Connect(string args)
    {
        var p = ConnectionParams.Parse(ConnectionParams.SplitArgs(args), out _, out _)
                ?? throw new MacroException($"invalid connect parameters: {args}");
        return OnUi(() => ConnectAsync(p)).GetAwaiter().GetResult();
    }

    void IMacroHost.Disconnect() => OnUi(() => Disconnect());
    void IMacroHost.LogOpen(string path, bool binary, bool append) => OnUi(() => StartLog(path, binary, append));
    void IMacroHost.LogClose() => OnUi(StopLog);
    void IMacroHost.LogWrite(string text) => OnUi(() => _logger?.WriteNote(text));
    void IMacroHost.MessageBox(string text, string title) => OnUi(() => MessageBox.Show(this, text, title));

    bool IMacroHost.YesNoBox(string text, string title) =>
        OnUi(() => MessageBox.Show(this, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);

    string? IMacroHost.InputBox(string text, string title, string def, bool password) =>
        OnUi(() => InputDialog.Show(this, title, text, def, password));

    void IMacroHost.Status(string text) => OnUi(() => _stMacro.Text = text);
    void IMacroHost.SendBreak() => OnUi(() => _conn?.SendBreak());
    void IMacroHost.ClearScreen() => OnUi(_view.ClearScreen);
    void IMacroHost.Display(string text) => OnUi(() => _view.Feed(text));

    void IMacroHost.SetTitle(string title) => OnUi(() =>
    {
        _oscTitle = title;
        UpdateTitle();
    });

    // ================================================================== help

    void ShowKeyHelp() => MessageBox.Show(this,
        "Keyboard\n" +
        "  Alt+N  New connection          Alt+I  Disconnect\n" +
        "  Alt+C  Copy                    Alt+V  Paste\n" +
        "  Alt+R  Paste + Enter           Alt+B  Send break\n" +
        "  Alt+M  Run macro               Alt+D  Duplicate session\n" +
        "  Alt+K  Show/hide quick commands   Alt+1..9  Button 1-9 of the current tab (number in its corner)\n" +
        "  Ctrl+Shift+C / Ctrl+Insert     Copy\n" +
        "  Ctrl+Shift+V / Shift+Insert    Paste\n" +
        "  Shift+PageUp / PageDown        Scroll back\n\n" +
        "Mouse\n" +
        "  Drag to select (copies automatically), double-click selects a word,\n" +
        "  Shift+click extends the selection, right click pastes,\n" +
        "  wheel scrolls back. Hold Shift to select when an app uses the mouse.\n" +
        "  Drop a file on the window to send it.",
        "NXTerm: Keyboard & mouse");

    void ShowLicenses()
    {
        static string Read(string name)
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream(name);
            if (s == null) return $"({name} is missing from this build)";
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        var text = "NXTerm\r\n=====\r\n\r\n" + Read("LICENSE.txt") + "\r\n\r\n" + Read("THIRD-PARTY-NOTICES.md");
        using var f = new Form
        {
            Text = "NXTerm: Open-source licenses",
            Icon = Program.AppIcon,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(720, 520),
            MinimizeBox = false,
            ShowInTaskbar = false,
        };
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5f),
            BackColor = SystemColors.Window,
            Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
        };
        f.Controls.Add(box);
        f.Shown += (_, _) => box.Select(0, 0);
        f.ShowDialog(this);
    }

    void ShowAbout() => MessageBox.Show(this,
        $"NXTerm {Updater.CurrentVersionText} - terminal emulator for Windows\n\n" +
        "Serial (COM), Telnet, SSH2 and raw TCP\n" +
        "VT100/VT220/xterm emulation with 256 colors and true color\n" +
        "UTF-8, Thai (TIS-620), Japanese, Chinese and Korean encodings\n" +
        "Logging, XMODEM, SSH SCP, macros, quick commands, RTSP viewer\n\n" +
        "Released under the MIT License (Help > Open-source licenses).\n\n" +
        "Settings: " + AppSettings.DefaultPath +
        (Updater.Repo != null ? $"\nUpdates: github.com/{Updater.Repo}" : ""),
        "About NXTerm");

    // ================================================================== lifecycle

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _view.Focus();
        if (_settings.QuickMode != QuickMode.Hidden) SetQuickMode(_settings.QuickMode);
        _ = CheckForUpdatesAsync(manual: false);

        var sessionArg = _args.FirstOrDefault(a => a.StartsWith("/S=", StringComparison.OrdinalIgnoreCase))?[3..].Trim('"');
        var p = ConnectionParams.Parse(_args, out var macro, out var log);
        if (log != null) StartLog(log);
        if (sessionArg != null)
        {
            var s = _settings.Sessions.FirstOrDefault(x => x.Name.Equals(sessionArg, StringComparison.CurrentCultureIgnoreCase));
            if (s != null) await ConnectSessionAsync(s);
            else MessageBox.Show(this, $"There is no saved session named \"{sessionArg}\".", "NXTerm", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else if (p != null) await ConnectAsync(p);
        if (macro != null) RunMacro(macro);
        else if (p == null && sessionArg == null) ShowConnectDialog();
    }

    // ================================================================== updates

    async Task CheckForUpdatesAsync(bool manual)
    {
        if (Updater.Repo == null)
        {
            if (manual)
                MessageBox.Show(this,
                    "This copy of NXTerm was built on this PC, so it does not know where to look for updates.\n\n" +
                    "Copies downloaded from GitHub Releases check automatically.\n" +
                    "For your own builds, set <GitHubRepo>owner/repo</GitHubRepo> in NXTerm.csproj.",
                    "NXTerm: Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!manual && (!_settings.CheckForUpdates || DateTime.UtcNow - _settings.LastUpdateCheck < TimeSpan.FromHours(20))) return;

        ReleaseInfo? release;
        try
        {
            if (manual) UseWaitCursor = true;
            release = await Updater.GetLatestAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A failed check must never disturb the user's work; only report it when they asked for it.
            if (manual && MessageBox.Show(this,
                    "Could not check for updates:\n" + Updater.DescribeFailure(ex, Updater.LastCertificateProblem) +
                    "\n\nOpen the download page in your browser instead?",
                    "NXTerm: Updates", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                Updater.OpenInBrowser($"https://github.com/{Updater.Repo}/releases/latest");
            return;
        }
        finally
        {
            UseWaitCursor = false;
        }
        if (IsDisposed) return;
        _settings.LastUpdateCheck = DateTime.UtcNow;
        SaveSettings();

        if (release == null || !Updater.IsNewer(release))
        {
            if (manual)
                MessageBox.Show(this, $"You have the latest version ({Updater.CurrentVersionText}).", "NXTerm: Updates",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _availableUpdate = release;
        _stUpdate.Text = $"Update available: {release.Version.ToString(3)}";
        _stUpdate.Visible = manual || release.Tag != _settings.SkippedVersion;
        if (manual) ShowUpdateDialog(release);
    }

    void ShowUpdateDialog(ReleaseInfo release)
    {
        using var dlg = new UpdateDialog(release) { Icon = Program.AppIcon };
        dlg.ShowDialog(this);
        switch (dlg.Choice)
        {
            case UpdateChoice.Skip:
                _settings.SkippedVersion = release.Tag;
                _stUpdate.Visible = false;
                SaveSettings();
                break;
            case UpdateChoice.Installed:
                // Reopen the same saved session in the new version, if there is one.
                _restartArgs = _conn != null && _connSession != null ? [$"/S={_connSession.Name}"] : [];
                Close();
                break;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _macroCts?.Cancel();
        Disconnect(quiet: true);
        StopLog();
        if (WindowState == FormWindowState.Normal)
        {
            _settings.Cols = _view.Term.Cols;
            _settings.Rows = _view.Term.Rows;
        }
        if (_quickForm != null)
        {
            var b = _quickForm.WindowState == FormWindowState.Normal ? _quickForm.Bounds : _quickForm.RestoreBounds;
            _settings.QuickWindowBounds = [b.X, b.Y, b.Width, b.Height];
        }
        if (Controls.Contains(_quickPanel)) _settings.QuickDockHeight = _quickPanel.Height;
        _settings.QuickGroup = Math.Max(0, _quickPanel.SelectedGroup);
        SaveSettings();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (_restartArgs != null) Updater.StartNewInstance(_restartArgs);
    }
}
