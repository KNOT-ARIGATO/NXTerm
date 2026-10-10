namespace NXTerm.Dialogs;

public sealed class LogDialog : DialogBase
{
    readonly TextBox _path;
    readonly CheckBox _plain, _time, _append, _binary;

    public string FilePath => _path.Text.Trim();
    public bool PlainText => _plain.Checked;
    public bool Timestamp => _time.Checked;
    public bool Append => _append.Checked;
    public bool Binary => _binary.Checked;

    public LogDialog(AppSettings s) : base("NXTerm: Log")
    {
        string folder = Directory.Exists(s.LogFolder) ? s.LogFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _path = Row("File:", new TextBox { Text = Path.Combine(folder, $"nxterm_{DateTime.Now:yyyyMMdd_HHmmss}.log"), Width = 360 },
            SmallButton("...", Browse));
        _plain = Full(new CheckBox { Text = "Plain text (remove escape sequences)", Checked = s.LogPlainText, AutoSize = true });
        _time = Full(new CheckBox { Text = "Timestamp each line", Checked = s.LogTimestamp, AutoSize = true });
        _append = Full(new CheckBox { Text = "Append to existing file", Checked = s.LogAppend, AutoSize = true });
        _binary = Full(new CheckBox { Text = "Binary (raw bytes exactly as received)", Checked = s.LogBinary, AutoSize = true });
        _binary.CheckedChanged += (_, _) => _plain.Enabled = _time.Enabled = !_binary.Checked;
        _plain.Enabled = _time.Enabled = !_binary.Checked;
        AddButtons();
    }

    void Browse()
    {
        using var sfd = new SaveFileDialog
        {
            Title = "Log file",
            Filter = "Log files (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*",
            FileName = Path.GetFileName(_path.Text),
            InitialDirectory = Path.GetDirectoryName(_path.Text),
            OverwritePrompt = false,
        };
        if (sfd.ShowDialog(this) == DialogResult.OK) _path.Text = sfd.FileName;
    }

    protected override bool ValidateInput()
    {
        if (FilePath.Length == 0) return Fail("Please enter a file name.", _path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (dir == null || !Directory.Exists(dir)) return Fail("The folder does not exist.", _path);
        return true;
    }
}

public sealed class ScpDialog : DialogBase
{
    readonly RadioButton _upload, _download;
    readonly TextBox _local, _remote;

    public bool Upload => _upload.Checked;
    public string LocalPath => _local.Text.Trim();
    public string RemotePath => _remote.Text.Trim();

    public ScpDialog() : base("NXTerm: SSH SCP")
    {
        var dir = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        _upload = new RadioButton { Text = "Send to remote", AutoSize = true, Checked = true };
        _download = new RadioButton { Text = "Receive from remote", AutoSize = true };
        dir.Controls.AddRange([_upload, _download]);
        Row("Direction:", dir);
        _local = Row("Local file:", new TextBox { Width = 360 }, SmallButton("...", Browse));
        _remote = Row("Remote path:", new TextBox { Width = 360 });
        Full(new Label
        {
            Text = "Remote path is relative to your home directory. When sending, a path ending with '/' (or empty) keeps the local file name.",
            AutoSize = true,
            MaximumSize = new Size(480, 0),
            ForeColor = SystemColors.GrayText,
        });
        AddButtons();
    }

    void Browse()
    {
        if (_upload.Checked)
        {
            using var ofd = new OpenFileDialog { Title = "File to send" };
            if (ofd.ShowDialog(this) == DialogResult.OK) _local.Text = ofd.FileName;
        }
        else
        {
            using var sfd = new SaveFileDialog { Title = "Save as", FileName = Path.GetFileName(_remote.Text) };
            if (sfd.ShowDialog(this) == DialogResult.OK) _local.Text = sfd.FileName;
        }
    }

    protected override bool ValidateInput()
    {
        if (LocalPath.Length == 0) return Fail("Please choose a local file.", _local);
        if (Upload && !File.Exists(LocalPath)) return Fail("Local file not found.", _local);
        if (!Upload && RemotePath.Length == 0) return Fail("Please enter the remote file path.", _remote);
        return true;
    }
}

/// <summary>Modeless progress window for file transfers. Closing it cancels the transfer.</summary>
public sealed class TransferDialog : Form
{
    readonly ProgressBar _bar = new() { Width = 360, Maximum = 1000 };
    readonly Label _label = new() { AutoSize = true };
    readonly CancellationTokenSource _cts = new();
    long _total;

    public CancellationToken Token => _cts.Token;
    public IProgress<long> Progress { get; }

    public TransferDialog(string title, long total)
    {
        Text = title;
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _total = total;
        var cancel = new Button { Text = "Cancel", AutoSize = true, Anchor = AnchorStyles.Right };
        cancel.Click += (_, _) => _cts.Cancel();
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(12) };
        t.Controls.Add(_label);
        t.Controls.Add(_bar);
        t.Controls.Add(cancel);
        Controls.Add(t);
        Progress = new Progress<long>(ShowProgress);
        ShowProgress(0);
    }

    public void SetTotal(long total) => _total = total;

    void ShowProgress(long done)
    {
        if (IsDisposed) return;
        _label.Text = _total > 0 ? $"{done:N0} / {_total:N0} bytes" : $"{done:N0} bytes";
        _bar.Style = _total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
        if (_total > 0) _bar.Value = (int)Math.Clamp(done * 1000 / _total, 0, 1000);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts.Cancel();
        base.OnFormClosing(e);
    }
}
