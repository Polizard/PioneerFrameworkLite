using System;

namespace PioneerFramework.Celestial
{
    public interface ICelestialParameterDistribution<out T>
    {
        T Sample(ulong sampleWord);
    }

    public sealed class DoubleParameterSpec : ICelestialParameterDistribution<double>
    {
        public double Minimum { get; }
        public double Maximum { get; }
        public bool IsFixed { get; }

        private DoubleParameterSpec(double minimum, double maximum, bool isFixed)
        {
            if (!double.IsFinite(minimum)) throw new ArgumentOutOfRangeException(nameof(minimum));
            if (!double.IsFinite(maximum)) throw new ArgumentOutOfRangeException(nameof(maximum));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(minimum, maximum);
            Minimum = minimum;
            Maximum = maximum;
            IsFixed = isFixed;
        }

        public static DoubleParameterSpec Fixed(double value) => new(value, value, true);
        public static DoubleParameterSpec Uniform(double minimum, double maximum) => new(minimum, maximum, false);
        public double Sample(ulong sampleWord) => CelestialParameterSampler.SampleDouble(this, sampleWord);
    }

    public sealed class IntParameterSpec : ICelestialParameterDistribution<int>
    {
        public int Minimum { get; }
        public int Maximum { get; }
        public bool IsFixed { get; }

        private IntParameterSpec(int minimum, int maximum, bool isFixed)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(minimum, maximum);
            Minimum = minimum;
            Maximum = maximum;
            IsFixed = isFixed;
        }

        public static IntParameterSpec Fixed(int value) => new(value, value, true);
        public static IntParameterSpec Uniform(int minimum, int maximum) => new(minimum, maximum, false);
        public int Sample(ulong sampleWord) => CelestialParameterSampler.SampleInt(this, sampleWord);
    }
}
