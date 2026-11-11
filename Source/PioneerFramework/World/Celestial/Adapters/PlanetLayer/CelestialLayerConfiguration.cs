using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace PioneerFramework.Celestial
{
    /// <summary>
    /// Decodes a surface header's frozen generation configuration once per header object.
    /// Every consumer (world-gen step, native map generation, native layer manager) reads this
    /// instead of re-decoding the same blob, so the binary layout stays single-sourced.
    /// </summary>
    internal static class CelestialLayerConfiguration
    {
        private static readonly ConditionalWeakTable<CelestialSurfaceHeader, CelestialResult<IReadOnlyList<CelestialCompSaveRecord>>> cache = new();

        internal static CelestialResult<IReadOnlyList<CelestialCompSaveRecord>> For(CelestialSurfaceHeader header)
            => cache.GetValue(header, static value => Decode(value));

        private static CelestialResult<IReadOnlyList<CelestialCompSaveRecord>> Decode(CelestialSurfaceHeader header)
        {
            try { return CelestialGenerationConfiguration.Decode(header.GenerationConfiguration); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException)
            { return CelestialResult.Reject<IReadOnlyList<CelestialCompSaveRecord>>(new CelestialError("InvalidGenerationConfiguration", Detail: exception.Message)); }
        }
    }
}
