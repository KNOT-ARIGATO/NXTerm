namespace NXTerm.Transfer;

/// <summary>XMODEM (checksum / CRC / 1K) sender and receiver. Received bytes are fed in via <see cref="OnData"/>.</summary>
public sealed class XModem(Action<byte[]> send)
{
    const byte SOH = 0x01, STX = 0x02, EOT = 0x04, ACK = 0x06, NAK = 0x15, CAN = 0x18, SUB = 0x1A, C = (byte)'C';
    const int MaxRetries = 10;

    readonly Queue<byte> _rx = new();

    public void OnData(byte[] buf, int n)
    {
        lock (_rx)
        {
            for (int i = 0; i < n; i++) _rx.Enqueue(buf[i]);
            Monitor.PulseAll(_rx);
        }
    }

    int Read(int timeoutMs, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        lock (_rx)
        {
            while (_rx.Count == 0)
            {
                ct.ThrowIfCancellationRequested();
                long rem = deadline - Environment.TickCount64;
                if (rem <= 0) return -1;
                Monitor.Wait(_rx, (int)Math.Min(rem, 100));
            }
            return _rx.Dequeue();
        }
    }

    void Purge()
    {
        Thread.Sleep(50);
        lock (_rx) _rx.Clear();
    }

    public void Cancel() => send([CAN, CAN, CAN, CAN, CAN]);

    static ushort Crc16(byte[] d, int off, int len)
    {
        int crc = 0;
        for (int i = off; i < off + len; i++)
        {
            crc ^= d[i] << 8;
            for (int b = 0; b < 8; b++) crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
        }
        return (ushort)crc;
    }

    static byte Checksum(byte[] d, int off, int len)
    {
        int s = 0;
        for (int i = off; i < off + len; i++) s += d[i];
        return (byte)s;
    }

    public void Send(Stream file, bool use1K, IProgress<long>? progress, CancellationToken ct)
    {
        bool crc;
        while (true)
        {
            int b = Read(60000, ct);
            if (b < 0) throw new TimeoutException("Receiver did not start (no 'C' or NAK within 60 s).");
            if (b == C) { crc = true; break; }
            if (b == NAK) { crc = false; break; }
            if (b == CAN) throw new IOException("Cancelled by receiver.");
        }

        int size = use1K && crc ? 1024 : 128;
        var data = new byte[size];
        byte blk = 1;
        long sent = 0;
        while (true)
        {
            int n = 0;
            while (n < size)
            {
                int r = file.Read(data, n, size - n);
                if (r == 0) break;
                n += r;
            }
            if (n == 0) break;
            int blockSize = size == 1024 && n <= 128 ? 128 : size;  // short tail goes in a 128-byte block
            for (int i = n; i < blockSize; i++) data[i] = SUB;

            var pkt = new byte[3 + blockSize + (crc ? 2 : 1)];
            pkt[0] = blockSize == 1024 ? STX : SOH;
            pkt[1] = blk;
            pkt[2] = (byte)~blk;
            Array.Copy(data, 0, pkt, 3, blockSize);
            if (crc)
            {
                ushort v = Crc16(pkt, 3, blockSize);
                pkt[^2] = (byte)(v >> 8);
                pkt[^1] = (byte)v;
            }
            else pkt[^1] = Checksum(pkt, 3, blockSize);

            for (int retry = 0; ; retry++)
            {
                if (retry >= MaxRetries) throw new IOException($"Block {blk}: too many retries.");
                send(pkt);
                int r = Read(10000, ct);
                if (r == ACK) break;
                if (r == CAN && Read(1000, ct) == CAN) throw new IOException("Cancelled by receiver.");
            }
            sent += n;
            progress?.Report(sent);
            blk++;
        }

        for (int retry = 0; ; retry++)
        {
            if (retry >= MaxRetries) throw new IOException("No ACK for EOT.");
            send([EOT]);
            if (Read(5000, ct) == ACK) break;
        }
    }

    public void Receive(Stream file, IProgress<long>? progress, CancellationToken ct)
    {
        bool crc = true;
        int hdr = -1;
        for (int tries = 0; hdr < 0; tries++)
        {
            if (tries >= 20) throw new TimeoutException("Sender did not start.");
            if (tries == 6) crc = false;  // fall back to checksum mode
            send([crc ? C : NAK]);
            hdr = Read(3000, ct);
            if (hdr is not (SOH or STX or EOT or CAN)) hdr = -1;
        }

        byte expected = 1;
        long received = 0;
        byte[]? pending = null;  // last block is written at EOT with trailing SUB padding removed
        int errors = 0;
        while (true)
        {
            if (hdr == EOT)
            {
                send([ACK]);
                if (pending != null)
                {
                    int len = pending.Length;
                    while (len > 0 && pending[len - 1] == SUB) len--;
                    file.Write(pending, 0, len);
                }
                return;
            }
            if (hdr == CAN) throw new IOException("Cancelled by sender.");

            bool ok = false;
            if (hdr is SOH or STX)
            {
                int size = hdr == SOH ? 128 : 1024;
                var pkt = new byte[2 + size + (crc ? 2 : 1)];
                int got = 0;
                while (got < pkt.Length)
                {
                    int b = Read(1000, ct);
                    if (b < 0) break;
                    pkt[got++] = (byte)b;
                }
                if (got == pkt.Length && pkt[0] == (byte)~pkt[1])
                {
                    bool sumOk = crc
                        ? Crc16(pkt, 2, size) == (ushort)((pkt[^2] << 8) | pkt[^1])
                        : Checksum(pkt, 2, size) == pkt[^1];
                    if (sumOk)
                    {
                        if (pkt[0] == expected)
                        {
                            if (pending != null) file.Write(pending);
                            pending = pkt.AsSpan(2, size).ToArray();
                            expected++;
                            received += size;
                            progress?.Report(received);
                            ok = true;
                        }
                        else if (pkt[0] == (byte)(expected - 1)) ok = true;  // duplicate: ACK again
                        else
                        {
                            Cancel();
                            throw new IOException("Block sequence error.");
                        }
                    }
                }
            }
            if (ok)
            {
                errors = 0;
                send([ACK]);
            }
            else
            {
                if (++errors > MaxRetries)
                {
                    Cancel();
                    throw new IOException("Too many errors.");
                }
                Purge();
                send([NAK]);
            }
            hdr = Read(10000, ct);
            if (hdr < 0)
            {
                if (++errors > MaxRetries) throw new TimeoutException("Sender stopped responding.");
                send([NAK]);
                hdr = Read(10000, ct);
                if (hdr < 0) throw new TimeoutException("Sender stopped responding.");
            }
        }
    }
}
