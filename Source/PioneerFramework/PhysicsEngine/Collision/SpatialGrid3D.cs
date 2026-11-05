using System;
using System.Collections.Generic;
using Verse;

namespace PioneerFramework
{
    public class SpatialGrid3D<T> where T : ISpatialEntity
    {
        public class RoughCell
        {
            private readonly HashSet<T> entities = new();

            public double minX, maxX;
            public double minY, maxY;
            public double minZ, maxZ;
            public int emptyTick = -1;

            public void SetBounds(double minX, double maxX, double minY, double maxY, double minZ, double maxZ)
            {
                this.minX = minX; this.maxX = maxX;
                this.minY = minY; this.maxY = maxY;
                this.minZ = minZ; this.maxZ = maxZ;
            }

            public void Add(T entity) => entities.Add(entity);
            public void Remove(T entity) => entities.Remove(entity);
            public IReadOnlyCollection<T> GetAll() => entities;
            public int Count => entities.Count;
            public bool IsEmpty => entities.Count == 0;
            internal void ClearForCelestialStep() => entities.Clear();
        }

        private Dictionary<(int cx, int cy, int cz), RoughCell> cells = new();
        private Queue<(int cx, int cy, int cz)> emptyQueue = new();

        internal SpatialGrid3D<T> CopyForCelestialStep(Func<T, T> mapEntity, SpatialGrid3D<T>? recycled = null)
        {
            var copy = recycled != null && !ReferenceEquals(recycled, this) ? recycled : new SpatialGrid3D<T>();
            foreach (var key in copy.cells.Keys) if (!cells.ContainsKey(key)) copy.cells.Remove(key);
            foreach (var pair in cells)
            {
                var source = pair.Value;
                if (!copy.cells.TryGetValue(pair.Key, out var cell)) copy.cells.Add(pair.Key, cell = new RoughCell());
                cell.ClearForCelestialStep(); cell.emptyTick = source.emptyTick;
                cell.SetBounds(source.minX, source.maxX, source.minY, source.maxY, source.minZ, source.maxZ);
                foreach (var entity in source.GetAll()) cell.Add(mapEntity(entity));
            }
            copy.emptyQueue.Clear();
            foreach (var key in emptyQueue) copy.emptyQueue.Enqueue(key);
            return copy;
        }

        public const int ROUGH_EMPTY_TTL = 600;

        private (int cx, int cy, int cz) WorldToCell(DVector3 posMeters)
        {
            int cx = (int)Math.Floor(posMeters.x / SpaceConstants.CellSizeMeters);
            int cy = (int)Math.Floor(posMeters.y / SpaceConstants.CellSizeMeters);
            int cz = (int)Math.Floor(posMeters.z / SpaceConstants.CellSizeMeters);
            return (cx, cy, cz);
        }

        private RoughCell GetOrCreateCell((int cx, int cy, int cz) coord)
        {
            if (!cells.TryGetValue(coord, out var rCell))
            {
                rCell = new RoughCell();
                rCell.SetBounds(
                    coord.cx * SpaceConstants.CellSizeMeters, (coord.cx + 1) * SpaceConstants.CellSizeMeters,
                    coord.cy * SpaceConstants.CellSizeMeters, (coord.cy + 1) * SpaceConstants.CellSizeMeters,
                    coord.cz * SpaceConstants.CellSizeMeters, (coord.cz + 1) * SpaceConstants.CellSizeMeters
                );
                cells[coord] = rCell;
            }
            return rCell;
        }

        public (int cx, int cy, int cz) GetCell(DVector3 posMeters) => WorldToCell(posMeters);

        public void Add(T entity, DVector3 posMeters)
        {
            var coord = WorldToCell(posMeters);
            entity.LastCellCoord = coord;
            var rCell = GetOrCreateCell(coord);
            rCell.Add(entity);
            rCell.emptyTick = -1;
        }

        public void Remove(T entity)
        {
            if (cells.TryGetValue(entity.LastCellCoord, out var rCell))
            {
                rCell.Remove(entity);
                if (rCell.IsEmpty)
                {
                    rCell.emptyTick = Find.TickManager.TicksGame;
                    emptyQueue.Enqueue(entity.LastCellCoord);
                }
            }
        }

        public void Update(T entity, DVector3 oldPosMeters, DVector3 newPosMeters)
        {
            var oldCoord = WorldToCell(oldPosMeters);
            var newCoord = WorldToCell(newPosMeters);
            if (oldCoord.Equals(newCoord))
                return;

            if (cells.TryGetValue(oldCoord, out var oldRCell))
            {
                oldRCell.Remove(entity);
                if (oldRCell.IsEmpty)
                {
                    oldRCell.emptyTick = Find.TickManager.TicksGame;
                    emptyQueue.Enqueue(oldCoord);
                }
            }
            else
            {
                Log.Warning($"Update: entity {entity.EntityId} not found in old cell ({oldCoord.cx},{oldCoord.cy},{oldCoord.cz})");
            }

            var newRCell = GetOrCreateCell(newCoord);
            newRCell.Add(entity);
            entity.LastCellCoord = newCoord;
        }

        public IEnumerable<T> GetNearby(DVector3 centerMeters, double radiusMeters)
        {
            double radiusSq = radiusMeters * radiusMeters;
            int radCells = (int)Math.Ceiling(radiusMeters / SpaceConstants.CellSizeMeters);
            var centerCell = WorldToCell(centerMeters);
            for (int dx = -radCells; dx <= radCells; dx++)
                for (int dy = -radCells; dy <= radCells; dy++)
                    for (int dz = -radCells; dz <= radCells; dz++)
                    {
                        var coord = (centerCell.cx + dx, centerCell.cy + dy, centerCell.cz + dz);
                        if (cells.TryGetValue(coord, out var rCell))
                        {
                            foreach (var entity in rCell.GetAll())
                            {
                                if ((entity.Position - centerMeters).sqrMagnitude <= radiusSq)
                                    yield return entity;
                            }
                        }
                    }
        }

        public void DrainEmptyCells()
        {
            int drainCount = Math.Max(1, emptyQueue.Count / 20);
            for (int i = 0; i < drainCount && emptyQueue.Count > 0; i++)
            {
                var coord = emptyQueue.Peek();
                if (!cells.TryGetValue(coord, out var rCell) || !rCell.IsEmpty)
                {
                    emptyQueue.Dequeue();
                    continue;
                }
                if (Find.TickManager.TicksGame - rCell.emptyTick >= ROUGH_EMPTY_TTL)
                {
                    emptyQueue.Dequeue();
                    cells.Remove(coord);
                }
                else
                    break;
            }
        }

        public IEnumerable<KeyValuePair<(int cx, int cy, int cz), RoughCell>> GetAllNonEmptyCells()
        {
            foreach (var kv in cells)
                yield return kv;
        }

        public int CellCount => cells.Count;

        public void DebugPrintStats(System.Text.StringBuilder sb)
        {
            int entities = 0;
            foreach (var c in cells.Values)
                entities += c.Count;
            sb.Append(" 粗网格格数=").Append(cells.Count)
              .Append(" 实体=").Append(entities)
              .Append(" 空队=").Append(emptyQueue.Count);
        }

        public void ApplyChanges() { }

        public void Clear()
        {
            cells.Clear();
            emptyQueue.Clear();
        }
    }
}
