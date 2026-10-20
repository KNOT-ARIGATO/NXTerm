using System.IO.Ports;
using NXTerm.Connections;

namespace NXTerm.Dialogs;

/// <summary>New connection dialog with a list of saved sessions on the left.</summary>
public sealed class ConnectionDialog : DialogBase
{
    static readonly int[] Bauds = [110, 300, 600, 1200, 2400, 4800, 9600, 14400, 19200, 38400, 57600, 115200, 230400, 460800, 921600, 1000000, 2000000];
    static readonly string[] ParityNames = ["none", "odd", "even", "mark", "space"];
    static readonly string[] StopNames = ["1", "1.5", "2"];
    static readonly string[] FlowNames = ["none", "Xon/Xoff", "RTS/CTS", "RTS/CTS + Xon/Xoff"];

    readonly AppSettings _settings;
    readonly Action _saveSettings;
    readonly RadioButton _tcp, _serial, _telnet, _ssh, _raw;
    readonly ComboBox _host, _com, _baud, _data, _parity, _stop, _flow;
    readonly NumericUpDown _port, _charDelay, _lineDelay;
    readonly TextBox _user, _pass, _key;
    readonly CheckBox _remember;
    readonly Button _keyBrowse;
    readonly ListView _list;
    readonly List<Control> _tcpControls = [], _serialControls = [];
    int _lastAutoPort;
    bool _loading, _filling;
    SavedSession? _loaded;  // the session whose values are currently in the fields

    public ConnectionParams Result { get; private set; } = new();
    /// <summary>The saved session used for this connection, or null for an unsaved one.</summary>
    public SavedSession? Session { get; private set; }

    public ConnectionDialog(AppSettings settings, Action saveSettings) : base("NXTerm: New connection")
    {
        _settings = settings;
        _saveSettings = saveSettings;
        var bold = new Font(Font, FontStyle.Bold);

        // ---- right side: connection details
        _tcp = Full(new RadioButton { Text = "TCP/IP", AutoSize = true, Font = bold });
        _host = Row("Host:", Combo(settings.HostHistory.Cast<object>(), "", editable: true));
        var svc = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        _ssh = new RadioButton { Text = "SSH", AutoSize = true };
        _telnet = new RadioButton { Text = "Telnet", AutoSize = true };
        _raw = new RadioButton { Text = "Raw TCP", AutoSize = true };
        svc.Controls.AddRange([_ssh, _telnet, _raw]);
        Row("Service:", svc);
        _port = Row("TCP port:", new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 22 });
        _user = Row("User name:", new TextBox());
        _pass = Row("Password / passphrase:", new TextBox { UseSystemPasswordChar = true });
        _remember = Row("", new CheckBox { Text = "Remember password (encrypted for your Windows account)", AutoSize = true });
        _keyBrowse = SmallButton("...", BrowseKey);
        _key = Row("Private key file:", new TextBox(), _keyBrowse);
        _tcpControls.AddRange([_host, svc, _port, _user, _pass, _remember, _key, _keyBrowse]);

        _serial = Full(new RadioButton { Text = "Serial", AutoSize = true, Font = bold, Margin = new Padding(3, 12, 3, 3) });
        var ports = SerialConnection.ListPorts(friendlyNames: false);
        _com = Row("Port:", Combo(ports.Cast<object>(), null, editable: true));
        _baud = Row("Speed (baud):", Combo(Bauds.Cast<object>(), 115200, editable: true));
        _data = Row("Data bits:", Combo([5, 6, 7, 8], 8));
        _parity = Row("Parity:", Combo(ParityNames, "none"));
        _stop = Row("Stop bits:", Combo(StopNames, "1"));
        _flow = Row("Flow control:", Combo(FlowNames, "none"));
        _charDelay = Row("Transmit delay (ms/char):", new NumericUpDown { Maximum = 5000 });
        _lineDelay = Row("Transmit delay (ms/line):", new NumericUpDown { Maximum = 10000 });
        _serialControls.AddRange([_com, _baud, _data, _parity, _stop, _flow, _charDelay, _lineDelay]);
        if (ports.Count == 0) Full(new Label { Text = "(no COM ports detected)", AutoSize = true, ForeColor = SystemColors.GrayText });
        AddButtons("Connect");

