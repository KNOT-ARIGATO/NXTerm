using System.Buffers.Binary;

namespace NXTerm.Rtsp;

/// <summary>
/// Rebuilds H.264 pictures (Annex B byte stream) from RTP packets as described in RFC 6184:
/// single NAL units, STAP-A aggregation packets and FU-A fragments.
/// </summary>
public sealed class H264Depacketizer
{
    static readonly byte[] StartCode = [0, 0, 0, 1];

    readonly MemoryStream _picture = new();
    uint _timestamp;
    int _nalStart;
    bool _open, _fragment, _hasIdr, _hasSps, _hasSlice, _gotKeyFrame;
    byte[]? _sps, _pps;

    /// <summary>Raised for each complete picture: Annex B data, RTP timestamp (90 kHz), is key frame.</summary>
    public event Action<byte[], uint, bool>? FrameReady;

    /// <summary>Parameter sets announced in the SDP (sprop-parameter-sets), used when the stream does not repeat them.</summary>
    public void SetParameterSets(byte[]? sps, byte[]? pps)
    {
        _sps = sps;
        _pps = pps;
    }

    public void OnRtp(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12 || packet[0] >> 6 != 2) return;
        int csrcCount = packet[0] & 0x0F;
        bool extension = (packet[0] & 0x10) != 0, padding = (packet[0] & 0x20) != 0;
        bool marker = (packet[1] & 0x80) != 0;
        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);

        int offset = 12 + csrcCount * 4;
        if (extension)
        {
            if (packet.Length < offset + 4) return;
            offset += 4 + BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]) * 4;
        }
        int end = packet.Length;
        if (padding && end > offset) end -= packet[end - 1];
        if (end <= offset) return;
        var payload = packet[offset..end];

        if (_open && timestamp != _timestamp) Flush();  // the previous picture had no marker bit
        _timestamp = timestamp;
        _open = true;

        int type = payload[0] & 0x1F;
        if (type is >= 1 and <= 23) AddNal(payload);
        else if (type == 24)  // STAP-A: several small NAL units in one packet
        {
            int p = 1;
            while (p + 2 <= payload.Length)
            {
                int size = (payload[p] << 8) | payload[p + 1];
                p += 2;
                if (size == 0 || p + size > payload.Length) break;
                AddNal(payload.Slice(p, size));
                p += size;
            }
        }
        else if (type == 28 && payload.Length >= 2)  // FU-A: one NAL unit split over several packets
        {
            byte fu = payload[1];
            if ((fu & 0x80) != 0)
            {
                byte header = (byte)((payload[0] & 0xE0) | (fu & 0x1F));
                _picture.Write(StartCode);
                _nalStart = (int)_picture.Length;
                _picture.WriteByte(header);
                Note(header & 0x1F);
                _fragment = true;
            }
            if (_fragment)
            {
                _picture.Write(payload[2..]);
                if ((fu & 0x40) != 0) NoteEmbedded(_nalStart);
            }
            if ((fu & 0x40) != 0) _fragment = false;
        }
        if (marker) Flush();
    }

    void AddNal(ReadOnlySpan<byte> nal)
    {
        int type = nal[0] & 0x1F;
        if (type == 7) _sps = nal.ToArray();
        else if (type == 8) _pps = nal.ToArray();
        _picture.Write(StartCode);
        int start = (int)_picture.Length;
        _picture.Write(nal);
        Note(type);
        NoteEmbedded(start);
        _fragment = false;
    }

    /// <summary>
    /// Some cameras (Realtek Ameba boards, for one) send a whole picture as if it were one NAL unit: SEI or SPS first,
    /// then the other units each behind their own start code. The data is already in the Annex B form the decoder
    /// reads, so it is kept as it is; only the units inside are noted, so the picture is not mistaken for one without
    /// a slice. A real NAL unit never contains 00 00 01 (emulation prevention), so this finds nothing in normal streams.
    /// </summary>
    void NoteEmbedded(int nalStart)
    {
        var buf = _picture.GetBuffer();
        int len = (int)_picture.Length;
        for (int i = nalStart + 1; i + 3 < len; i++)
        {
            if (buf[i] != 0 || buf[i + 1] != 0 || buf[i + 2] != 1) continue;
            int start = i + 3, type = buf[start] & 0x1F;
            Note(type);
            if (type is 7 or 8)
            {
                int end = start + 1;
                while (end + 2 < len && !(buf[end] == 0 && buf[end + 1] == 0 && buf[end + 2] == 1)) end++;
                if (end + 2 >= len) end = len;
                while (end > start + 1 && buf[end - 1] == 0) end--;  // the zero before a 4-byte start code
                var unit = buf.AsSpan(start, end - start).ToArray();
                if (type == 7) _sps = unit;
                else _pps = unit;
            }
            i = start;
        }
    }

    void Note(int nalType)
    {
        if (nalType == 5) _hasIdr = _hasSlice = true;
        else if (nalType is >= 1 and <= 4) _hasSlice = true;
        else if (nalType == 7) _hasSps = true;
    }

    void Flush()
    {
        // Decoding can only start at a key frame, so everything before the first one is dropped.
        if (_hasSlice && (_gotKeyFrame || _hasIdr))
        {
            _gotKeyFrame = true;
            byte[] frame;
            if (_hasIdr && !_hasSps && _sps != null && _pps != null)
            {
                frame = new byte[8 + _sps.Length + _pps.Length + _picture.Length];
                int p = 0;
                foreach (var part in new[] { StartCode, _sps, StartCode, _pps })
                {
                    part.CopyTo(frame, p);
                    p += part.Length;
                }
                _picture.GetBuffer().AsSpan(0, (int)_picture.Length).CopyTo(frame.AsSpan(p));
            }
            else frame = _picture.ToArray();
            FrameReady?.Invoke(frame, _timestamp, _hasIdr);
        }
        // Parameter sets that arrive in their own packets stay buffered for the picture that follows.
        if (_hasSlice || _picture.Length > 65536)
        {
            _picture.SetLength(0);
            _hasIdr = _hasSps = false;
        }
        _hasSlice = false;
        _open = false;
        _fragment = false;
    }
}
