namespace NXTerm.Updates;

public enum UpdateChoice { Later, Skip, Installed }

public sealed class UpdateDialog : Form
{
    readonly ReleaseInfo _release;
    readonly Button _update, _page, _skip, _later;
    readonly ProgressBar _bar = new() { Width = 480, Visible = false, Maximum = 1000 };
    readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    CancellationTokenSource? _cts;

    public UpdateChoice Choice { get; private set; } = UpdateChoice.Later;

    public UpdateDialog(ReleaseInfo release)
    {
        _release = release;
        Text = "NXTerm: Update available";
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(14) };
        t.Controls.Add(new Label { Text = $"NXTerm {release.Version.ToString(3)} is available", Font = new Font(Font.FontFamily, Font.Size * 1.3f, FontStyle.Bold), AutoSize = true });
        t.Controls.Add(new Label { Text = $"You have version {Updater.CurrentVersionText}.", AutoSize = true, Margin = new Padding(3, 4, 3, 10) });
        t.Controls.Add(new Label { Text = "What's new:", AutoSize = true });
        t.Controls.Add(new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Width = 480,
            Height = 180,
            Text = release.Notes.Length > 0 ? release.Notes.Replace("\r\n", "\n").Replace("\n", "\r\n") : "(no release notes)",
            BackColor = SystemColors.Window,
        });
        if (release.AssetUrl == null)
            _status.Text = "This release has no NXTerm.exe attached: use \"Release page\" to download it.";
        t.Controls.Add(_status);
        t.Controls.Add(_bar);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        _later = new Button { Text = "Later", AutoSize = true, MinimumSize = new Size(80, 0) };
        _skip = new Button { Text = "Skip this version", AutoSize = true };
        _page = new Button { Text = "Release page", AutoSize = true };
        _update = new Button { Text = "Update now", AutoSize = true, MinimumSize = new Size(100, 0), Enabled = release.AssetUrl != null };
        buttons.Controls.AddRange([_later, _skip, _page, _update]);
        t.Controls.Add(buttons);
        Controls.Add(t);
        AcceptButton = _update;
        CancelButton = _later;
        ActiveControl = _update.Enabled ? _update : _page;

        _later.Click += (_, _) => Close();
        _skip.Click += (_, _) =>
        {
            Choice = UpdateChoice.Skip;
            Close();
        };
        _page.Click += (_, _) => Updater.OpenInBrowser(release.PageUrl);
        _update.Click += async (_, _) => await Install();
    }

    async Task Install()
    {
        foreach (var b in new[] { _update, _page, _skip }) b.Enabled = false;
        _later.Text = "Cancel";
        _bar.Visible = true;
        _status.Text = "Downloading...";
        _cts = new CancellationTokenSource();
        var progress = new Progress<long>(done =>
        {
            if (_release.AssetSize > 0) _bar.Value = (int)Math.Clamp(done * 1000 / _release.AssetSize, 0, 1000);
            _status.Text = $"Downloading... {done / 1024:N0} KB" + (_release.AssetSize > 0 ? $" of {_release.AssetSize / 1024:N0} KB" : "");
        });
        try
        {
            await Updater.InstallAsync(_release, progress, _cts.Token);
            Choice = UpdateChoice.Installed;
            MessageBox.Show(this, $"NXTerm was updated to {_release.Version.ToString(3)}.\n\nIt will now restart.", "NXTerm: Update installed",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) Close();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException)
        {
            var hint = ex is UnauthorizedAccessException
                ? "\n\nNXTerm cannot replace its own file in this folder (for example Program Files). Download the new version from the release page instead."
                : "";
            if (MessageBox.Show(this, "The update failed:\n" + ex.Message + hint + "\n\nOpen the release page to download it manually?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                Updater.OpenInBrowser(_release.PageUrl);
            Close();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnFormClosing(e);
    }
}
