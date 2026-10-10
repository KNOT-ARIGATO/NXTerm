using System.Net.Sockets;
using System.Text;

namespace NXTerm.Connections;

/// <summary>Telnet (RFC 854 with NAWS/TTYPE/ECHO/SGA negotiation) or raw TCP socket.</summary>
public sealed class TcpConnection(string host, int port, bool telnet, string termType) : IConnection
{
    const byte SE = 240, BRK = 243, SB = 250, WILL = 251, WONT = 252, DO = 253, DONT = 254, IAC = 255;
    const byte OptBinary = 0, OptEcho = 1, OptSga = 3, OptTType = 24, OptNaws = 31;

    readonly CancellationTokenSource _cts = new();
    readonly object _sendLock = new();
    readonly HashSet<byte> _local = [], _remote = [];
    readonly List<byte> _sb = [];
    TcpClient? _client;
    NetworkStream? _stream;
    int _cols = 80, _rows = 24;

    enum TS { Data, Cr, Iac, Opt, Sb, SbIac }
    TS _ts;
    byte _verb;

    public bool IsConnected => _client?.Connected == true && !_cts.IsCancellationRequested;
    public event Action<byte[], int>? DataReceived;
    public event Action<string?>? Disconnected;

    public async Task ConnectAsync(int cols, int rows, CancellationToken ct = default)
    {
        _cols = cols;
        _rows = rows;
        _client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await _client.ConnectAsync(host, port, timeout.Token);
        _stream = _client.GetStream();
        _ = Task.Run(ReadLoop);
    }

    async Task ReadLoop()
    {
        var buf = new byte[16384];
        string? error = null;
        try
        {
            while (true)
            {
                int n = await _stream!.ReadAsync(buf, _cts.Token);
                if (n <= 0) break;
                if (telnet) n = ProcessTelnet(buf, n);
                if (n > 0) DataReceived?.Invoke(buf, n);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            error = ex.Message;
        }
        if (!_cts.IsCancellationRequested) Disconnected?.Invoke(error);
    }

    int ProcessTelnet(byte[] buf, int n)
    {
        int w = 0;
        for (int i = 0; i < n; i++)
        {
            byte b = buf[i];
            switch (_ts)
            {
                case TS.Data:
                    if (b == IAC) _ts = TS.Iac;
                    else
                    {
                        buf[w++] = b;
                        if (b == 13) _ts = TS.Cr;
                    }
                    break;
                case TS.Cr:
                    _ts = TS.Data;
                    if (b == 0) break;           // CR NUL -> CR
                    if (b == IAC) { _ts = TS.Iac; break; }
                    buf[w++] = b;
                    if (b == 13) _ts = TS.Cr;
                    break;
                case TS.Iac:
                    if (b == IAC) { buf[w++] = IAC; _ts = TS.Data; }
                    else if (b is WILL or WONT or DO or DONT) { _verb = b; _ts = TS.Opt; }
                    else if (b == SB) { _sb.Clear(); _ts = TS.Sb; }
                    else _ts = TS.Data;
                    break;
                case TS.Opt:
                    HandleOption(_verb, b);
                    _ts = TS.Data;
                    break;
                case TS.Sb:
                    if (b == IAC) _ts = TS.SbIac;
                    else if (_sb.Count < 1024) _sb.Add(b);
                    break;
                case TS.SbIac:
                    if (b == SE) { HandleSubnegotiation(); _ts = TS.Data; }
                    else if (b == IAC) { _sb.Add(IAC); _ts = TS.Sb; }
                    else _ts = TS.Data;
                    break;
            }
        }
        return w;
    }

    void HandleOption(byte verb, byte opt)
    {
        switch (verb)
        {
            case DO:
                if (opt is OptTType or OptNaws or OptSga or OptBinary)
                {
                    if (_local.Add(opt))
                    {
                        SendRaw([IAC, WILL, opt]);
                        if (opt == OptNaws) SendNaws();
                    }
                }
                else SendRaw([IAC, WONT, opt]);
                break;
            case DONT:
                if (_local.Remove(opt)) SendRaw([IAC, WONT, opt]);
                break;
            case WILL:
                if (opt is OptEcho or OptSga or OptBinary)
                {
                    if (_remote.Add(opt)) SendRaw([IAC, DO, opt]);
                }
                else SendRaw([IAC, DONT, opt]);
                break;
            case WONT:
                if (_remote.Remove(opt)) SendRaw([IAC, DONT, opt]);
                break;
        }
    }

    void HandleSubnegotiation()
    {
        if (_sb.Count >= 2 && _sb[0] == OptTType && _sb[1] == 1)
        {
            var msg = new List<byte> { IAC, SB, OptTType, 0 };
            msg.AddRange(Encoding.ASCII.GetBytes(termType.ToUpperInvariant()));
            msg.AddRange([IAC, SE]);
            SendRaw(msg.ToArray());
        }
    }

    void SendNaws()
    {
        var msg = new List<byte> { IAC, SB, OptNaws };
        foreach (int v in new[] { _cols >> 8, _cols & 255, _rows >> 8, _rows & 255 })
        {
            msg.Add((byte)v);
            if (v == IAC) msg.Add(IAC);
        }
        msg.AddRange([IAC, SE]);
        SendRaw(msg.ToArray());
    }

    void SendRaw(byte[] data)
    {
        var s = _stream;
        if (s == null) return;
        try
        {
            lock (_sendLock) s.Write(data, 0, data.Length);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public void Send(byte[] data)
    {
        if (!telnet)
        {
            SendRaw(data);
            return;
        }
        var outp = new List<byte>(data.Length + 8);
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            outp.Add(b);
            if (b == IAC) outp.Add(IAC);
            else if (b == 13 && (i + 1 >= data.Length || data[i + 1] != 10) && !_local.Contains(OptBinary)) outp.Add(0);
        }
        SendRaw(outp.ToArray());
    }

    public void Resize(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        if (telnet && _local.Contains(OptNaws)) SendNaws();
    }

    public void SendBreak()
    {
        if (telnet) SendRaw([IAC, BRK]);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _client?.Close(); } catch (SocketException) { }
    }
}
