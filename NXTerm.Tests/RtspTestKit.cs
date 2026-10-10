using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// A tiny H.264 encoder for tests. Every picture is a key frame made of uncompressed (I_PCM) macroblocks,
/// which is valid Baseline H.264 that any decoder accepts, and needs no real compression code.
/// </summary>
static class TestH264
{
    sealed class BitWriter
    {
        readonly List<byte> _bytes = [];
        int _current, _count;

        public void U(int bits, uint value)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                _current = (_current << 1) | (int)((value >> i) & 1);
                if (++_count == 8)
                {
                    _bytes.Add((byte)_current);
                    _current = _count = 0;
                }
            }
        }

        public void UE(uint value)
        {
            uint x = value + 1;
            int length = 32 - BitOperations.LeadingZeroCount(x);
            U(length - 1, 0);
            U(length, x);
        }

        public void SE(int value) => UE(value > 0 ? (uint)(2 * value - 1) : (uint)(-2 * value));

        public void Align()
        {
            while (_count != 0) U(1, 0);
        }

        public void Raw(ReadOnlySpan<byte> data) => _bytes.AddRange(data);

        public byte[] Finish()
        {
            U(1, 1);
            Align();
            return _bytes.ToArray();
        }
    }

    /// <summary>Adds the NAL header and the emulation prevention bytes (00 00 0x becomes 00 00 03 0x).</summary>
    static byte[] Nal(byte header, byte[] payload)
    {
        var o = new List<byte>(payload.Length + 16) { header };
        int zeros = 0;
        foreach (byte b in payload)
        {
            if (zeros >= 2 && b <= 3)
            {
                o.Add(3);
                zeros = 0;
            }
            o.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return o.ToArray();
    }

    public static byte[] Sps(int widthMb, int heightMb)
    {
        var w = new BitWriter();
        w.U(8, 66);     // Baseline profile
        w.U(8, 0xC0);   // constraint_set0 and 1
        w.U(8, 31);     // level 3.1
        w.UE(0);        // seq_parameter_set_id
        w.UE(0);        // log2_max_frame_num_minus4
        w.UE(2);        // pic_order_cnt_type 2: output order = decoding order
        w.UE(1);        // max_num_ref_frames
        w.U(1, 0);      // gaps_in_frame_num_value_allowed_flag
        w.UE((uint)widthMb - 1);
        w.UE((uint)heightMb - 1);
        w.U(1, 1);      // frame_mbs_only_flag
        w.U(1, 1);      // direct_8x8_inference_flag
        w.U(1, 0);      // frame_cropping_flag
        w.U(1, 0);      // vui_parameters_present_flag
        return Nal(0x67, w.Finish());
    }

    public static byte[] Pps()
    {
        var w = new BitWriter();
        w.UE(0);        // pic_parameter_set_id
        w.UE(0);        // seq_parameter_set_id
        w.U(1, 0);      // entropy_coding_mode_flag (CAVLC)
        w.U(1, 0);      // bottom_field_pic_order_in_frame_present_flag
        w.UE(0);        // num_slice_groups_minus1
        w.UE(0);        // num_ref_idx_l0_default_active_minus1
        w.UE(0);        // num_ref_idx_l1_default_active_minus1
        w.U(1, 0);      // weighted_pred_flag
        w.U(2, 0);      // weighted_bipred_idc
        w.SE(0);        // pic_init_qp_minus26
        w.SE(0);        // pic_init_qs_minus26
        w.SE(0);        // chroma_qp_index_offset
        w.U(1, 0);      // deblocking_filter_control_present_flag
        w.U(1, 0);      // constrained_intra_pred_flag
        w.U(1, 0);      // redundant_pic_cnt_present_flag
        return Nal(0x68, w.Finish());
    }

    /// <summary>Colour of the test picture at a pixel: eight colour bars and a white square that moves every frame.</summary>
    public static (byte Y, byte U, byte V) Pixel(int x, int y, int width, int height, int frame)
    {
        int box = height / 5, bx = frame * 8 % Math.Max(1, width - box), by = height - box - 8;
        if (x >= bx && x < bx + box && y >= by && y < by + box) return (235, 128, 128);
        return Bars[Math.Min(7, x * 8 / width)];
    }

    /// <summary>White, yellow, cyan, green, magenta, red, blue, black (BT.601 video range).</summary>
    public static readonly (byte Y, byte U, byte V)[] Bars =
    [
        (235, 128, 128), (210, 16, 146), (170, 166, 16), (145, 54, 34),
        (106, 202, 222), (81, 90, 240), (41, 240, 110), (16, 128, 128),
    ];

    public static byte[] IdrSlice(int widthMb, int heightMb, int frame)
    {
        int width = widthMb * 16, height = heightMb * 16;
        var w = new BitWriter();
        w.UE(0);                    // first_mb_in_slice
        w.UE(7);                    // slice_type: I (all slices of the picture)
        w.UE(0);                    // pic_parameter_set_id
        w.U(4, 0);                  // frame_num
        w.UE((uint)(frame & 1));    // idr_pic_id must differ between consecutive key frames
        w.U(1, 0);                  // no_output_of_prior_pics_flag
        w.U(1, 0);                  // long_term_reference_flag
        w.SE(0);                    // slice_qp_delta
        Span<byte> mb = stackalloc byte[384];
        for (int my = 0; my < heightMb; my++)
        {
            for (int mx = 0; mx < widthMb; mx++)
            {
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                        mb[y * 16 + x] = Pixel(mx * 16 + x, my * 16 + y, width, height, frame).Y;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        var p = Pixel(mx * 16 + x * 2, my * 16 + y * 2, width, height, frame);
                        mb[256 + y * 8 + x] = p.U;
                        mb[320 + y * 8 + x] = p.V;
                    }
                }
                w.UE(25);           // mb_type I_PCM
                w.Align();          // pcm_alignment_zero_bit
                w.Raw(mb);
            }
        }
        return Nal(0x65, w.Finish());
    }

    /// <summary>One complete picture as an Annex B byte stream (SPS, PPS, key frame).</summary>
    public static byte[] AnnexBFrame(int widthMb, int heightMb, int frame)
    {
        var ms = new MemoryStream();
        foreach (var nal in new[] { Sps(widthMb, heightMb), Pps(), IdrSlice(widthMb, heightMb, frame) })
        {
            ms.Write([0, 0, 0, 1]);
            ms.Write(nal);
        }
        return ms.ToArray();
    }
}

