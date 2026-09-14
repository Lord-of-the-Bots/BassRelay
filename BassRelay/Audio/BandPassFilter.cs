using System;
using BassRelay.Models;

namespace BassRelay.Audio;

/// <summary>Fourth-order Butterworth high-pass and low-pass: 24 dB/octave at each edge.</summary>
public sealed class BandPassFilter
{
    private readonly Biquad[] _sections;

    public BandPassFilter(int sampleRate, double lowCutHz, double highCutHz)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (!FrequencyRange.IsValidBand(lowCutHz, highCutHz) || highCutHz >= sampleRate / 2d)
            throw new ArgumentOutOfRangeException(nameof(lowCutHz), "Требуется 0 ≤ нижняя частота < верхняя частота ≤ 200 Гц и ниже половины частоты дискретизации.");

        // The two Q values form a fourth-order Butterworth response (not two Q=.707 filters).
        // A zero lower cutoff explicitly disables the high-pass stage.
        _sections = lowCutHz == 0 ? [
            new(sampleRate, highCutHz, 0.541196100146197, false),
            new(sampleRate, highCutHz, 1.306562964876377, false)
        ] : [
            new(sampleRate, lowCutHz, 0.541196100146197, true),
            new(sampleRate, lowCutHz, 1.306562964876377, true),
            new(sampleRate, highCutHz, 0.541196100146197, false),
            new(sampleRate, highCutHz, 1.306562964876377, false)
        ];
    }

    public float Process(float sample)
    {
        double value = float.IsFinite(sample) ? sample : 0;
        foreach (Biquad section in _sections) value = section.Process(value);
        if (!double.IsFinite(value) || Math.Abs(value) > float.MaxValue)
        {
            Reset();
            return 0;
        }
        return (float)value;
    }

    public void Reset()
    {
        foreach (Biquad section in _sections) section.Reset();
    }

    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;

        public Biquad(int sampleRate, double frequency, double q, bool highPass)
        {
            double omega = 2 * Math.PI * frequency / sampleRate;
            double cosine = Math.Cos(omega);
            double alpha = Math.Sin(omega) / (2 * q);
            double a0 = 1 + alpha;
            _b0 = (highPass ? 1 + cosine : 1 - cosine) / (2 * a0);
            _b1 = (highPass ? -(1 + cosine) : 1 - cosine) / a0;
            _b2 = _b0;
            _a1 = -2 * cosine / a0;
            _a2 = (1 - alpha) / a0;
        }

        public double Process(double value)
        {
            double result = _b0 * value + _z1;
            _z1 = _b1 * value - _a1 * result + _z2;
            _z2 = _b2 * value - _a2 * result;
            // Prevent denormals after a long silent tail.
            if (Math.Abs(_z1) < 1e-30) _z1 = 0;
            if (Math.Abs(_z2) < 1e-30) _z2 = 0;
            return result;
        }

        public void Reset() => _z1 = _z2 = 0;
    }
}
