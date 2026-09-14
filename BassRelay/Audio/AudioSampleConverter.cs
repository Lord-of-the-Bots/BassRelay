using System;
using System.Buffers.Binary;
using NAudio.Wave;

namespace BassRelay.Audio;

public static class AudioSampleConverter
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Average all channels, including LFE; discard an incomplete final frame.</summary>
    public static int ToMono(ReadOnlySpan<byte> data, WaveFormat format, Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(format);
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

        int frames = data.Length / format.BlockAlign;
        if (destination.Length < frames) throw new ArgumentException("Буфер назначения слишком мал.", nameof(destination));
        for (int frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                ReadOnlySpan<byte> sample = data.Slice(frame * format.BlockAlign + channel * bytesPerSample, bytesPerSample);
                double value;
                if (isFloat)
                    value = bytesPerSample == 4 ? BinaryPrimitives.ReadSingleLittleEndian(sample) : BinaryPrimitives.ReadDoubleLittleEndian(sample);
                else
                    value = bytesPerSample switch
                    {
                        1 => (sample[0] - 128) / 128d,
                        2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768d,
                        3 => ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608d,
                        _ => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648d
                    };
                sum += double.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
            }
            destination[frame] = (float)(sum / channels);
        }
        return frames;
    }
}
