using System;
using NAudio.Wave;

namespace BassRelay.Audio;

/// <summary>
/// A bounded mono FIFO shared by capture/render threads. Oldest samples are dropped on clock
/// drift or stalled hardware, so latency cannot grow without limit. The render clock always
/// receives full buffers, with zero input on starvation; the filter's tail decays naturally.
/// </summary>
public sealed class ShakerSampleProvider : ISampleProvider
{
    private readonly object _gate = new();
    private readonly float[] _ring;
    private BandPassFilter _filter;
    private int _head, _count, _channel;
    private float _currentFrame;
    private double _gain;
    private double _low, _high;
    private bool _muted;

    public ShakerSampleProvider(int sampleRate, int channels, double lowCutHz, double highCutHz,
        double gain = 1, int capacityMilliseconds = 160)
    {
        if (channels is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(channels));
        if (capacityMilliseconds is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(capacityMilliseconds));
        _filter = new BandPassFilter(sampleRate, lowCutHz, highCutHz);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _ring = new float[Math.Max(1, checked(sampleRate * capacityMilliseconds / 1000))];
        _low = lowCutHz;
        _high = highCutHz;
        _gain = Numeric.IsFinite(gain) ? Numeric.Clamp(gain, 0, 2) : 1;
    }

    public WaveFormat WaveFormat { get; }
    public int CapacityFrames => _ring.Length;
    public int BufferedFrames { get { lock (_gate) return _count; } }

    public void Configure(double lowCutHz, double highCutHz, double gain)
    {
        lock (_gate)
        {
            if (_low != lowCutHz || _high != highCutHz)
            {
                var replacement = new BandPassFilter(WaveFormat.SampleRate, lowCutHz, highCutHz);
                _filter = replacement;
                _low = lowCutHz;
                _high = highCutHz;
            }
            _gain = Numeric.IsFinite(gain) ? Numeric.Clamp(gain, 0, 2) : 1;
        }
    }

    public void Enqueue(float[] monoSamples) => Enqueue(monoSamples, 0, monoSamples?.Length ?? 0);

    public void Enqueue(float[] monoSamples, int offset, int count)
    {
        if (monoSamples is null) throw new ArgumentNullException(nameof(monoSamples));
        if (offset < 0 || count < 0 || offset > monoSamples.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        lock (_gate)
        {
            if (_muted) return;
            if (count >= _ring.Length)
            {
                offset += count - _ring.Length;
                count = _ring.Length;
            }
            int tail = PrepareEnqueue(count);
            int first = Math.Min(count, _ring.Length - tail);
            Array.Copy(monoSamples, offset, _ring, tail, first);
            Array.Copy(monoSamples, offset + first, _ring, 0, count - first);
            _count += count;
        }
    }

#if !NETFRAMEWORK
    public void Enqueue(ReadOnlySpan<float> monoSamples)
    {
        lock (_gate)
        {
            if (_muted) return;
            if (monoSamples.Length >= _ring.Length)
            {
                monoSamples = monoSamples[^_ring.Length..];
            }
            int tail = PrepareEnqueue(monoSamples.Length);
            int first = Math.Min(monoSamples.Length, _ring.Length - tail);
            monoSamples[..first].CopyTo(_ring.AsSpan(tail));
            monoSamples[first..].CopyTo(_ring);
            _count += monoSamples.Length;
        }
    }
#endif

    // Called while holding _gate; both supported runtimes use the same overflow policy.
    private int PrepareEnqueue(int count)
    {
        if (count >= _ring.Length) _head = _count = 0;
        int overflow = Math.Max(0, _count + count - _ring.Length);
        _head = (_head + overflow) % _ring.Length;
        _count -= overflow;
        return (_head + _count) % _ring.Length;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        lock (_gate)
        {
            if (_muted)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
            for (int i = 0; i < count; i++)
            {
                if (_channel == 0)
                {
                    float mono = 0;
                    if (_count > 0)
                    {
                        mono = _ring[_head];
                        _head = (_head + 1) % _ring.Length;
                        _count--;
                    }
                    _currentFrame = (float)Numeric.Clamp(_filter.Process(mono) * _gain, -1, 1);
                }
                buffer[offset + i] = _currentFrame;
                _channel = (_channel + 1) % WaveFormat.Channels;
            }
        }
        return count;
    }

    public void Clear()
    {
        lock (_gate) ClearCore();
    }

    internal void SetMuted(bool muted)
    {
        lock (_gate)
        {
            _muted = muted;
            if (muted) ClearCore();
        }
    }

    private void ClearCore()
    {
        _head = _count = _channel = 0;
        _currentFrame = 0;
        _filter.Reset();
    }
}
