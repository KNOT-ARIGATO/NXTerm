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
    readonly ComboBox _url = new() { Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 3) };
    readonly Button _connect = new() { Text = "Connect", AutoSize = true, MinimumSize = new Size(90, 0) };
    readonly Button _snapshot = new() { Text = "Snapshot", AutoSize = true, Enabled = false };
    readonly CheckBox _onTop = new() { Text = "Always on top", AutoSize = true, Margin = new Padding(8, 7, 3, 3) };
    readonly TableLayoutPanel _bar = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, Padding = new Padding(4, 2, 4, 2) };
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
        _bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _bar.Controls.Add(new Label { Text = "Address:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 0);
        _bar.Controls.Add(_url, 1, 0);
        _bar.Controls.Add(_connect, 2, 0);
        _bar.Controls.Add(_snapshot, 3, 0);
        _bar.Controls.Add(_onTop, 4, 0);
        _status.Items.AddRange([_stState, _stInfo]);
        Controls.Add(_video);
        Controls.Add(_bar);
        Controls.Add(_status);

        foreach (var saved in settings.RtspHistory)
            if (Secrets.Unprotect(saved, Entropy) is { Length: > 0 } url) _url.Items.Add(url);
        if (_url.Items.Count > 0) _url.SelectedIndex = 0;

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
        _url.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || _cts != null) return;
            e.Handled = e.SuppressKeyPress = true;
            Start();
        };
        _snapshot.Click += (_, _) => SaveSnapshot();
        _onTop.CheckedChanged += (_, _) => TopMost = _onTop.Checked;
        _video.DoubleClick += (_, _) => ToggleFullScreen();
        _statsTimer.Tick += (_, _) => UpdateStats();
        _video.Message = "Enter the camera address and press Connect, for example:\n\n" +
                         "rtsp://user:password@192.168.1.10:554/stream1\n\n" +
                         "H.264 video over RTSP (TCP).  Double-click the picture for full screen.";
        SetState("Not connected");
    }

    // ------------------------------------------------------------------ start / stop

    void Start()
    {
        var url = _url.Text.Trim();
        if (url.Length == 0)
        {
            _url.Focus();
            return;
        }
        int generation = ++_generation;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _connect.Text = "Disconnect";
        _url.Enabled = false;
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
        _url.Enabled = true;
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
        var list = _url.Items.Cast<string>().Where(u => !u.Equals(url, StringComparison.OrdinalIgnoreCase)).Prepend(url).Take(MaxHistory).ToList();
        _url.Items.Clear();
        foreach (var u in list) _url.Items.Add(u);
        _url.Text = url;
        _settings.RtspHistory = list.Select(u => Secrets.Protect(u, Entropy)).ToList();
        _saveSettings();
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
