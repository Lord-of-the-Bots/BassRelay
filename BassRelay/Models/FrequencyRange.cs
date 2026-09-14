using System;

namespace BassRelay.Models;

public static class FrequencyRange
{
    public const double Minimum = 0;
    public const double Maximum = 200;

    public static bool IsValidBand(double low, double high) =>
        double.IsFinite(low) && double.IsFinite(high) &&
        low >= Minimum && high <= Maximum && low < high;
}
