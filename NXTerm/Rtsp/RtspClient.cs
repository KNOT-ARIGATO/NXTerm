using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NXTerm.Rtsp;

/// <param name="fatal">True when retrying cannot help (wrong password, unsupported video format, bad address).</param>
public sealed class RtspException(string message, bool fatal = false) : Exception(message)
{
    public bool Fatal { get; } = fatal;
}

/// <summary>
/// Minimal RTSP 1.0 client for IP cameras: DESCRIBE / SETUP / PLAY with RTP over the same TCP connection
/// (interleaved), Basic and Digest authentication, H.264 video.
/// </summary>
public sealed class RtspClient : IDisposable
{
    sealed record Response(int Code, string Reason, Dictionary<string, List<string>> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v[0] : null;
    }

    readonly string _host, _user, _password, _requestUrl;
    readonly int _port;
    readonly H264Depacketizer _depacketizer = new();
    readonly object _writeLock = new();

    TcpClient? _tcp;
    Stream? _read;
    NetworkStream? _write;
    int _cseq, _nonceCount, _rtpChannel;
    string? _session, _authScheme, _realm, _nonce, _qop, _opaque;
    bool _triedAuth, _supportsGetParameter;
    int _sessionTimeout = 60;
    long _bytes;

    /// <summary>Complete pictures as Annex B data, with the RTP timestamp (90 kHz) and a key frame flag. Raised on the reading thread.</summary>
    public event Action<byte[], uint, bool>? FrameReceived
    {
        add => _depacketizer.FrameReady += value;
        remove => _depacketizer.FrameReady -= value;
    }

    public long BytesReceived => Interlocked.Read(ref _bytes);

    public RtspClient(string url)
    {
        url = url.Trim();
        if (!url.Contains("://")) url = "rtsp://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase) || uri.Host.Length == 0)
            throw new RtspException("The address must look like  rtsp://user:password@192.168.1.10:554/stream", fatal: true);
        _host = uri.Host;
        _port = uri.Port > 0 ? uri.Port : 554;
        var info = uri.UserInfo.Split(':', 2);
        _user = Uri.UnescapeDataString(info[0]);
        _password = info.Length > 1 ? Uri.UnescapeDataString(info[1]) : "";
        _requestUrl = $"rtsp://{(uri.HostNameType == UriHostNameType.IPv6 ? $"[{_host}]" : _host)}:{_port}{uri.PathAndQuery}";
    }

    /// <summary>The address without the user name and password, safe to show on screen.</summary>
    public string DisplayUrl => _requestUrl;

