using System;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace BassRelay.Audio;

public static class AudioSampleConverter
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Average all channels, including LFE; discard an incomplete final frame.</summary>
    public static int ToMono(byte[] data, int byteCount, WaveFormat format, float[] destination)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (destination is null) throw new ArgumentNullException(nameof(destination));
        if (byteCount < 0 || byteCount > data.Length) throw new ArgumentOutOfRangeException(nameof(byteCount));
        return ConvertToMono(data, byteCount, format, destination);
    }

    public static int ToMono(byte[] data, WaveFormat format, float[] destination) =>
        ToMono(data, data?.Length ?? 0, format, destination);

#if !NETFRAMEWORK
    // Keep span callers allocation-free on modern .NET; net48 has no System.Memory dependency.
    public static int ToMono(ReadOnlySpan<byte> data, WaveFormat format, Span<float> destination) =>
        ConvertToMono(data, data.Length, format, destination);

    private static int ConvertToMono(ReadOnlySpan<byte> data, int byteCount, WaveFormat format, Span<float> destination)
#else
    private static int ConvertToMono(byte[] data, int byteCount, WaveFormat format, float[] destination)
#endif
    {
        if (format is null) throw new ArgumentNullException(nameof(format));
        int channels = format.Channels;
        int bytesPerSample = format.BitsPerSample / 8;
        if (channels < 1 || bytesPerSample < 1 || format.BlockAlign < channels * bytesPerSample)
            throw new NotSupportedException("Некорректный формат системного звука.");
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm;
        if (format is WaveFormatExtensible extensible)
        {
            isFloat = extensible.SubFormat == FloatSubFormat;
            isPcm = extensible.SubFormat == PcmSubFormat;
        }
        if ((!isFloat && !isPcm) || (isFloat && bytesPerSample is not (4 or 8)) ||
            (isPcm && bytesPerSample is not (1 or 2 or 3 or 4)))
            throw new NotSupportedException("Формат системного звука не поддерживается.");

        int frames = byteCount / format.BlockAlign;
        if (destination.Length < frames) throw new ArgumentException("Буфер назначения слишком мал.", nameof(destination));
        for (int frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                int start = frame * format.BlockAlign + channel * bytesPerSample;
                double value;
                if (isFloat)
                {
                    // WASAPI mixes are little-endian. Explicit byte decoding also supports
                    // unaligned PCM and float samples without unsafe code or scratch arrays.
                    uint low = (uint)(data[start] | data[start + 1] << 8 | data[start + 2] << 16 | data[start + 3] << 24);
                    if (bytesPerSample == 4) value = new FloatBits { Bits = low }.Value;
                    else
                    {
                        uint high = (uint)(data[start + 4] | data[start + 5] << 8 | data[start + 6] << 16 | data[start + 7] << 24);
                        value = new DoubleBits { Bits = low | (ulong)high << 32 }.Value;
                    }
                }
                else
                    value = bytesPerSample switch
                    {
                        1 => (data[start] - 128) / 128d,
                        2 => (short)(data[start] | data[start + 1] << 8) / 32768d,
                        3 => ((data[start] | data[start + 1] << 8 | data[start + 2] << 16) << 8 >> 8) / 8388608d,
                        _ => (data[start] | data[start + 1] << 8 | data[start + 2] << 16 | data[start + 3] << 24) / 2147483648d
                    };
                sum += Numeric.IsFinite(value) ? Numeric.Clamp(value, -1, 1) : 0;
            }
            destination[frame] = (float)(sum / channels);
        }
        return frames;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct FloatBits
    {
        [FieldOffset(0)] public uint Bits;
        [FieldOffset(0)] public float Value;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct DoubleBits
    {
        [FieldOffset(0)] public ulong Bits;
        [FieldOffset(0)] public double Value;
    }
}
