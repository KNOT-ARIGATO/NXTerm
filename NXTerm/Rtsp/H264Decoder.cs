using System.Runtime.InteropServices;

namespace NXTerm.Rtsp;

/// <summary>
/// Decodes H.264 with the decoder that ships with Windows (Media Foundation "Microsoft H264 Video Decoder MFT")
/// and converts the pictures to 32-bit BGRA. No third-party codec is needed.
/// The COM interfaces are called through their vtables, so the slot numbers below follow mfobjects.h / mftransform.h.
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    const int MfVersion = 0x00020070;
    const int NeedMoreInput = unchecked((int)0xC00D6D72), StreamChange = unchecked((int)0xC00D6D61), NotAccepting = unchecked((int)0xC00D36B5);
    const uint BeginStreaming = 0x10000000, StartOfStream = 0x10000003;
    const uint ProvidesSamples = 0x100, CanProvideSamples = 0x200;

    static readonly Guid ClsidH264Decoder = new("62CE7E72-4C71-4d20-B15D-452831A87D9D");
    static readonly Guid IidTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    static readonly Guid FormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    static readonly Guid FormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    static readonly Guid KeyMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid KeySubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid KeyFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid KeyDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid KeyDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    static readonly Guid KeyLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] static extern int MFShutdown();
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(uint size, out IntPtr buffer);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(in Guid clsid, IntPtr outer, int context, in Guid iid, out IntPtr obj);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, int model);

    [StructLayout(LayoutKind.Sequential)]
    struct OutputDataBuffer
    {
        public uint StreamId;
        public IntPtr Sample;
        public uint Status;
        public IntPtr Events;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct OutputStreamInfo
    {
        public uint Flags, Size, Alignment;
    }

    IntPtr _mft;
    bool _started, _outputReady, _mftAllocates;
    uint _outputSize;
    int _width, _height, _stride, _cropX, _cropY;

    public int Width => _width;
    public int Height => _height;

    public H264Decoder()
    {
        CoInitializeEx(IntPtr.Zero, 0);  // multithreaded; harmless when the thread is already initialized
        const string noMediaFoundation = "This Windows installation has no Media Foundation (install the Media Feature Pack).";
        try
        {
            if (MFStartup(MfVersion, 0) < 0) throw new RtspException(noMediaFoundation, fatal: true);
        }
        catch (DllNotFoundException) { throw new RtspException(noMediaFoundation, fatal: true); }
        _started = true;
        int hr = CoCreateInstance(ClsidH264Decoder, IntPtr.Zero, 1, IidTransform, out _mft);
        if (hr != 0)
        {
            Dispose();
            throw new RtspException("Windows has no H.264 decoder installed (Media Feature Pack is missing).", fatal: true);
        }

        // Ask for pictures as soon as they are decoded instead of after the usual reorder delay.
        if (GetPointer(_mft, 8, out IntPtr attributes) == 0)  // IMFTransform::GetAttributes
        {
            SetUInt32(attributes, KeyLowLatency, 1);
            Marshal.Release(attributes);
        }

        Check(MFCreateMediaType(out IntPtr input), "MFCreateMediaType");
        try
        {
            SetGuid(input, KeyMajorType, MediaTypeVideo);
            SetGuid(input, KeySubtype, FormatH264);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Slot(_mft, 15))(_mft, 0, input, 0), "SetInputType");
        }
        finally { Marshal.Release(input); }

        SelectOutputType();
        Message(BeginStreaming);
        Message(StartOfStream);
    }

    // ------------------------------------------------------------------ vtable helpers

    static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    /// <summary>Calls a method of the form HRESULT M(T** result).</summary>
    static int GetPointer(IntPtr obj, int slot, out IntPtr result)
    {
        IntPtr r;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(obj, slot))(obj, &r);
        result = hr == 0 ? r : IntPtr.Zero;
        return hr;
    }

    static void SetGuid(IntPtr attributes, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value), "SetGUID");

    static void SetUInt32(IntPtr attributes, Guid key, uint value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(attributes, 21))(attributes, &key, value);

    static bool GetGuid(IntPtr attributes, Guid key, out Guid value)
    {
        Guid v;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(attributes, 10))(attributes, &key, &v);
        value = v;
        return hr == 0;
    }

    static bool GetUInt32(IntPtr attributes, Guid key, out uint value)
    {
        uint v;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(attributes, 7))(attributes, &key, &v);
        value = v;
        return hr == 0;
    }

    static bool GetUInt64(IntPtr attributes, Guid key, out ulong value)
    {
        ulong v;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Slot(attributes, 8))(attributes, &key, &v);
        value = v;
        return hr == 0;
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new RtspException($"Video decoder error in {what} (0x{hr:X8}).");
    }

    void Message(uint message) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, int>)Slot(_mft, 23))(_mft, message, 0);

    // ------------------------------------------------------------------ output format

    /// <summary>Picks NV12 as the output format and reads the picture size the decoder reports for it.</summary>
    bool SelectOutputType()
    {
        _outputReady = false;
        for (uint i = 0; i < 32; i++)
        {
            IntPtr type;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)Slot(_mft, 14))(_mft, 0, i, &type) != 0) break;
            try
            {
                if (!GetGuid(type, KeySubtype, out var subtype) || subtype != FormatNv12) continue;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Slot(_mft, 16))(_mft, 0, type, 0) != 0) continue;

                if (GetUInt64(type, KeyFrameSize, out ulong size))
                {
                    _width = (int)(size >> 32);
                    _height = (int)(size & 0xFFFFFFFF);
                }
                _stride = GetUInt32(type, KeyDefaultStride, out uint stride) && (int)stride > 0 ? (int)stride : _width;
                _cropX = _cropY = 0;

                // The visible area can be smaller than the coded picture (1080 lines inside 1088).
                byte* area = stackalloc byte[16];
                uint got;
                var key = KeyDisplayAperture;
                if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, byte*, uint, uint*, int>)Slot(type, 15))(type, &key, area, 16, &got) == 0 && got == 16)
                {
                    int w = *(int*)(area + 8), h = *(int*)(area + 12);
                    if (w > 0 && h > 0 && w <= _width && h <= _height)
                    {
                        _cropX = *(short*)(area + 2);
                        _cropY = *(short*)(area + 6);
                        _width = w;
                        _height = h;
                    }
                }

                OutputStreamInfo info;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, OutputStreamInfo*, int>)Slot(_mft, 7))(_mft, 0, &info), "GetOutputStreamInfo");
                _mftAllocates = (info.Flags & (ProvidesSamples | CanProvideSamples)) != 0;
                _outputSize = info.Size;
                _outputReady = true;
                return true;
            }
            finally { Marshal.Release(type); }
        }
        return false;
    }

    // ------------------------------------------------------------------ decoding

    /// <summary>
    /// Feeds one picture (Annex B) to the decoder. For every decoded picture <paramref name="rent"/> is asked for a
    /// buffer of width*height*4 bytes, which is filled with BGRA pixels and handed to <paramref name="ready"/>.
    /// </summary>
    /// <param name="time">Presentation time in 100 ns units.</param>
    public void Decode(byte[] data, long time, Func<int, int, byte[]> rent, Action<byte[], int, int> ready)
    {
        Check(MFCreateSample(out IntPtr sample), "MFCreateSample");
        try
        {
            Check(MFCreateMemoryBuffer((uint)data.Length, out IntPtr buffer), "MFCreateMemoryBuffer");
            try
            {
                byte* p;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &p, null, null), "Lock");
                Marshal.Copy(data, 0, (IntPtr)p, data.Length);
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer);
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(buffer, 6))(buffer, (uint)data.Length);
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(sample, 42))(sample, buffer), "AddBuffer");
            }
            finally { Marshal.Release(buffer); }
            ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(sample, 36))(sample, time);

            var processInput = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Slot(_mft, 24);
            int hr = processInput(_mft, 0, sample, 0);
            if (hr == NotAccepting)
            {
                Drain(rent, ready);
                hr = processInput(_mft, 0, sample, 0);
            }
            Check(hr, "ProcessInput");
        }
        finally { Marshal.Release(sample); }
        Drain(rent, ready);
    }

    void Drain(Func<int, int, byte[]> rent, Action<byte[], int, int> ready)
    {
        for (int guard = 0; guard < 64; guard++)
        {
            if (!_outputReady && !SelectOutputType()) return;

            var output = new OutputDataBuffer();
            if (!_mftAllocates)
            {
                Check(MFCreateSample(out output.Sample), "MFCreateSample");
                Check(MFCreateMemoryBuffer(_outputSize, out IntPtr buffer), "MFCreateMemoryBuffer");
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(output.Sample, 42))(output.Sample, buffer);
                Marshal.Release(buffer);
            }
            uint status;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, OutputDataBuffer*, uint*, int>)Slot(_mft, 25))(_mft, 0, 1, &output, &status);
            if (output.Events != IntPtr.Zero) Marshal.Release(output.Events);
            try
            {
                if (hr == NeedMoreInput) return;
                if (hr == StreamChange)
                {
                    // The decoder now knows the real picture size: choose the output format again and retry.
                    if (!SelectOutputType()) throw new RtspException("The video decoder offers no usable output format.", fatal: true);
                    continue;
                }
                Check(hr, "ProcessOutput");
                if (output.Sample != IntPtr.Zero) Deliver(output.Sample, rent, ready);
            }
            finally
            {
                if (output.Sample != IntPtr.Zero) Marshal.Release(output.Sample);
            }
        }
    }

    void Deliver(IntPtr sample, Func<int, int, byte[]> rent, Action<byte[], int, int> ready)
    {
        IntPtr buffer;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &buffer), "ConvertToContiguousBuffer");
        try
        {
            byte* p;
            uint length;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &p, null, &length), "Lock");
            try
            {
                if (_width <= 0 || _height <= 0 || _stride <= 0) return;
                // NV12 is a full-size Y plane followed by a half-height interleaved UV plane, both using the same stride.
                int codedHeight = (int)(length * 2 / (3 * (uint)_stride));
                if (codedHeight < _cropY + _height || _stride < _cropX + _width) return;
                var bgra = rent(_width, _height);
                fixed (byte* dst = bgra)
                    Nv12ToBgra(p, _stride, codedHeight, _cropX, _cropY, _width, _height, dst);
                ready(bgra, _width, _height);
            }
            finally { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer); }
        }
        finally { Marshal.Release(buffer); }
    }

    /// <summary>Converts NV12 (video range) to BGRA. HD pictures use BT.709 coefficients, smaller ones BT.601.</summary>
    public static void Nv12ToBgra(byte* src, int stride, int codedHeight, int cropX, int cropY, int width, int height, byte* dst)
    {
        bool hd = height >= 720;
        int crv = hd ? 459 : 409, cgu = hd ? 55 : 100, cgv = hd ? 136 : 208, cbu = hd ? 541 : 516;
        byte* uvPlane = src + (long)stride * codedHeight;
        for (int y = 0; y < height; y++)
        {
            byte* yRow = src + (long)(y + cropY) * stride + cropX;
            byte* uvRow = uvPlane + (long)((y + cropY) / 2) * stride + (cropX & ~1);
            byte* o = dst + (long)y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int c = 298 * (yRow[x] - 16) + 128;
                int uv = x & ~1;
                int d = uvRow[uv] - 128, e = uvRow[uv + 1] - 128;
                int r = (c + crv * e) >> 8, g = (c - cgu * d - cgv * e) >> 8, b = (c + cbu * d) >> 8;
                o[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                o[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                o[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                o[3] = 255;
                o += 4;
            }
        }
    }

    public void Dispose()
    {
        if (_mft != IntPtr.Zero)
        {
            Marshal.Release(_mft);
            _mft = IntPtr.Zero;
        }
        if (_started)
        {
            MFShutdown();
            _started = false;
        }
    }
}
