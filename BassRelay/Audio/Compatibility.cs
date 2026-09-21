#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    // Records compile to ordinary types; only this compile-time marker is absent in net48.
    internal static class IsExternalInit { }
}
#endif

namespace BassRelay.Audio
{
    internal static class Numeric
    {
        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public static double Clamp(double value, double minimum, double maximum) =>
            value < minimum ? minimum : value > maximum ? maximum : value;
        public static int Clamp(int value, int minimum, int maximum) =>
            value < minimum ? minimum : value > maximum ? maximum : value;
    }
}
