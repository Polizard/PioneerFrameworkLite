using System;

namespace PioneerFramework.Celestial
{
    public static class CelestialParameterSampler
    {
        private const double ClosedUnitDenominator = 9007199254740991d;

        public static double SampleDouble(DoubleParameterSpec specification, ulong sampleWord)
        {
            ArgumentNullException.ThrowIfNull(specification);
            double min = specification.Minimum;
            double max = specification.Maximum;
            if (min == max) return min;
            double unit = (sampleWord >> 11) / ClosedUnitDenominator;
            if (unit == 0) return min;
            if (unit == 1) return max;
            return min < 0 && max > 0
                ? (1 - unit) * min + unit * max
                : min + (max - min) * unit;
        }

        public static int SampleInt(IntParameterSpec specification, ulong sampleWord)
        {
            ArgumentNullException.ThrowIfNull(specification);
            ulong span = (ulong)((long)specification.Maximum - specification.Minimum) + 1;
            if (span == 1) return specification.Minimum;
            ulong limit = ulong.MaxValue - ulong.MaxValue % span;
            ulong word = sampleWord;
            ulong counter = 0;
            while (word >= limit) word = CelestialSeed.NextWord(sampleWord, checked(++counter));
            return checked((int)(specification.Minimum + (long)(word % span)));
        }
    }
}
