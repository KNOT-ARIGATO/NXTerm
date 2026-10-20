using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace NXTerm.Rtsp;

/// <summary>Extra tool: a window that shows the H.264 video of an RTSP camera.</summary>
public sealed class RtspViewerForm : Form
{
    static readonly byte[] Entropy = "NXTerm.Rtsp.v1"u8.ToArray();
    const int MaxHistory = 12;

    readonly AppSettings _settings;
    readonly Action _saveSettings;
    static readonly string[] CommonProfiles = ["profile1", "profile2", "stream1", "stream2", "live", "h264"];

    readonly ComboBox _ip = new() { Width = 170, Margin = new Padding(3, 5, 3, 3) };
    readonly ComboBox _profile = new() { Dock = DockStyle.Fill, MinimumSize = new Size(90, 0), Margin = new Padding(3, 5, 3, 3) };
    readonly ToolTip _tip = new() { AutoPopDelay = 20000 };
    readonly List<string> _history = [];  // full addresses, most recent first
    readonly Button _connect = new() { Text = "Connect", AutoSize = true, MinimumSize = new Size(90, 0) };
    readonly Button _snapshot = new() { Text = "Snapshot", AutoSize = true, Enabled = false };
    readonly CheckBox _onTop = new() { Text = "Always on top", AutoSize = true, Margin = new Padding(8, 7, 3, 3) };
    readonly TableLayoutPanel _bar = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 7, Padding = new Padding(4, 2, 4, 2) };
    readonly VideoPanel _video = new() { Dock = DockStyle.Fill };
    readonly StatusStrip _status = new();
    readonly ToolStripStatusLabel _stState = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _stInfo = new();
    readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 1000 };

    CancellationTokenSource? _cts;
    volatile RtspClient? _client;
    int _generation;  // bumped on every start/stop so late frames of an old stream are ignored

    readonly object _frameLock = new();
    byte[]? _pending, _spare;
    int _pendingWidth, _pendingHeight;
    bool _posted;
    Bitmap? _bitmap;
    int _frames;
    long _lastBytes;

    bool _fullScreen;
    Rectangle _normalBounds;
    FormWindowState _normalState;

    public RtspViewerForm(AppSettings settings, Action saveSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        Text = "NXTerm: RTSP viewer";
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(720, 480);
        MinimumSize = new Size(360, 240);
        KeyPreview = true;

        _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _bar.Controls.Add(new Label { Text = "IP:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 0);
        _bar.Controls.Add(_ip, 1, 0);
        _bar.Controls.Add(new Label { Text = "Profile:", AutoSize = true, Margin = new Padding(8, 8, 3, 3) }, 2, 0);
        _bar.Controls.Add(_profile, 3, 0);
        _bar.Controls.Add(_connect, 4, 0);
        _bar.Controls.Add(_snapshot, 5, 0);
        _bar.Controls.Add(_onTop, 6, 0);
        _tip.SetToolTip(_ip, "Camera IP address, for example 192.168.43.1\n" +
                             "With a port: 192.168.43.1:8554\n" +
                             "With a login: user:password@192.168.1.10\n" +
                             "A whole rtsp:// address can be pasted here too.");
        _tip.SetToolTip(_profile, "The stream name after the address, for example profile1 or stream1");
        foreach (var name in CommonProfiles) _profile.Items.Add(name);
        _status.Items.AddRange([_stState, _stInfo]);
        Controls.Add(_video);
        Controls.Add(_bar);
        Controls.Add(_status);

        foreach (var saved in settings.RtspHistory)
            if (Secrets.Unprotect(saved, Entropy) is { Length: > 0 } url) _history.Add(url);
        FillHistory();
        if (_history.Count > 0) ShowAddress(_history[0]);
        else _profile.Text = CommonProfiles[0];
        _ip.SelectionChangeCommitted += (_, _) =>
        {
            // Picking a camera from the list brings back the profile last used with it.
            var host = _ip.SelectedItem as string;
            var last = _history.FirstOrDefault(u => SplitAddress(u).Host.Equals(host, StringComparison.OrdinalIgnoreCase));
            if (last != null) _profile.Text = SplitAddress(last).Profile;
        };

        var b = settings.RtspWindowBounds;
        var saved2 = b is { Length: 4 } ? new Rectangle(b[0], b[1], b[2], b[3]) : Rectangle.Empty;
        if (saved2.Width > 0 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved2))) Bounds = saved2;
        else StartPosition = FormStartPosition.WindowsDefaultLocation;
        _onTop.Checked = TopMost = settings.RtspOnTop;

        _connect.Click += (_, _) =>
        {
            if (_cts != null) Stop("Disconnected");
            else Start();
        };
        foreach (var box in new[] { _ip, _profile })
            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Enter || _cts != null) return;
                e.Handled = e.SuppressKeyPress = true;
                Start();
            };
        _snapshot.Click += (_, _) => SaveSnapshot();
        _onTop.CheckedChanged += (_, _) => TopMost = _onTop.Checked;
        _video.DoubleClick += (_, _) => ToggleFullScreen();
        _statsTimer.Tick += (_, _) => UpdateStats();
        _video.Message = "Enter the camera IP and profile, then press Connect, for example:\n\n" +
                         "IP  192.168.43.1      Profile  profile1\n\n" +
                         "H.264 video over RTSP (TCP).  Double-click the picture for full screen.";
        SetState("Not connected");
    }

    // ------------------------------------------------------------------ start / stop

    void Start()
    {
        var url = BuildAddress();
        if (url == null)
        {
            _ip.Focus();
            return;
        }
        int generation = ++_generation;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _connect.Text = "Disconnect";
        _ip.Enabled = _profile.Enabled = false;
        _frames = 0;
        _lastBytes = 0;
        _statsTimer.Start();
        new Thread(() => Work(url, generation, token)) { IsBackground = true, Name = "rtsp" }.Start();
    }

    void Stop(string state)
    {
        _generation++;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _client = null;
        _statsTimer.Stop();
        _connect.Text = "Connect";
        _ip.Enabled = _profile.Enabled = true;
        _snapshot.Enabled = false;
        _stInfo.Text = "";
        _video.SetImage(null);
        SetState(state);
    }

    void SetState(string text) => _stState.Text = text;

    void Post(int generation, Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (generation == _generation && !IsDisposed) action();
            });
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>Background thread: connect, decode, and reconnect after network errors until stopped.</summary>
    void Work(string url, int generation, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string problem;
            try
            {
                using var client = new RtspClient(url);
                using var decoder = new H264Decoder();
                _client = client;
                Post(generation, () => SetState($"Connecting to {client.DisplayUrl} ..."));
                client.FrameReceived += (data, timestamp, _) => decoder.Decode(data, timestamp * 1000L / 9, Rent, (buf, w, h) => Ready(buf, w, h, generation));
                client.Run(ct, () => Post(generation, () =>
                {
                    SetState($"Playing {client.DisplayUrl}");
                    Remember(url);
                }));
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (RtspException ex) when (ex.Fatal)
            {
                Post(generation, () =>
                {
                    Stop("Not connected");
                    MessageBox.Show(this, ex.Message, "NXTerm: RTSP viewer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
                return;
            }
            catch (RtspException ex) { problem = ex.Message; }

            if (ct.IsCancellationRequested) return;
            Post(generation, () => SetState($"{problem}  Trying again in 3 seconds..."));
            if (ct.WaitHandle.WaitOne(3000)) return;
        }
    }

    // ------------------------------------------------------------------ frames

    byte[] Rent(int width, int height)
    {
        lock (_frameLock)
        {
            var spare = _spare;
            _spare = null;
            return spare != null && spare.Length == width * height * 4 ? spare : new byte[width * height * 4];
        }
    }

    void Ready(byte[] bgra, int width, int height, int generation)
    {
        bool post;
        lock (_frameLock)
        {
            if (_pending != null) _spare = _pending;  // the window is slower than the camera: drop the older picture
            _pending = bgra;
            _pendingWidth = width;
            _pendingHeight = height;
            post = !_posted;
            _posted = true;
        }
        if (!post || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => Present(generation)); }
        catch (InvalidOperationException) { }
    }

    void Present(int generation)
    {
        byte[] data;
        int w, h;
        lock (_frameLock)
        {
            _posted = false;
            if (_pending == null) return;
            data = _pending;
            w = _pendingWidth;
            h = _pendingHeight;
            _pending = null;
            if (generation != _generation || IsDisposed)
            {
                _spare ??= data;  // a late picture from a stream that was already stopped
                return;
            }
        }
        if (_bitmap == null || _bitmap.Width != w || _bitmap.Height != h)
        {
            _video.SetImage(null);
            _bitmap?.Dispose();
            _bitmap = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        }
        var bits = _bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            if (bits.Stride == w * 4) Marshal.Copy(data, 0, bits.Scan0, w * h * 4);
            else
                for (int y = 0; y < h; y++) Marshal.Copy(data, y * w * 4, bits.Scan0 + y * bits.Stride, w * 4);
        }
        finally { _bitmap.UnlockBits(bits); }
        lock (_frameLock) _spare ??= data;
        _frames++;
        _snapshot.Enabled = true;
        _video.SetImage(_bitmap);
    }

    void UpdateStats()
    {
        var client = _client;
        if (client == null || _bitmap == null)
        {
            _frames = 0;
            return;
        }
        long bytes = client.BytesReceived;
        double mbit = Math.Max(0, bytes - _lastBytes) * 8 / 1e6;
        _lastBytes = bytes;
        _stInfo.Text = $"{_bitmap.Width}x{_bitmap.Height}   {_frames} fps   {mbit:0.0} Mbit/s";
        _frames = 0;
    }

    // ------------------------------------------------------------------ extras

    void Remember(string url)
    {
        var list = _history.Where(u => !u.Equals(url, StringComparison.OrdinalIgnoreCase)).Prepend(url).Take(MaxHistory).ToList();
        _history.Clear();
        _history.AddRange(list);
        FillHistory();
        ShowAddress(url);
        _settings.RtspHistory = list.Select(u => Secrets.Protect(u, Entropy)).ToList();
        _saveSettings();
    }

    // ------------------------------------------------------------------ address

    /// <summary>The rtsp:// address from the IP and Profile boxes, or null when there is no IP.</summary>
    string? BuildAddress()
    {
        var ip = _ip.Text.Trim();
        if (ip.Length == 0) return null;
        if (ip.Contains("://"))
        {
            // A whole address was pasted into the IP box: split it into the two boxes.
            ShowAddress(ip);
            return ip;
        }
        var profile = _profile.Text.Trim().TrimStart('/');
        return $"rtsp://{ip}/{profile}";
    }

    /// <summary>Splits rtsp://[login@]host[:port]/profile into the part for the IP box and the part for the Profile box.</summary>
    public static (string Host, string Profile) SplitAddress(string url)
    {
        var rest = url.Trim();
        int scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) rest = rest[(scheme + 3)..];
        int slash = rest.IndexOf('/', rest.LastIndexOf('@') + 1);
        return slash < 0 ? (rest, "") : (rest[..slash], rest[(slash + 1)..]);
    }

    void ShowAddress(string url)
    {
        var (host, profile) = SplitAddress(url);
        _ip.Text = host;
        _profile.Text = profile;
    }

    void FillHistory()
    {
        _ip.Items.Clear();
        foreach (var host in _history.Select(u => SplitAddress(u).Host).Distinct(StringComparer.OrdinalIgnoreCase))
            _ip.Items.Add(host);
        var profiles = _history.Select(u => SplitAddress(u).Profile).Concat(CommonProfiles)
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _profile.Items.Clear();
        foreach (var x in profiles) _profile.Items.Add(x);
    }

    void SaveSnapshot()
    {
        if (_bitmap == null) return;
        using var copy = (Bitmap)_bitmap.Clone();
        using var sfd = new SaveFileDialog
        {
            Title = "Save snapshot",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FileName = $"nxterm_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            copy.Save(sfd.FileName, sfd.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Jpeg : ImageFormat.Png);
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Cannot save the picture:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            _normalState = WindowState;
            _normalBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _bar.Visible = _status.Visible = false;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = _normalBounds;
            WindowState = _normalState;
            _bar.Visible = _status.Visible = true;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11 || (keyData == Keys.Escape && _fullScreen))
        {
            ToggleFullScreen();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_cts != null) Stop("Not connected");
        if (_fullScreen) ToggleFullScreen();
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.RtspWindowBounds = [b.X, b.Y, b.Width, b.Height];
        _settings.RtspOnTop = _onTop.Checked;
        _saveSettings();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statsTimer.Dispose();
            _bitmap?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Draws the current picture scaled to fit, keeping its shape.</summary>
    sealed class VideoPanel : Control
    {
        Bitmap? _image;
        public string Message = "";

        public VideoPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.StandardDoubleClick, true);
            BackColor = Color.Black;
        }

        public void SetImage(Bitmap? image)
        {
            _image = image;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.Black);
            if (_image == null)
            {
                TextRenderer.DrawText(g, Message, Font, ClientRectangle, Color.Gray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            double scale = Math.Min(ClientSize.Width / (double)_image.Width, ClientSize.Height / (double)_image.Height);
            int w = Math.Max(1, (int)(_image.Width * scale)), h = Math.Max(1, (int)(_image.Height * scale));
            var target = new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.InterpolationMode = Math.Abs(scale - 1) < 0.01 ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            g.DrawImage(_image, target);
        }
    }
}
