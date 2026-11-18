using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 天体管理器 GameComponent。
    /// </summary>
    public class CelestialBodyManager : GameComponent
    {
        private Dictionary<int, CelestialBodyDef> bodiesById;
        private bool initialized;

        // ── 天体-棋子双向索引 ──
        private Dictionary<int, HashSet<int>> bodyToGroupIds = new Dictionary<int, HashSet<int>>();
        private Dictionary<int, HashSet<int>> bodyToEntityIds = new Dictionary<int, HashSet<int>>();
        private Dictionary<int, int> groupToBodyId = new Dictionary<int, int>();
        private Dictionary<int, int> entityToBodyId = new Dictionary<int, int>();

        public CelestialBodyManager(Game game)
        {
        }

        private void EnsureInitialized()
        {
            if (initialized) return;
            initialized = true;

            var allDefs = DefDatabase<CelestialBodyDef>.AllDefsListForReading;
            bodiesById = new Dictionary<int, CelestialBodyDef>(allDefs.Count);

            foreach (var def in allDefs)
            {
                if (bodiesById.ContainsKey(def.bodyId))
                {
                    Log.Error(string.Format("[CelestialBodyManager] Duplicate bodyId {0} between '{1}' and '{2}'.",
                        def.bodyId, def.defName, bodiesById[def.bodyId].defName));
                    continue;
                }
                bodiesById.Add(def.bodyId, def);
            }

            // ── 将 RimWorld 世界对接到地球天体 ──
            SpaceConstants.SyncWithWorld();

            if (bodiesById.TryGetValue(1, out var earthDef))
            {
                earthDef.radius = SpaceConstants.PlanetRadiusMeters;
                // 仅当 XML 显式定义了有效引力参数时才覆盖默认值，否则保留 SpaceConstants 的硬编码兜底
                if (earthDef.gravitationalParameter > 0.0)
                    SpaceConstants.PrimaryGravitationalParameter = earthDef.gravitationalParameter;
            }
            else
            {
                Log.Warning("[CelestialBodyManager] Earth (bodyId=1) not found in DefDatabase. Cannot sync world radius.");
            }

            // ── 构建 行星→卫星 正向索引（运行时零查询开销）──
            foreach (var def in allDefs)
            {
                if (def.ParentBodyId >= 0 && bodiesById.TryGetValue(def.ParentBodyId, out var parent))
                {
                    parent.moonBodies.Add(def);
                }
            }

            Log.Message(string.Format("[CelestialBodyManager] Loaded {0} celestial bodies, built moon indices. World radius = {1:F0} m ({2} tiles).",
                allDefs.Count, SpaceConstants.PlanetRadiusMeters, (int)(SpaceConstants.PlanetRadiusMeters / SpaceConstants.MetersPerUnit)));
        }

        // ── 查询方法 ──

        public CelestialBodyDef GetBodyById(int bodyId)
        {
            EnsureInitialized();
            bodiesById.TryGetValue(bodyId, out var def);
            return def;
        }

        public List<CelestialBodyDef> GetAllBodies()
        {
            EnsureInitialized();
            return bodiesById.Values.ToList();
        }

        public List<CelestialBodyDef> GetBodiesByType(CelestialBodyType type)
        {
            EnsureInitialized();
            return bodiesById.Values.Where(b => b.bodyType == type).ToList();
        }

        public List<CelestialBodyDef> GetGravitySources()
        {
            EnsureInitialized();
            return bodiesById.Values.Where(b => b.IsGravitySource).ToList();
        }

        public List<CelestialBodyDef> GetChildrenOf(int parentBodyId)
        {
            EnsureInitialized();
            return bodiesById.Values.Where(b => b.ParentBodyId == parentBodyId).ToList();
        }

        public CelestialBodyDef GetStar()
        {
            EnsureInitialized();
            return bodiesById.Values.FirstOrDefault(b => b.bodyType == CelestialBodyType.Star);
        }

        public List<CelestialBodyDef> GetPlanets()
        {
            EnsureInitialized();
            return bodiesById.Values.Where(b => b.bodyType == CelestialBodyType.Planet && b.ParentBodyId == -1).ToList();
        }

        public List<CelestialBodyDef> GetMoonsOf(int planetBodyId)
        {
            EnsureInitialized();
            return bodiesById.Values.Where(b => b.bodyType == CelestialBodyType.Moon && b.ParentBodyId == planetBodyId).ToList();
        }

        public string GetBodyName(int bodyId)
        {
            var def = GetBodyById(bodyId);
            return def != null ? def.label : "UnknownBody_" + bodyId;
        }

        // ── 天体-棋子双向索引管理 ──

        public void RegisterGroupToBody(int groupId, int bodyId)
        {
            EnsureInitialized();
            if (!bodiesById.ContainsKey(bodyId))
            {
                Log.Warning(string.Format("[CelestialBodyManager] Cannot register group {0} to unknown body {1}.", groupId, bodyId));
                return;
            }
            UnregisterGroupFromBody(groupId);

            groupToBodyId[groupId] = bodyId;
            if (!bodyToGroupIds.TryGetValue(bodyId, out var set))
            {
                set = new HashSet<int>();
                bodyToGroupIds[bodyId] = set;
            }
            set.Add(groupId);
        }

        public void UnregisterGroupFromBody(int groupId)
        {
            if (groupToBodyId.TryGetValue(groupId, out var oldBodyId))
            {
                groupToBodyId.Remove(groupId);
                if (bodyToGroupIds.TryGetValue(oldBodyId, out var set))
                {
                    set.Remove(groupId);
                    if (set.Count == 0)
                        bodyToGroupIds.Remove(oldBodyId);
                }
            }
        }

        public void RegisterEntityToBody(int entityId, int bodyId)
        {
            EnsureInitialized();
            if (!bodiesById.ContainsKey(bodyId))
            {
                Log.Warning(string.Format("[CelestialBodyManager] Cannot register entity {0} to unknown body {1}.", entityId, bodyId));
                return;
            }
            UnregisterEntityFromBody(entityId);

            entityToBodyId[entityId] = bodyId;
            if (!bodyToEntityIds.TryGetValue(bodyId, out var set))
            {
                set = new HashSet<int>();
                bodyToEntityIds[bodyId] = set;
            }
            set.Add(entityId);
        }

        public void UnregisterEntityFromBody(int entityId)
        {
            if (entityToBodyId.TryGetValue(entityId, out var oldBodyId))
            {
                entityToBodyId.Remove(entityId);
                if (bodyToEntityIds.TryGetValue(oldBodyId, out var set))
                {
                    set.Remove(entityId);
                    if (set.Count == 0)
                        bodyToEntityIds.Remove(oldBodyId);
                }
            }
        }

        public IReadOnlyCollection<int> GetGroupIdsForBody(int bodyId)
        {
            if (bodyToGroupIds.TryGetValue(bodyId, out var set))
                return set;
            return System.Array.Empty<int>();
        }

        public IReadOnlyCollection<int> GetEntityIdsForBody(int bodyId)
        {
            if (bodyToEntityIds.TryGetValue(bodyId, out var set))
                return set;
            return System.Array.Empty<int>();
        }

        public int GetBodyIdForGroup(int groupId)
        {
            return groupToBodyId.TryGetValue(groupId, out var bodyId) ? bodyId : -1;
        }

        public int GetBodyIdForEntity(int entityId)
        {
            return entityToBodyId.TryGetValue(entityId, out var bodyId) ? bodyId : -1;
        }

        /// <summary>
        /// 获取任意天体所属的根行星 ID。
        /// 月球 → 地球 ID；地球 → 自身 ID；恒星 → -1（不使用区块系统）。
        /// </summary>
        public int GetRootPlanetId(int bodyId)
        {
            var body = GetBodyById(bodyId);
            if (body == null) return -1;

            while (body != null && body.bodyType != CelestialBodyType.Planet)
            {
                if (body.ParentBodyId < 0)
                    return -1;
                body = GetBodyById(body.ParentBodyId);
            }

            return body?.bodyId ?? -1;
        }

        /// <summary>
        /// 获取天体所在的参考系 ID（粗网格分区键）。
        /// 行星 → 自身 ID；卫星 → 根行星 ID；恒星 → 恒星 ID；深空 → -1。
        /// </summary>
        public int GetReferenceFrameId(int bodyId)
        {
            var body = GetBodyById(bodyId);
            if (body == null) return -1;

            while (body != null)
            {
                if (body.bodyType == CelestialBodyType.Planet)
                    return body.bodyId;
                if (body.bodyType == CelestialBodyType.Star)
                    return body.bodyId;
                if (body.ParentBodyId < 0)
                    break;
                body = GetBodyById(body.ParentBodyId);
            }
            return -1;
        }

        // ── 轨道位置计算 ──

        private static void GetOrbitFrame(DVector3 normal, out DVector3 refX, out DVector3 refY)
        {
            DVector3 up = System.Math.Abs(DVector3.Dot(normal, DVector3.up)) > 0.99
                ? DVector3.forward : DVector3.up;
            refX = DVector3.Cross(normal, up).normalized;
            refY = DVector3.Cross(normal, refX).normalized;
        }

        /// <summary>
        /// 计算天体在给定 tick 的世界空间位置。
        /// </summary>
        public DVector3 GetBodyWorldPosition(int bodyId, int tick)
        {
            var body = GetBodyById(bodyId);
            if (body == null) return DVector3.zero;
            return GetBodyWorldPosition(body, tick);
        }

        private DVector3 GetBodyWorldPosition(CelestialBodyDef body, int tick)
        {
            DVector3 localPos = DVector3.zero;

            if (body.HasOrbit)
            {
                double angularVelocity = 2.0 * System.Math.PI / body.orbitPeriod;
                double phase = body.initialPhase + angularVelocity * tick;

                DVector3 normal = body.OrbitNormalD.normalized;
                DVector3 refX, refY;
                GetOrbitFrame(normal, out refX, out refY);

                double cos = System.Math.Cos(phase);
                double sin = System.Math.Sin(phase);

                localPos = body.orbitRadius * (cos * refX + sin * refY);
            }

            if (body.ParentBodyId >= 0)
            {
                var parent = GetBodyById(body.ParentBodyId);
                if (parent != null)
                    return localPos + GetBodyWorldPosition(parent, tick);
            }

            return localPos;
        }

        /// <summary>
        /// 计算天体在给定 tick 的世界空间速度（圆轨解析）。
        /// 速度方向沿轨道切向，大小 = ω × orbitRadius。
        /// 对于 Star 或没有轨道的天体返回零向量。
        /// </summary>
        public DVector3 GetBodyWorldVelocity(int bodyId, int tick)
        {
            var body = GetBodyById(bodyId);
            if (body == null || !body.HasOrbit) return DVector3.zero;
            return GetBodyWorldVelocity(body, tick);
        }

        private DVector3 GetBodyWorldVelocity(CelestialBodyDef body, int tick)
        {
            if (!body.HasOrbit) return DVector3.zero;

            double angularVelocity = 2.0 * System.Math.PI / body.orbitPeriod;
            double phase = body.initialPhase + angularVelocity * tick;

            DVector3 normal = body.OrbitNormalD.normalized;
            DVector3 refX, refY;
            GetOrbitFrame(normal, out refX, out refY);

            // 位置 = R·(cos·refX + sin·refY)
            // 速度 = ω·R·(-sin·refX + cos·refY)
            double cos = System.Math.Cos(phase);
            double sin = System.Math.Sin(phase);
            DVector3 localVel = (body.orbitRadius * angularVelocity) * (-sin * refX + cos * refY);

            // 若绕父天体公转，父天体本身也可能在运动 → 叠加父天体速度
            if (body.ParentBodyId >= 0)
            {
                var parent = GetBodyById(body.ParentBodyId);
                if (parent != null)
                    return localVel + GetBodyWorldVelocity(parent, tick);
            }

            return localVel;
        }

        /// <summary>
        /// 计算天体在其父天体中心坐标系中的轨道位置。
        /// 例如月球的返回值是「以地球为原点的月球轨道位置」。
        /// 对没有轨道或 Star 类型返回零向量。
        /// </summary>
        public DVector3 GetBodyLocalPosition(int bodyId, int tick)
        {
            var body = GetBodyById(bodyId);
            if (body == null || !body.HasOrbit) return DVector3.zero;

            double angularVelocity = 2.0 * System.Math.PI / body.orbitPeriod;
            double phase = body.initialPhase + angularVelocity * tick;

            DVector3 normal = body.OrbitNormalD.normalized;
            DVector3 refX, refY;
            GetOrbitFrame(normal, out refX, out refY);

            double cos = System.Math.Cos(phase);
            double sin = System.Math.Sin(phase);
            return body.orbitRadius * (cos * refX + sin * refY);
        }

        /// <summary>
        /// 计算天体在其父天体中心坐标系中的轨道速度。
        /// 对没有轨道或 Star 类型返回零向量。
        /// </summary>
        public DVector3 GetBodyLocalVelocity(int bodyId, int tick)
        {
            var body = GetBodyById(bodyId);
            if (body == null || !body.HasOrbit) return DVector3.zero;

            double angularVelocity = 2.0 * System.Math.PI / body.orbitPeriod;
            double phase = body.initialPhase + angularVelocity * tick;

            DVector3 normal = body.OrbitNormalD.normalized;
            DVector3 refX, refY;
            GetOrbitFrame(normal, out refX, out refY);

            double cos = System.Math.Cos(phase);
            double sin = System.Math.Sin(phase);
            return (body.orbitRadius * angularVelocity) * (-sin * refX + cos * refY);
        }

        /// <summary>
        /// 获取所有天体在指定 tick 时的世界空间位置字典。
        /// </summary>
        public Dictionary<int, DVector3> GetAllBodyWorldPositions(int tick)
        {
            EnsureInitialized();
            var result = new Dictionary<int, DVector3>(bodiesById.Count);
            foreach (var kv in bodiesById)
            {
                result[kv.Key] = GetBodyWorldPosition(kv.Key, tick);
            }
            return result;
        }

        /// <summary>
        /// 生成天体的轨道路径采样点（用于绘制轨道线）。
        /// 返回世界空间中的 Unity 单位位置列表。
        /// </summary>
        public List<Vector3> GetOrbitPathUnity(int bodyId, int tick, int segments = 64)
        {
            var body = GetBodyById(bodyId);
            if (body == null || !body.HasOrbit) return null;

            DVector3 centerWorld = DVector3.zero;
            if (body.ParentBodyId >= 0)
            {
                var parent = GetBodyById(body.ParentBodyId);
                if (parent != null)
                    centerWorld = GetBodyWorldPosition(parent, tick);
            }

            DVector3 normal = body.OrbitNormalD.normalized;
            DVector3 refX, refY;
            GetOrbitFrame(normal, out refX, out refY);

            var points = new List<Vector3>(segments);
            double toUnity = SpaceConstants.UnitsPerMeter;

            for (int i = 0; i < segments; i++)
            {
                double angle = 2.0 * System.Math.PI * i / segments;
                double cos = System.Math.Cos(angle);
                double sin = System.Math.Sin(angle);
                DVector3 pWorld = centerWorld + body.orbitRadius * (cos * refX + sin * refY);
                points.Add((Vector3)(pWorld * toUnity));
            }

            return points;
        }

        public static Color GetBodyColor(CelestialBodyDef body)
        {
            switch (body.bodyType)
            {
                case CelestialBodyType.Star:
                    return new Color(1f, 0.8f, 0.2f);
                case CelestialBodyType.Planet:
                    return new Color(0.2f, 0.5f, 0.8f);
                case CelestialBodyType.Moon:
                    return new Color(0.7f, 0.7f, 0.7f);
                default:
                    return Color.white;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                initialized = false;
            }
        }
    }
}
