using System;
using NAudio.Wave;

namespace BassRelay.Audio;

/// <summary>Float sample adapter that retains an extensible WASAPI output descriptor.</summary>
public sealed class FloatWaveProvider : IWaveProvider
{
    private readonly ISampleProvider _source;
    private float[] _buffer = [];
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    public FloatWaveProvider(ISampleProvider source, WaveFormat outputFormat)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        WaveFormat = outputFormat ?? throw new ArgumentNullException(nameof(outputFormat));
        if (outputFormat.BitsPerSample != 32 || outputFormat.SampleRate != source.WaveFormat.SampleRate ||
            outputFormat.Channels != source.WaveFormat.Channels ||
            !(outputFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                outputFormat is WaveFormatExtensible extensible && extensible.SubFormat == FloatSubtype))
            throw new ArgumentException("The output must describe the source's 32-bit floating-point samples.", nameof(outputFormat));
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        int samples = count / sizeof(float);
        if (_buffer.Length < samples) _buffer = new float[samples];
        int read = _source.Read(_buffer, 0, samples);
        Buffer.BlockCopy(_buffer, 0, buffer, offset, read * sizeof(float));
        return read * sizeof(float);
    }
}
