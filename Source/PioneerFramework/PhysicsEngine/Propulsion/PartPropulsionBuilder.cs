using System;
using System.Collections.Generic;
using PioneerFramework.Components;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **零件 → 推进合成**（2026-10-26；用户裁定"导弹/卫星 = 一个 part 叠控制核心+工质罐+引擎"）。
    ///
    /// 为什么需要它：推力原先只由**发射装配链**（`LaunchService` → `PartPropulsionResolver`）写进
    /// `PropulsionData` / 地图虚拟推进；通用组创建（导弹/载荷/调试）不会读零件上的
    /// `EngineComponentData` / `PropellantTankComponentData` ⇒ 一个 part 叠引擎+罐**没有 Δv**。
    ///
    /// 语义（与用户对过的三条约束）：
    /// · **只补空**：实体已有显式推进（`HasPropulsionData`）⇒ 不碰（导弹的武器定义/调试船的 ve/ṁ 是外部权威）；
    /// · **只管无图**：有图船的推进在地图侧（`SpaceMapComp` 虚拟引擎/罐），本类不越界；
    /// · **生成一次 + 变更重建**：`RegisterGroup` 首次调用；之后由 `SpaceEntity.InvalidateStats`
    ///   （部件集/组件变化的唯一收口）在 `PropulsionFromParts` 为真时重建。
    ///
    /// 聚合口径照 `PartPropulsionResolver`：等效 ve = `Σ(ve·ṁ) / Σṁ`（质量流加权，不是"最后一台赢"）；
    /// 同 `(工质, 是否 RCS)` 一组网络；无引擎的组不产生网络（只有罐没有推力源）。
    /// </summary>
    public static class PartPropulsionBuilder
    {
        /// <summary>
        /// 若实体还没有推进、且是无图船，则从零件合成 `PropulsionData`。返回是否真的写了推进。
        /// </summary>
        /// <param name="defLookup">工质 Def 查询（测试注入；null ⇒ `DefDatabase`）。</param>
        public static bool BuildForMapless(SpaceEntity entity, Func<string, PropellantDef?>? defLookup = null)
        {
            if (entity == null || entity.HasPropulsionData) return false;
            if (entity.HasMap) return false;   // 有图船的推进在地图侧（虚拟引擎/罐），本类不越界
            defLookup ??= name => string.IsNullOrEmpty(name) ? null : DefDatabase<PropellantDef>.GetNamedSilentFail(name);

            var groups = new List<Group>();
            var parts = entity.PartsList;
            // 2026-11-03（C-full）：活动集 = 最大级 + 标了“并联点火”的下级（可传递；唯一出处 StagePartition）。
            // 串行火箭仍只烧最大级；并联火箭（芯级 + 助推器）一起烧，但**每块一池**（stageId/blockId 分组）。
            var activeStages = StagePartition.ActiveStages(parts);

            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null || part.isDestroyed) continue;
                if (!activeStages.Contains(part.stageId)) continue;   // 非活动级的引擎/罐是死重

                var engine = part.GetComponent<EngineComponentData>();
                if (engine != null && engine.massFlowRate > 0f && engine.exhaustVelocity > 0f)
                    Get(groups, engine.propellantDefName, engine.isVectoredRcs, part.stageId, part.blockId).AddEngine(engine);

                var tank = part.GetComponent<PropellantTankComponentData>();
                if (tank != null && tank.capacityKg > 0.0)
                    Get(groups, tank.propellantDefName, tank.rcsSupply, part.stageId, part.blockId).AddTank(tank);
            }

            bool wrote = false;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (g.flow <= 0f || g.engineCount == 0) continue;
                var def = defLookup(g.propellantDefName);
                if (def == null) continue;

                entity.AddPropulsion(new PropulsionData
                {
                    propellant = def,
                    ve = (float)(g.veFlowSum / g.flow),
                    massFlow = g.flow,
                    mass = (float)g.tankMass,
                    capacity = (float)g.tankCapacity,
                    isVectoredRcs = g.isVectoredRcs,
                    stageId = g.stageId,
                    blockId = g.blockId,
                });
                wrote = true;
            }
            if (wrote) entity.PropulsionFromParts = true;
            return wrote;
        }

        /// <summary>清掉零件合成的推进并按当前零件重建（`InvalidateStats` 调用；只在 `PropulsionFromParts` 时）。</summary>
        public static void RebuildMapless(SpaceEntity entity, Func<string, PropellantDef?>? defLookup = null)
        {
            if (entity == null || !entity.PropulsionFromParts) return;
            entity.propulsion.Clear();
            entity.InvalidatePropulsion();
            BuildForMapless(entity, defLookup);
        }

        private sealed class Group
        {
            public string propellantDefName = "";
            public bool isVectoredRcs;
            public int stageId;
            public int blockId;
            public int engineCount;
            public float flow;
            public double veFlowSum;
            public double tankCapacity;
            public double tankMass;

            public void AddEngine(EngineComponentData e)
            {
                engineCount++;
                flow += e.massFlowRate;
                veFlowSum += (double)e.exhaustVelocity * e.massFlowRate;
            }

            public void AddTank(PropellantTankComponentData t)
            {
                tankCapacity += t.capacityKg;
                tankMass += t.massKg;
            }
        }

        private static Group Get(List<Group> groups, string propellant, bool rcs, int stageId, int blockId)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (g.isVectoredRcs == rcs && g.propellantDefName == propellant
                    && g.stageId == stageId && g.blockId == blockId) return g;
            }
            var ng = new Group
            {
                propellantDefName = propellant ?? "",
                isVectoredRcs = rcs,
                stageId = stageId,
                blockId = blockId,
            };
            groups.Add(ng);
            return ng;
        }
    }
}
