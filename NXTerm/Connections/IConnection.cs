namespace NXTerm.Connections;

public interface IConnection : IDisposable
{
    bool IsConnected { get; }

    /// <summary>Raised on a background thread. The buffer is reused, so handlers must copy it.</summary>
    event Action<byte[], int>? DataReceived;

    /// <summary>Raised on a background thread when the remote side closes or the link fails.</summary>
    event Action<string?>? Disconnected;

    Task ConnectAsync(int cols, int rows, CancellationToken ct = default);

    /// <summary>Thread-safe.</summary>
    void Send(byte[] data);

    void Resize(int cols, int rows);

    void SendBreak();
}
