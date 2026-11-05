using System;
using System.IO;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialSurfaceCreationScope : IDisposable
    {
        [ThreadStatic] private static CelestialSurfaceCreationScope? current;
        private readonly CelestialSurfaceCreationScope? previous;
        private bool disposed;
        private readonly WorldGrid grid;
        private readonly Func<int, CelestialSurfaceHeader>? bind;
        public static CelestialSurfaceHeader? CurrentHeader => current?.Header;
        private CelestialSurfaceHeader Header { get; set; }
        public CelestialSurfaceCreationScope(CelestialSurfaceHeader header) : this(header, Find.WorldGrid) { }
        internal CelestialSurfaceCreationScope(CelestialSurfaceHeader header, WorldGrid grid, Func<int, CelestialSurfaceHeader>? bind = null)
        { Header = header; this.grid = grid ?? throw new ArgumentNullException(nameof(grid)); this.bind = bind; previous = current; current = this; }
        internal static void ValidateGrid(WorldGrid grid)
        { if (current != null && !ReferenceEquals(current.grid, grid)) throw new InvalidOperationException("SurfaceConstructionWorldMismatch"); }
        internal static CelestialSurfaceHeader Bind(int layerId)
        {
            if (current == null) throw new InvalidOperationException("Missing celestial surface creation header.");
            current.Header = current.bind?.Invoke(layerId) ?? current.Header.WithLayerId(layerId);
            return current.Header;
        }
        public void Dispose()
        {
            if (disposed) return;
            if (current != this) throw new InvalidOperationException("Unbalanced celestial surface creation scope.");
            current = previous;
            disposed = true;
        }
    }
    public class CelestialSurfaceLayer : PF_CelestialSurfaceLayer, IExposable
    {
        [ThreadStatic] private static CelestialSurfaceHeader? initializing;
        private CelestialSurfaceHeader header = null!;
        public CelestialSurfaceHeader Header => header;
        public static CelestialSurfaceHeader? InitializingHeader => initializing ?? CelestialSurfaceCreationScope.CurrentHeader;
        public CelestialSurfaceLayer() { }
        public CelestialSurfaceLayer(int layerId, PlanetLayerDef def, float radius, Vector3 origin, Vector3 viewCenter, float viewAngle,
            int subdivisions, float extraCameraAltitude, float backgroundWorldCameraOffset, float backgroundWorldCameraParallaxDistance)
            : base(layerId, def, CelestialWorldBootstrap.RadiusFor(def, radius), origin, viewCenter, viewAngle,
                CelestialWorldBootstrap.SubdivisionsFor(def, subdivisions), extraCameraAltitude, backgroundWorldCameraOffset, backgroundWorldCameraParallaxDistance)
            => header = CelestialWorldBootstrap.HeaderFor(def, layerId);
        void IExposable.ExposeData()
        {
            Scribe_Deep.Look(ref header, "celestialHeader");
            if (header == null) throw new InvalidDataException("Unsupported celestial surface save: missing header.");
            var previous = initializing;
            initializing = header;
            try { base.ExposeData(); }
            finally { initializing = previous; }
            if (Scribe.mode == LoadSaveMode.LoadingVars && (header.LayerId != LayerID || header.NativeRadius != Radius))
                throw new InvalidDataException("Celestial surface header/native identity mismatch.");
        }
    }
}
