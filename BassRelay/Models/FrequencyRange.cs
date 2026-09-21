using System;

namespace BassRelay.Models;

public static class FrequencyRange
{
    public const double Minimum = 0;
    public const double Maximum = 200;

    public static bool IsValidBand(double low, double high) =>
        !double.IsNaN(low) && !double.IsInfinity(low) &&
        !double.IsNaN(high) && !double.IsInfinity(high) &&
        low >= Minimum && high <= Maximum && low < high;
}