    /// <summary>Connects, starts the stream and keeps reading until cancelled. Throws when the stream fails.</summary>
    public void Run(CancellationToken ct, Action? onPlaying = null)
    {
        _tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 10000, SendTimeout = 10000 };
        using var closeOnCancel = ct.Register(() => _tcp.Close());
        try
        {
            try
            {
                if (!_tcp.ConnectAsync(_host, _port).Wait(8000, ct)) throw new RtspException($"No answer from {_host}:{_port}");
            }
            catch (AggregateException ex) when (ex.InnerException is SocketException se)
            {
                throw new RtspException($"Cannot connect to {_host}:{_port}: {se.Message}");
            }
            _write = _tcp.GetStream();
            _read = new BufferedStream(_write, 128 * 1024);

            var options = Request("OPTIONS", _requestUrl);
            _supportsGetParameter = options.Header("Public")?.Contains("GET_PARAMETER", StringComparison.OrdinalIgnoreCase) == true;

            var describe = Request("DESCRIBE", _requestUrl, ("Accept", "application/sdp"));
            string baseUrl = describe.Header("Content-Base") ?? describe.Header("Content-Location") ?? _requestUrl;
            var (trackControl, sessionControl, sps, pps) = ParseSdp(describe.Body);
            _depacketizer.SetParameterSets(sps, pps);

            var setup = Request("SETUP", Resolve(baseUrl, trackControl), ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"));
            var session = setup.Header("Session") ?? throw new RtspException("The camera did not open a session.");
            _session = session.Split(';')[0].Trim();
            var timeout = Regex.Match(session, @"timeout\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            if (timeout.Success) _sessionTimeout = Math.Max(10, int.Parse(timeout.Groups[1].Value));
            var channel = Regex.Match(setup.Header("Transport") ?? "", @"interleaved\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            if (channel.Success) _rtpChannel = int.Parse(channel.Groups[1].Value);

            Request("PLAY", sessionControl is { } sc && sc.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ? sc : baseUrl, ("Range", "npt=0.000-"));
            onPlaying?.Invoke();

            long keepAliveEvery = Math.Min(_sessionTimeout / 2, 25) * 1000L, lastKeepAlive = Environment.TickCount64;
            while (!ct.IsCancellationRequested)
            {
                ReadMessage();  // RTP is handled inside; replies to keep-alives are simply dropped
                if (Environment.TickCount64 - lastKeepAlive >= keepAliveEvery)
                {
                    lastKeepAlive = Environment.TickCount64;
                    Send(_supportsGetParameter ? "GET_PARAMETER" : "OPTIONS", _requestUrl);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            ct.ThrowIfCancellationRequested();
            throw new RtspException(ex is IOException { InnerException: SocketException { SocketErrorCode: SocketError.TimedOut } }
                ? "The camera stopped sending video."
                : "Connection lost: " + ((ex.InnerException as SocketException)?.Message ?? ex.Message).TrimEnd('.') + ".");
        }
        finally
        {
            if (_session != null && _tcp.Connected)
            {
                try { Send("TEARDOWN", _requestUrl); }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
            }
        }
        ct.ThrowIfCancellationRequested();
    }

    // ------------------------------------------------------------------ SDP

    static (string Track, string? Session, byte[]? Sps, byte[]? Pps) ParseSdp(string sdp)
    {
        string? sessionControl = null, trackControl = null, fmtp = null;
        var codecs = new Dictionary<string, string>();
        bool inVideo = false, seenMedia = false, done = false;
        foreach (var raw in sdp.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                if (inVideo) done = true;  // only the first video track is used
                seenMedia = true;
                inVideo = !done && line.StartsWith("m=video", StringComparison.Ordinal);
            }
            else if (line.StartsWith("a=control:", StringComparison.Ordinal))
            {
                if (inVideo) trackControl = line[10..].Trim();
                else if (!seenMedia) sessionControl = line[10..].Trim();
            }
            else if (inVideo && line.StartsWith("a=rtpmap:", StringComparison.Ordinal))
            {
                var parts = line[9..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) codecs[parts[0]] = parts[1].Split('/')[0].ToUpperInvariant();
            }
            else if (inVideo && line.StartsWith("a=fmtp:", StringComparison.Ordinal)) fmtp = line;
        }
        if (codecs.Count == 0 && trackControl == null) throw new RtspException("This address has no video stream.", fatal: true);
        if (!codecs.ContainsValue("H264"))
        {
            var found = string.Join(", ", codecs.Values.Distinct());
            throw new RtspException(
                $"The camera sends {(found.Length > 0 ? found : "an unknown format")}, but only H.264 is supported.\n" +
                "Change the camera's video encoding to H.264, or use its H.264 sub-stream.", fatal: true);
        }

        byte[]? sps = null, pps = null;
        var sprop = Regex.Match(fmtp ?? "", @"sprop-parameter-sets\s*=\s*([^;\s]+)", RegexOptions.IgnoreCase);
        if (sprop.Success)
        {
            var sets = sprop.Groups[1].Value.Split(',');
            try
            {
                sps = Convert.FromBase64String(sets[0]);
                if (sets.Length > 1) pps = Convert.FromBase64String(sets[1]);
            }
            catch (FormatException) { sps = pps = null; }
        }
        return (trackControl ?? "", sessionControl, sps, pps);
    }

    static string Resolve(string baseUrl, string control)
    {
        if (control.Length == 0 || control == "*") return baseUrl;
        if (control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)) return control;
        return baseUrl.TrimEnd('/') + "/" + control.TrimStart('/');
    }

    // ------------------------------------------------------------------ requests

    void Send(string method, string url, params (string Name, string Value)[] headers)
    {
        var sb = new StringBuilder();
        sb.Append($"{method} {url} RTSP/1.0\r\nCSeq: {++_cseq}\r\nUser-Agent: NXTerm\r\n");
        if (_session != null) sb.Append($"Session: {_session}\r\n");
        if (_authScheme != null) sb.Append($"Authorization: {Authorization(method, url)}\r\n");
        foreach (var (name, value) in headers) sb.Append($"{name}: {value}\r\n");
        sb.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        lock (_writeLock) _write!.Write(bytes);
    }

    Response Request(string method, string url, params (string Name, string Value)[] headers)
    {
        while (true)
        {
            Send(method, url, headers);
            Response? r;
            do r = ReadMessage(); while (r == null);  // video packets may arrive before the reply

            if (r.Code == 401)
            {
                if (_user.Length == 0) throw new RtspException("The camera needs a user name and password.\nUse  rtsp://user:password@address/...", fatal: true);
                if (_triedAuth) throw new RtspException("The camera rejected the user name or password.", fatal: true);
                _triedAuth = true;
                var challenges = r.Headers.GetValueOrDefault("WWW-Authenticate") ?? [];
                var challenge = challenges.FirstOrDefault(c => c.StartsWith("Digest", StringComparison.OrdinalIgnoreCase)) ?? challenges.FirstOrDefault()
                                ?? throw new RtspException("The camera asked for a login in a way that is not supported.", fatal: true);
                _authScheme = challenge.StartsWith("Digest", StringComparison.OrdinalIgnoreCase) ? "Digest" : "Basic";
                string? Param(string name) =>
                    Regex.Match(challenge, name + @"\s*=\s*(?:""([^""]*)""|([^,\s]+))", RegexOptions.IgnoreCase) is { Success: true } m
                        ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                        : null;
                _realm = Param("realm") ?? "";
                _nonce = Param("nonce") ?? "";
                _opaque = Param("opaque");
                _qop = Param("qop")?.Split(',').Select(q => q.Trim()).FirstOrDefault(q => q == "auth");
                continue;
            }
            if (r.Code == 404) throw new RtspException("The camera has no stream at this path (404). Check the part after the address.", fatal: true);
            if (r.Code is < 200 or >= 300) throw new RtspException($"The camera answered {r.Code} {r.Reason} to {method}.", fatal: r.Code is 400 or 403 or 405 or 415 or 461);
            return r;
        }
    }

    string Authorization(string method, string url)
    {
        if (_authScheme == "Basic") return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_password}"));
        static string Md5(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));
        string ha1 = Md5($"{_user}:{_realm}:{_password}"), ha2 = Md5($"{method}:{url}");
        var sb = new StringBuilder($"Digest username=\"{_user}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{url}\", ");
        if (_qop != null)
        {
            string nc = (++_nonceCount).ToString("x8"), cnonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            sb.Append($"qop=auth, nc={nc}, cnonce=\"{cnonce}\", response=\"{Md5($"{ha1}:{_nonce}:{nc}:{cnonce}:auth:{ha2}")}\"");
        }
        else sb.Append($"response=\"{Md5($"{ha1}:{_nonce}:{ha2}")}\"");
        if (_opaque != null) sb.Append($", opaque=\"{_opaque}\"");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Reads one message. RTP packets are passed to the depacketizer and null is returned; RTSP replies are returned.</summary>
    Response? ReadMessage()
    {
        int first = _read!.ReadByte();
        if (first < 0) throw new IOException("The camera closed the connection.");
        if (first == '$')
        {
            Span<byte> header = stackalloc byte[3];
            _read.ReadExactly(header);
            int length = (header[1] << 8) | header[2];
            var packet = new byte[length];
            _read.ReadExactly(packet);
            Interlocked.Add(ref _bytes, length + 4);
            if (header[0] == _rtpChannel) _depacketizer.OnRtp(packet);
            return null;
        }

        var status = ((char)first + ReadLine()).Trim();
        if (status.Length == 0) return null;
        var parts = status.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("RTSP/", StringComparison.Ordinal) || !int.TryParse(parts[1], out int code))
            throw new RtspException("This does not look like an RTSP camera (unexpected reply).", fatal: true);

        var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (string line = ReadLine(); line.Length > 0; line = ReadLine())
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            if (!headers.TryGetValue(name, out var list)) headers[name] = list = [];
            list.Add(line[(colon + 1)..].Trim());
        }
        string body = "";
        if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl[0], out int n) && n > 0)
        {
            if (n > 1 << 20) throw new RtspException("The camera sent an unreasonably large reply.", fatal: true);
            var buf = new byte[n];
            _read.ReadExactly(buf);
            body = Encoding.UTF8.GetString(buf);
        }
        return new Response(code, parts.Length > 2 ? parts[2] : "", headers, body);
    }

    string ReadLine()
    {
        var sb = new StringBuilder();
        while (true)
        {
            int b = _read!.ReadByte();
            if (b < 0) throw new IOException("The camera closed the connection.");
            if (b == '\n') return sb.ToString().TrimEnd('\r');
            if (sb.Length > 8192) throw new RtspException("This does not look like an RTSP camera (unexpected reply).", fatal: true);
            sb.Append((char)b);
        }
    }

    public void Dispose()
    {
        _tcp?.Close();
    }
}
