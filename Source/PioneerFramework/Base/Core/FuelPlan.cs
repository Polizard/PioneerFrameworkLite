using System.Collections.Generic;
using PioneerFramework.Components;

namespace PioneerFramework
{
    /// <summary>
    /// **B6：供油计划**——把同一供油组、同一工质的 tank part 预排成“层级计划”，
    /// 消耗/加注只按计划改罐，不再每步全量重分配。
    ///
    /// 口径（2026-10-18 用户定稿；**同日二次裁定：只切罐**）：
    /// - `fuelGroupId`：0 = 主网；切成新组 = **只有该 part 的储罐**脱离主网。
    ///   引擎不按此字段分组，仍由地图/实体级推进聚合统一取油；因此切出的罐在当前实现里
    ///   是“封存/预留”，不会被主网引擎烧掉（引擎侧分组后置，不在本口径内）。
    /// - `fuelPriority`：0 = 无优先级（同层按**剩余质量比例**消耗）；1..N 低层先烧。
    /// - 消耗：低优先级先烧；同层内按剩余质量比例（一遍扫描，不排序）。
    /// - 加注：**高优先级先加满**（保证高优先级罐不被日常加注打散）。
    /// - 计划只在拓扑/优先级变化时重建；热路径零分配、零排序、无 Def 查找。
    /// </summary>
    public sealed class FuelPlan
    {
        private readonly struct Slot
        {
            public readonly SpacePart Part;
            public readonly PropellantTankComponentData Tank;
            public readonly byte Priority;

            public Slot(SpacePart part, PropellantTankComponentData tank)
            {
                Part = part;
                Tank = tank;
                Priority = part.fuelPriority;
            }
        }

        private readonly List<Slot> slots = new();
        private readonly List<List<int>> tiers = new();   // 索引 = 优先级；每层存 slot 下标

        public string PropellantDefName { get; private set; } = "";
        public int GroupId { get; private set; }
        public int Version { get; set; }
        public double TotalMass { get; private set; }
        public double TotalCapacity { get; private set; }
        public int TankCount => slots.Count;
        public bool HasTanks => slots.Count > 0;

        /// <summary>重建计划（调用方在拓扑/优先级变化时调用；O(parts)，不参与 tick）。</summary>
        public void Build(IReadOnlyList<SpacePart> parts, string propellantDefName, int groupId)
        {
            slots.Clear();
            tiers.Clear();
            PropellantDefName = propellantDefName ?? "";
            GroupId = groupId;
            TotalMass = 0.0;
            TotalCapacity = 0.0;

            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null || part.isDestroyed || part.fuelGroupId != groupId) continue;
                var tank = part.GetComponent<PropellantTankComponentData>();
                if (tank == null || tank.propellantDefName != PropellantDefName) continue;
                if (!tank.feedEnabled) continue;   // 阀门关（非活动级）：不进供油计划
                if (tank.capacityKg <= 0.0) continue;

                int idx = slots.Count;
                slots.Add(new Slot(part, tank));
                TotalMass += tank.massKg;
                TotalCapacity += tank.capacityKg;

                int tier = part.fuelPriority;
                while (tiers.Count <= tier) tiers.Add(new List<int>(4));
                tiers[tier].Add(idx);
            }
        }

        /// <summary>按优先级消耗（低层先烧；层内按剩余质量比例）。返回实际消耗 kg。</summary>
        public double Consume(double kg)
        {
            if (!(kg > 0.0) || TotalMass <= 0.0) return 0.0;
            double remaining = kg;

            for (int t = 0; t < tiers.Count && remaining > 1e-12; t++)
            {
                var tier = tiers[t];
                double tierMass = 0.0;
                for (int i = 0; i < tier.Count; i++)
                {
                    double m = slots[tier[i]].Tank.massKg;
                    if (m > 0.0) tierMass += m;
                }
                if (tierMass <= 0.0) continue;

                double tierTake = System.Math.Min(remaining, tierMass);
                if (tierTake <= 0.0) continue;
                double factor = tierTake / tierMass;
                double got = 0.0;
                for (int i = 0; i < tier.Count; i++)
                {
                    var tank = slots[tier[i]].Tank;
                    double m = tank.massKg;
                    if (!(m > 0.0)) continue;
                    double take = m * factor;
                    tank.massKg = m - take;
                    got += take;
                }
                remaining -= got;
                TotalMass = System.Math.Max(0.0, TotalMass - got);
            }

            return kg - System.Math.Max(0.0, remaining);
        }

        /// <summary>按优先级加注（高层先加满）。返回实际加注 kg。</summary>
        public double Fill(double kg)
        {
            if (!(kg > 0.0)) return 0.0;
            double remaining = kg;
            for (int t = tiers.Count - 1; t >= 0 && remaining > 1e-12; t--)
            {
                var tier = tiers[t];
                for (int i = 0; i < tier.Count && remaining > 1e-12; i++)
                {
                    var tank = slots[tier[i]].Tank;
                    double room = tank.capacityKg - tank.massKg;
                    if (!(room > 0.0)) continue;
                    double take = System.Math.Min(room, remaining);
                    tank.massKg += take;
                    TotalMass += take;
                    remaining -= take;
                }
            }
            return kg - System.Math.Max(0.0, remaining);
        }

        /// <summary>按罐当前值刷新汇总（外部直接改过 tank.massKg 后调；O(n)）。</summary>
        public void RefreshTotals()
        {
            double mass = 0.0, cap = 0.0;
            for (int i = 0; i < slots.Count; i++)
            {
                mass += slots[i].Tank.massKg;
                cap += slots[i].Tank.capacityKg;
            }
            TotalMass = mass;
            TotalCapacity = cap;
        }
    }
}
