using Renci.SshNet;
using Renci.SshNet.Common;

namespace NXTerm.Connections;

/// <summary>SSH2 interactive shell (password, keyboard-interactive or public key) via SSH.NET.</summary>
/// <param name="confirmHostKey">Called on a background thread with ("host:port", "type SHA256:fingerprint"); return true to trust.</param>
public sealed class SshConnection(ConnectionParams p, string termType, Func<string, string, bool> confirmHostKey) : IConnection
{
    readonly CancellationTokenSource _cts = new();
    readonly object _sendLock = new();
    SshClient? _client;
    ShellStream? _shell;
    ConnectionInfo? _info;
    string? _trustedFingerprint;

    public bool IsConnected => _client?.IsConnected == true && !_cts.IsCancellationRequested;
    public event Action<byte[], int>? DataReceived;
    public event Action<string?>? Disconnected;

    string HostId => $"{p.Host}:{p.Port}";

    public async Task ConnectAsync(int cols, int rows, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var methods = new List<AuthenticationMethod>();
            if (!string.IsNullOrEmpty(p.KeyFile))
            {
                var key = string.IsNullOrEmpty(p.Password) ? new PrivateKeyFile(p.KeyFile) : new PrivateKeyFile(p.KeyFile, p.Password);
                methods.Add(new PrivateKeyAuthenticationMethod(p.User, key));
            }
            else
            {
                methods.Add(new PasswordAuthenticationMethod(p.User, p.Password));
                var kbd = new KeyboardInteractiveAuthenticationMethod(p.User);
                kbd.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts) prompt.Response = p.Password;
                };
                methods.Add(kbd);
            }
            _info = new ConnectionInfo(p.Host, p.Port, p.User, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(15) };
            _client = new SshClient(_info);
            _client.HostKeyReceived += (_, e) =>
            {
                var fp = $"{e.HostKeyName} SHA256:{e.FingerPrintSHA256}";
                e.CanTrust = confirmHostKey(HostId, fp);
                if (e.CanTrust) _trustedFingerprint = fp;
            };
            _client.ErrorOccurred += (_, e) =>
            {
                if (!_cts.IsCancellationRequested) Disconnected?.Invoke(e.Exception.Message);
            };
            _client.Connect();
            _shell = _client.CreateShellStream(termType, (uint)cols, (uint)rows, 0, 0, 65536);
        }, ct);
        new Thread(ReadLoop) { IsBackground = true, Name = "ssh-read" }.Start();
    }

    void ReadLoop()
    {
        var buf = new byte[16384];
        string? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = _shell!.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                DataReceived?.Invoke(buf, n);
            }
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            error = ex.Message;
        }
        if (!_cts.IsCancellationRequested) Disconnected?.Invoke(error);
    }

    public void Send(byte[] data)
    {
        var s = _shell;
        if (s == null) return;
        try
        {
            lock (_sendLock)
            {
                s.Write(data, 0, data.Length);
                s.Flush();
            }
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException) { }
    }

    public void Resize(int cols, int rows)
    {
        try { _shell?.ChangeWindowSize((uint)cols, (uint)rows, 0, 0); }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException) { }
    }

    public void SendBreak() { }

    ScpClient NewScp()
    {
        if (_info == null) throw new InvalidOperationException("Not connected.");
        var scp = new ScpClient(_info, RemotePathTransformation.ShellQuote);
        // Same server as the shell session: only accept the key we already trusted.
        scp.HostKeyReceived += (_, e) => e.CanTrust = $"{e.HostKeyName} SHA256:{e.FingerPrintSHA256}" == _trustedFingerprint;
        return scp;
    }

    public void ScpUpload(string localPath, string remotePath, Action<long, long>? progress, CancellationToken ct)
    {
        using var scp = NewScp();
        if (progress != null) scp.Uploading += (_, e) => progress(e.Uploaded, e.Size);
        using var reg = ct.Register(() => scp.Dispose());
        scp.Connect();
        using var fs = File.OpenRead(localPath);
        if (remotePath.Length == 0 || remotePath.EndsWith('/')) remotePath += Path.GetFileName(localPath);
        scp.Upload(fs, remotePath);
    }

    public void ScpDownload(string remotePath, string localPath, Action<long, long>? progress, CancellationToken ct)
    {
        using var scp = NewScp();
        if (progress != null) scp.Downloading += (_, e) => progress(e.Downloaded, e.Size);
        using var reg = ct.Register(() => scp.Dispose());
        scp.Connect();
        using var fs = File.Create(localPath);
        scp.Download(remotePath, fs);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _shell?.Dispose();
            _client?.Disconnect();
            _client?.Dispose();
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException) { }
    }
}