        // ---- left side: saved sessions
        _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Size = new Size(300, 420),
            ShowItemToolTips = true,
        };
        _list.Columns.Add("Name", 120);
        _list.Columns.Add("Connection", 170);
        var left = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 14, 0) };
        left.Controls.Add(new Label { Text = "Saved sessions", Font = bold, AutoSize = true, Margin = new Padding(3, 6, 3, 6) });
        left.Controls.Add(_list);
        var buttons = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(310, 0), Margin = Padding.Empty };
        buttons.Controls.AddRange(
        [
            SmallButton("New", NewSession),
            SmallButton("Save...", SaveSession),
            SmallButton("Rename...", RenameSession),
            SmallButton("Delete", DeleteSession),
            SmallButton("Desktop shortcut", MakeShortcut),
        ]);
        left.Controls.Add(buttons);
        left.Controls.Add(new Label
        {
            Text = "\"New\" starts empty details for another device.\nFill them in, then \"Save...\" to keep them.\nDouble-click a session to connect.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });

        Controls.Remove(Table);
        var outer = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Location = new Point(10, 10) };
        outer.Controls.Add(left, 0, 0);
        outer.Controls.Add(Table, 1, 0);
        Controls.Add(outer);

        // ---- behavior
        foreach (var rb in new[] { _tcp, _serial }) rb.CheckedChanged += (_, _) => UpdateEnabled();
        foreach (var rb in new[] { _ssh, _telnet, _raw }) rb.CheckedChanged += (_, _) => OnServiceChanged();
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (!_filling && _list.SelectedItems.Count == 1) LoadSession((SavedSession)_list.SelectedItems[0].Tag!);
        };
        _list.DoubleClick += (_, _) =>
        {
            if (_list.SelectedItems.Count == 1 && ValidateInput()) DialogResult = DialogResult.OK;
        };

        LoadParams(settings.LastConnection, "");
        var lastSession = settings.Sessions.FirstOrDefault(s => s.Name == settings.LastSession);
        FillList(lastSession);
        if (lastSession != null) LoadSession(lastSession);
        ActiveControl = settings.Sessions.Count > 0 ? _list : _host;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Device names come from WMI, which can take seconds: fill them in after the dialog is up.
        var named = await Task.Run(() => SerialConnection.ListPorts(friendlyNames: true));
        if (IsDisposed) return;
        string current = _com.Text.Split(':')[0].Trim();
        _com.Items.Clear();
        foreach (var n in named) _com.Items.Add(n);
        _com.Text = named.FirstOrDefault(n => n.Split(':')[0] == current) ?? _com.Text;
    }

    // ------------------------------------------------------------------ fields

    ConnectionType Service => _ssh.Checked ? ConnectionType.Ssh : _telnet.Checked ? ConnectionType.Telnet : ConnectionType.Raw;

    void OnServiceChanged()
    {
        if (!_loading)
        {
            int def = ConnectionParams.DefaultPort(Service);
            if (def > 0 && (int)_port.Value == _lastAutoPort)
            {
                _port.Value = def;
                _lastAutoPort = def;
            }
        }
        UpdateEnabled();
    }

    void UpdateEnabled()
    {
        foreach (var c in _tcpControls) c.Enabled = _tcp.Checked;
        foreach (var c in _serialControls) c.Enabled = _serial.Checked;
        bool ssh = _tcp.Checked && _ssh.Checked;
        _user.Enabled = _pass.Enabled = _remember.Enabled = _key.Enabled = _keyBrowse.Enabled = ssh;
    }

    void LoadParams(ConnectionParams p, string password)
    {
        _loading = true;
        (p.Type switch { ConnectionType.Telnet => _telnet, ConnectionType.Raw => _raw, _ => _ssh }).Checked = true;
        (p.Type == ConnectionType.Serial ? _serial : _tcp).Checked = true;
        _host.Text = p.Host;
        _port.Value = Math.Clamp(p.Port, 1, 65535);
        _lastAutoPort = (int)_port.Value;
        _user.Text = p.User;
        _pass.Text = password;
        _key.Text = p.KeyFile;
        var match = _com.Items.Cast<object>().Select(o => o.ToString()!).FirstOrDefault(n => n.Split(':')[0].Trim() == p.PortName);
        _com.Text = match ?? (p.Type == ConnectionType.Serial || _com.Items.Count == 0 ? p.PortName : _com.Items[0]!.ToString());
        _baud.Text = p.Baud.ToString();
        _data.SelectedItem = p.DataBits;
        _parity.SelectedIndex = Math.Clamp((int)p.Parity, 0, ParityNames.Length - 1);
        _stop.SelectedIndex = p.StopBits switch { StopBits.OnePointFive => 1, StopBits.Two => 2, _ => 0 };
        _flow.SelectedIndex = Math.Clamp((int)p.Handshake, 0, FlowNames.Length - 1);
        _charDelay.Value = Math.Clamp(p.CharDelayMs, 0, 5000);
        _lineDelay.Value = Math.Clamp(p.LineDelayMs, 0, 10000);
        _loading = false;
        UpdateEnabled();
    }

    ConnectionParams? BuildParams()
    {
        var r = new ConnectionParams();
        if (_tcp.Checked)
        {
            r.Type = Service;
            r.Host = _host.Text.Trim();
            if (r.Host.Length == 0) return Invalid("Please enter a host name or IP address.", _host);
            int at = r.Host.LastIndexOf('@');
            if (at > 0)
            {
                _user.Text = r.Host[..at];
                r.Host = r.Host[(at + 1)..];
                _host.Text = r.Host;
            }
            r.Port = (int)_port.Value;
            if (r.Type == ConnectionType.Ssh)
            {
                r.User = _user.Text.Trim();
                r.Password = _pass.Text;
                r.KeyFile = _key.Text.Trim();
                if (r.KeyFile.Length > 0 && !File.Exists(r.KeyFile)) return Invalid("Private key file not found.", _key);
            }
        }
        else
        {
            r.Type = ConnectionType.Serial;
            r.PortName = _com.Text.Split(':')[0].Trim().ToUpperInvariant();
            if (r.PortName.Length == 0) return Invalid("Please choose a COM port.", _com);
            if (!int.TryParse(_baud.Text.Trim(), out int baud) || baud <= 0) return Invalid("Invalid speed.", _baud);
            r.Baud = baud;
            r.DataBits = (int)_data.SelectedItem!;
            r.Parity = (Parity)_parity.SelectedIndex;
            r.StopBits = _stop.SelectedIndex switch { 1 => StopBits.OnePointFive, 2 => StopBits.Two, _ => StopBits.One };
            r.Handshake = (Handshake)_flow.SelectedIndex;
            r.CharDelayMs = (int)_charDelay.Value;
            r.LineDelayMs = (int)_lineDelay.Value;
        }
        return r;
    }

    ConnectionParams? Invalid(string message, Control focus)
    {
        Fail(message, focus);
        return null;
    }

    string? PasswordToStore(ConnectionParams p) =>
        p.Type == ConnectionType.Ssh && _remember.Checked && p.Password.Length > 0 ? p.Password : null;

    static ConnectionParams WithoutPassword(ConnectionParams p)
    {
        var c = p.Clone();
        c.Password = "";
        return c;
    }

    protected override bool ValidateInput()
    {
        var p = BuildParams();
        if (p == null) return false;
        Session = null;
        if (_loaded != null)
        {
            bool same = SavedSession.SameSettings(p, _loaded.Params) && (PasswordToStore(p) ?? "") == _loaded.GetPassword();
            if (same) Session = _loaded;
            else
            {
                var answer = MessageBox.Show(this, $"You changed the settings of session \"{_loaded.Name}\".\n\nSave the changes before connecting?",
                    Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return false;
                if (answer == DialogResult.Yes)
                {
                    _loaded.Params = WithoutPassword(p);
                    _loaded.SetPassword(PasswordToStore(p));
                    _saveSettings();
                    Session = _loaded;
                }
            }
        }
        Result = p;
        return true;
    }

    // ------------------------------------------------------------------ sessions

    void FillList(SavedSession? select)
    {
        _filling = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var s in _settings.Sessions.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new ListViewItem([s.Name, s.Summary])
            {
                Tag = s,
                ToolTipText = s.Summary + (s.HasPassword ? "\npassword saved" : "") +
                              (s.LastUsed > DateTime.MinValue ? $"\nlast used {s.LastUsed:g}" : ""),
            };
            _list.Items.Add(item);
            if (s == select) item.Selected = true;
        }
        _list.EndUpdate();
        _list.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.EnsureVisible();
        _filling = false;
    }

    void LoadSession(SavedSession s)
    {
        _loaded = s;
        LoadParams(s.Params, s.GetPassword());
        _remember.Checked = s.HasPassword;
    }

    SavedSession? Selected => _list.SelectedItems.Count == 1 ? (SavedSession)_list.SelectedItems[0].Tag! : null;

    SavedSession? FindByName(string name) =>
        _settings.Sessions.FirstOrDefault(s => s.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Forgets the chosen session and clears the details, so a new one can be entered without changing a saved one.</summary>
    void NewSession()
    {
        _filling = true;
        _list.SelectedItems.Clear();
        _filling = false;
        _loaded = null;
        var blank = new ConnectionParams { Type = _serial.Checked ? ConnectionType.Serial : Service, PortName = _com.Text.Split(':')[0].Trim() };
        blank.Port = Math.Max(1, ConnectionParams.DefaultPort(blank.Type == ConnectionType.Serial ? ConnectionType.Ssh : blank.Type));
        LoadParams(blank, "");
        _remember.Checked = false;
        (_serial.Checked ? (Control)_com : _host).Focus();
    }

    void SaveSession()
    {
        var p = BuildParams();
        if (p == null) return;
        // Suggest the chosen session's name only when its details are unchanged; edited details are most
        // likely a new device, and reusing the name would silently overwrite the old session.
        bool unchanged = _loaded != null && SavedSession.SameSettings(p, _loaded.Params);
        var name = InputDialog.Show(this, "Save session", "Session name:", unchanged ? _loaded!.Name : SavedSession.SuggestName(p))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var existing = FindByName(name);
        if (existing != null && !(existing == _loaded && unchanged) &&
            MessageBox.Show(this, $"A session named \"{name}\" already exists. Replace it?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        var s = existing ?? new SavedSession();
        s.Name = name;
        s.Params = WithoutPassword(p);
        s.SetPassword(PasswordToStore(p));
        if (existing == null) _settings.Sessions.Add(s);
        _saveSettings();
        _loaded = s;
        FillList(s);
    }

    void RenameSession()
    {
        var s = Selected;
        if (s == null) { MessageBox.Show(this, "Select a session first.", Text); return; }
        var name = InputDialog.Show(this, "Rename session", "New name:", s.Name)?.Trim();
        if (string.IsNullOrEmpty(name) || name == s.Name) return;
        if (FindByName(name) is { } other && other != s)
        {
            MessageBox.Show(this, $"A session named \"{name}\" already exists.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_settings.LastSession == s.Name) _settings.LastSession = name;
        s.Name = name;
        _saveSettings();
        FillList(s);
    }

    void DeleteSession()
    {
        var s = Selected;
        if (s == null) { MessageBox.Show(this, "Select a session first.", Text); return; }
        if (MessageBox.Show(this, $"Delete session \"{s.Name}\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        _settings.Sessions.Remove(s);
        if (_loaded == s) _loaded = null;
        _saveSettings();
        FillList(null);
    }

    void MakeShortcut()
    {
        var s = Selected;
        if (s == null) { MessageBox.Show(this, "Select a session first.", Text); return; }
        try
        {
            var path = Shortcuts.CreateForSession(s.Name);
            MessageBox.Show(this, $"Created a desktop shortcut:\n{Path.GetFileName(path)}\n\nDouble-click it to open NXTerm and connect to \"{s.Name}\".",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(this, "Cannot create the shortcut:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void BrowseKey()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Private key file",
            Filter = "All files (*.*)|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };
        if (ofd.ShowDialog(this) == DialogResult.OK) _key.Text = ofd.FileName;
    }
}