/// <summary>A pretend IP camera: RTSP over TCP with interleaved RTP and optional Digest login, sending the test picture.</summary>
sealed class TestRtspServer : IDisposable
{
    readonly TcpListener _listener;
    readonly string? _user, _password;
    readonly int _widthMb, _heightMb, _fps;
    volatile bool _stopped;

    public int Port { get; }

    public TestRtspServer(int port = 0, string? user = null, string? password = null, int widthMb = 20, int heightMb = 15, int fps = 10)
    {
        _user = user;
        _password = password;
        _widthMb = widthMb;
        _heightMb = heightMb;
        _fps = fps;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(AcceptLoop) { IsBackground = true }.Start();
    }

    void AcceptLoop()
    {
        try
        {
            while (!_stopped)
            {
                var client = _listener.AcceptTcpClient();
                new Thread(() => Serve(client)) { IsBackground = true }.Start();
            }
        }
        catch (SocketException) { }
    }

    static string Md5(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    bool Authorized(string method, string? authorization)
    {
        if (_user == null) return true;
        if (authorization == null || !authorization.StartsWith("Digest", StringComparison.OrdinalIgnoreCase)) return false;
        string P(string name) => Regex.Match(authorization, name + "=\"([^\"]*)\"").Groups[1].Value;
        string expected = Md5($"{Md5($"{_user}:{P("realm")}:{_password}")}:{P("nonce")}:{Md5($"{method}:{P("uri")}")}");
        return P("username") == _user && P("response") == expected;
    }

    void Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var writeLock = new object();
        var playing = new CancellationTokenSource();
        try
        {
            while (!_stopped)
            {
                var sb = new StringBuilder();
                while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    int b = stream.ReadByte();
                    if (b < 0) return;
                    sb.Append((char)b);
                }
                var lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var first = lines[0].Split(' ');
                string method = first[0], url = first[1];
                string? Header(string name) => lines.Skip(1).FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim();

                void Reply(string status, string headers = "", string body = "")
                {
                    var text = $"RTSP/1.0 {status}\r\nCSeq: {Header("CSeq")}\r\n{headers}" +
                               (body.Length > 0 ? $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" : "") + "\r\n" + body;
                    lock (writeLock) stream.Write(Encoding.UTF8.GetBytes(text));
                }

                if (method != "OPTIONS" && !Authorized(method, Header("Authorization")))
                {
                    Reply("401 Unauthorized", "WWW-Authenticate: Digest realm=\"NXTermTest\", nonce=\"7f3a9c21d4e5\"\r\n");
                    continue;
                }
                switch (method)
                {
                    case "OPTIONS":
                    case "GET_PARAMETER":
                        Reply("200 OK", "Public: OPTIONS, DESCRIBE, SETUP, PLAY, TEARDOWN, GET_PARAMETER\r\n");
                        break;
                    case "DESCRIBE":
                        {
                            string sprop = Convert.ToBase64String(TestH264.Sps(_widthMb, _heightMb)) + "," + Convert.ToBase64String(TestH264.Pps());
                            string sdp = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=NXTerm test camera\r\nt=0 0\r\na=control:*\r\n" +
                                         "m=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\n" +
                                         $"a=fmtp:96 packetization-mode=1;sprop-parameter-sets={sprop}\r\na=control:track1\r\n";
                            Reply("200 OK", $"Content-Base: {url.TrimEnd('/')}/\r\nContent-Type: application/sdp\r\n", sdp);
                            break;
                        }
                    case "SETUP":
                        if (!url.EndsWith("/track1", StringComparison.Ordinal)) { Reply("404 Not Found"); break; }
                        Reply("200 OK", "Session: 4F2A9C1B;timeout=60\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n");
                        break;
                    case "PLAY":
                        Reply("200 OK", "Session: 4F2A9C1B\r\n");
                        new Thread(() => SendVideo(stream, writeLock, playing.Token)) { IsBackground = true }.Start();
                        break;
                    case "TEARDOWN":
                        Reply("200 OK");
                        return;
                    default:
                        Reply("405 Method Not Allowed");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
        finally { playing.Cancel(); }
    }

    void SendVideo(NetworkStream stream, object writeLock, CancellationToken ct)
    {
        ushort sequence = 1000;
        uint timestamp = 123456;
        void Rtp(ReadOnlySpan<byte> payload, bool marker)
        {
            var packet = new byte[4 + 12 + payload.Length];
            packet[0] = (byte)'$';
            packet[1] = 0;
            packet[2] = (byte)((12 + payload.Length) >> 8);
            packet[3] = (byte)(12 + payload.Length);
            packet[4] = 0x80;
            packet[5] = (byte)(96 | (marker ? 0x80 : 0));
            packet[6] = (byte)(sequence >> 8);
            packet[7] = (byte)sequence++;
            for (int i = 0; i < 4; i++) packet[8 + i] = (byte)(timestamp >> (24 - 8 * i));
            packet[12] = 0x12; packet[13] = 0x34; packet[14] = 0x56; packet[15] = 0x78;
            payload.CopyTo(packet.AsSpan(16));
            lock (writeLock) stream.Write(packet);
        }

        try
        {
            byte[] sps = TestH264.Sps(_widthMb, _heightMb), pps = TestH264.Pps();
            for (int frame = 0; !ct.IsCancellationRequested && !_stopped; frame++)
            {
                // SPS + PPS together in one STAP-A packet
                var stap = new List<byte> { 24 };
                foreach (var nal in new[] { sps, pps })
                {
                    stap.Add((byte)(nal.Length >> 8));
                    stap.Add((byte)nal.Length);
                    stap.AddRange(nal);
                }
                Rtp(stap.ToArray(), false);

                // the picture itself, split into FU-A fragments
                var idr = TestH264.IdrSlice(_widthMb, _heightMb, frame);
                const int chunk = 1400;
                for (int offset = 1; offset < idr.Length; offset += chunk)
                {
                    int n = Math.Min(chunk, idr.Length - offset);
                    bool start = offset == 1, end = offset + n >= idr.Length;
                    var fu = new byte[2 + n];
                    fu[0] = (byte)((idr[0] & 0xE0) | 28);
                    fu[1] = (byte)((start ? 0x80 : 0) | (end ? 0x40 : 0) | (idr[0] & 0x1F));
                    Array.Copy(idr, offset, fu, 2, n);
                    Rtp(fu, end);
                }
                timestamp += (uint)(90000 / _fps);
                Thread.Sleep(1000 / _fps);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _stopped = true;
        _listener.Stop();
    }
}
