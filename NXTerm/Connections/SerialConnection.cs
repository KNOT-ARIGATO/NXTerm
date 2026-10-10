using System.Collections.Concurrent;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;

namespace NXTerm.Connections;

public sealed class SerialConnection(ConnectionParams p) : IConnection
{
    readonly CancellationTokenSource _cts = new();
    readonly BlockingCollection<byte[]> _sendQueue = new();
    SerialPort? _port;

    public bool IsConnected => _port?.IsOpen == true && !_cts.IsCancellationRequested;
    public event Action<byte[], int>? DataReceived;
    public event Action<string?>? Disconnected;

    public SerialPort? Port => _port;

    public Task ConnectAsync(int cols, int rows, CancellationToken ct = default) => Task.Run(() =>
    {
        var port = new SerialPort(p.PortName, p.Baud, p.Parity, p.DataBits, p.StopBits)
        {
            Handshake = p.Handshake,
            DtrEnable = true,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 10000,
        };
        if (p.Handshake is Handshake.None or Handshake.XOnXOff) port.RtsEnable = true;
        port.Open();
        _port = port;
        new Thread(ReadLoop) { IsBackground = true, Name = "serial-read" }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = "serial-write" }.Start();
    }, ct);

    void ReadLoop()
    {
        var buf = new byte[4096];
        string? error = null;
        try
        {
            var s = _port!.BaseStream;
            while (!_cts.IsCancellationRequested)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n > 0) DataReceived?.Invoke(buf, n);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        if (!_cts.IsCancellationRequested) Disconnected?.Invoke(error);
    }

    void WriteLoop()
    {
        try
        {
            foreach (var data in _sendQueue.GetConsumingEnumerable(_cts.Token))
            {
                var port = _port!;
                if (p.CharDelayMs <= 0 && p.LineDelayMs <= 0)
                {
                    port.Write(data, 0, data.Length);
                    continue;
                }
                for (int i = 0; i < data.Length; i++)
                {
                    port.Write(data, i, 1);
                    if (p.CharDelayMs > 0) Thread.Sleep(p.CharDelayMs);
                    if (p.LineDelayMs > 0 && data[i] == '\r') Thread.Sleep(p.LineDelayMs);
                    if (_cts.IsCancellationRequested) return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException) { }
    }

    public void Send(byte[] data)
    {
        if (!_cts.IsCancellationRequested) _sendQueue.Add(data);
    }

    public void Resize(int cols, int rows) { }

    public void SendBreak()
    {
        var port = _port;
        if (port == null) return;
        Task.Run(() =>
        {
            try
            {
                port.BreakState = true;
                Thread.Sleep(300);
                port.BreakState = false;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _port?.Close(); } catch (IOException) { }
    }

    /// <summary>COM ports with their device names, e.g. "COM3: USB-SERIAL CH340 (COM3)".</summary>
    public static List<string> ListPorts(bool friendlyNames)
    {
        var names = SerialPort.GetPortNames().Distinct().ToList();
        var friendly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (friendlyNames) try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var o in searcher.Get())
            {
                var name = o["Name"]?.ToString();
                if (name == null) continue;
                var m = Regex.Match(name, @"\((COM\d+)\)");
                if (m.Success) friendly[m.Groups[1].Value] = name;
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }

        return names
            .OrderBy(n => int.TryParse(n.AsSpan(3), out int k) ? k : int.MaxValue)
            .Select(n => friendly.TryGetValue(n, out var f) ? $"{n}: {f}" : n)
            .ToList();
    }
}
