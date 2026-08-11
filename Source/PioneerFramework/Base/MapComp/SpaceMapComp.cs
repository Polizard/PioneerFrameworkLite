using System.Collections.Generic;
using Verse;

namespace PioneerFramework
{
    public class SpaceMapComp : MapComponent
    {
        // 可扩展的持久化数据，例如空间站名称、内部建筑列表等
        private string stationName = "Unnamed Station";

        /// <summary>Map → Entity：此地图所属的 SpaceEntity ID。</summary>
        public int ownerEntityId = -1;

        /// <summary>Map → Part：此地图对应的 SpacePart.partId（在 ownerEntity 的 Parts 列表中）。</summary>
        public int linkedPartId = -1;

        // ── 工质聚合向量（dV 体系）──
        // 键 = 工质 Def，值 = 该工质在当前地图（=船）内的总质量（kg）。
        // 多储罐同种工质自然累加；一条船可同时携带多种工质（液氢/化学/氙……）。
        // 该向量是运行时聚合缓存，不落盘：由各储罐建筑在 Spawn 时 +、
        // 在加注/消耗/摧毁时 ±，读档/取档后由建筑重建，避免重复记账。
        private Dictionary<PropellantDef, float> propellantMass = new Dictionary<PropellantDef, float>();

        public string StationName
        {
            get => stationName;
            set => stationName = value;
        }

        public SpaceMapComp(Map map) : base(map) { }

        /// <summary>某工质的当前总质量（kg）。未记录返回 0。</summary>
        public float GetPropellantMass(PropellantDef def)
        {
            if (def == null) return 0f;
            return propellantMass.TryGetValue(def, out var m) ? m : 0f;
        }

        /// <summary>
        /// 增减某工质的总质量（kg）。delta 可为负（消耗/摧毁）；结果不落负。
        /// 向量 = Σ 全部储罐存量，由储罐 Comp 在变化点调用。
        /// </summary>
        public void AddPropellantMass(PropellantDef def, float delta)
        {
            if (def == null || delta == 0f) return;
            float next = GetPropellantMass(def) + delta;
            if (next <= 0.0001f)
                propellantMass.Remove(def);
            else
                propellantMass[def] = next;
        }

        /// <summary>全部工质 → 质量 的快照（调试 / dV 计算消费方）。</summary>
        public IEnumerable<KeyValuePair<PropellantDef, float>> PropellantSnapshot
        {
            get
            {
                foreach (var kv in propellantMass)
                    yield return kv;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref stationName, "stationName", "Unnamed Station");
            Scribe_Values.Look(ref ownerEntityId, "ownerEntityId", -1);
            Scribe_Values.Look(ref linkedPartId, "linkedPartId", -1);
            // propellantMass 为聚合缓存，不序列化（见字段注释）。
        }
    }
}