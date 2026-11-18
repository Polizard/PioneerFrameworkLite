using System;
using static System.Math;

namespace PioneerFramework
{
    /// <summary>
    /// MechJebLib 数学工具集。移植自 MechJebLib.Utils.Statics。
    /// </summary>
    public static class MJStatics
    {
        public const double TAU = 2.0 * PI;

        public const double EPS = 2.2204460492503131e-16;

        public static double Clamp(double x, double min, double max) => x < min ? min : x > max ? max : x;
        public static int Clamp(int x, int min, int max) => x < min ? min : x > max ? max : x;

        public static double Deg2Rad(double deg) => deg * PI / 180.0;
        public static double Rad2Deg(double rad) => rad * 180.0 / PI;

        public static double SafeAcos(double x) => !IsFinite(x) ? double.NaN : Acos(Clamp(x, -1.0, 1.0));
        public static double SafeAsin(double x) => !IsFinite(x) ? double.NaN : Asin(Clamp(x, -1.0, 1.0));

        public static double Powi(double x, int n)
        {
            if (n < 0) { x = 1.0 / x; n = -n; }
            if (n == 0) return 1.0;
            double y = 1.0;
            while (n > 1)
            {
                if (n % 2 == 0) { x *= x; n /= 2; }
                else { y *= x; x *= x; n = (n - 1) / 2; }
            }
            return x * y;
        }

        public static double Clamp2Pi(double x)
        {
            x %= TAU;
            x = x < 0 ? x + TAU : x;
            return x >= TAU ? 0 : x;
        }

        public static double ClampPi(double x)
        {
            x = Clamp2Pi(x);
            return x > PI ? x - TAU : x;
        }

        public static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        public static double Lerp(double a, double b, double t) => a + (b - a) * Clamp01(t);
        public static double Clamp01(double x) => Clamp(x, 0.0, 1.0);

        public static double Atanh(double x)
        {
            if (Abs(x) > 1.0) throw new ArgumentException($"Atanh argument out of range: {x}");
            return 0.5 * Log((1.0 + x) / (1.0 - x));
        }

        public static double Acosh(double x)
        {
            if (x < 1.0) throw new ArgumentException($"Acosh argument out of range: {x}");
            return Log(x + Sqrt(x * x - 1.0));
        }

        public static double Asinh(double x) => Log(x + Sqrt(x * x + 1.0));
    }
}
