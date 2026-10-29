using System;
using System.Collections.Generic;

namespace PioneerFramework.Celestial
{
    public sealed record CelestialEpochUsage(long Epoch, int Pending, int Terminal);
    public sealed class CelestialEpochState
    {
        public long NextEpoch { get; }
        public IReadOnlyList<long> OpenEpochs { get; }
        public CelestialEpochState(long nextEpoch, IEnumerable<long> openEpochs)
        { NextEpoch = nextEpoch; OpenEpochs = Array.AsReadOnly(System.Linq.Enumerable.ToArray(openEpochs)); }
    }
}
