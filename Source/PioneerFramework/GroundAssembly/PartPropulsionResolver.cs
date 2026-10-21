using System;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// **设计规格 → 推进聚合**（批 5 余下第一件，2026-10-17）。
    ///
    /// 目录是推进的**权威出处**：`ShipPartDef` 一旦收录某件，它的 `exhaustVelocity` /
    /// `massFlowRate` / `tankCapacityKg` 就是该件的推进参数，不再去查建筑 `ThingDef` 的 Comp。
    /// 目录没有收录的条目按旧链兜底（ThingDef 的 `CompProperties_Thruster` / `CompProperties_PropellantTank`），
    /// 两者都没有则保持调试兜底值（ve=3000 · ṁ=10/台 · 罐容=1000/件）——与旧实现逐位一致。
    ///
    /// 聚合口径：
    /// · 推力 = Σ ve_i·ṁ_i，等效 ve = Σve_i·ṁ_i / Σṁ_i（多型号混装时按质量流加权，不是"最后一台赢"）；
    /// · 罐容 = Σ 单件容量 × 数量；
    /// · 工质 = 首个非空 `propellantDefName`（推进器优先、储罐其次），空 ⇒ 调用方取默认工质。
    ///
    /// 本类**不碰 DefDatabase 以外的状态、不建对象**：有图（`SpaceMapComp` 虚拟推进）与
    /// 无图（`PropulsionData`）两个注入分支共用同一份解析，避免两处聚合口径漂移。
    /// </summary>
    public static class PartPropulsionResolver
    {
        public const float DefaultExhaustVelocity = 3000f;
        public const float DefaultMassFlowPerThruster = 10f;
        public const float DefaultTankCapacity = 1000f;

        /// <summary>解析结果（数量按 quantity 展开后的总数）。</summary>
        public struct Summary
        {
            public int thrusterCount;
            public int tankCount;

            /// <summary>其中由目录 `ShipPartDef` 提供参数的件数（读数/验收用）。</summary>
            public int catalogThrusters;
            public int catalogTanks;

            /// <summary>质量流加权等效排气速度（无推进器时 = 默认 3000）。</summary>
            public float exhaustVelocity;

            /// <summary>总质量流率（kg/s）。</summary>
            public float massFlow;

            /// <summary>总储罐容量（kg）。</summary>
            public float capacity;

            /// <summary>工质 DefName（可能是空串 ⇒ 调用方用默认工质）。</summary>
            public string propellantDefName;
        }

        /// <param name="catalogLookup">目录查询（测试注入；null ⇒ `DefDatabase&lt;ShipPartDef&gt;`）。</param>
        /// <param name="thingDefLookup">建筑 Def 查询（测试注入；null ⇒ `DefDatabase&lt;ThingDef&gt;`）。</param>
        public static Summary Resolve(RocketDesignSpec? spec,
            Func<string, ShipPartDef?>? catalogLookup = null,
            Func<string, ThingDef?>? thingDefLookup = null)
        {
            Summary s = default;
            s.exhaustVelocity = DefaultExhaustVelocity;
            s.propellantDefName = string.Empty;
            if (spec?.parts == null) return s;

            catalogLookup ??= name => DefDatabase<ShipPartDef>.GetNamedSilentFail(name);
            thingDefLookup ??= name => DefDatabase<ThingDef>.GetNamedSilentFail(name);

            double thrustSum = 0.0;   // Σ ve·ṁ，用于质量流加权等效 ve

            for (int i = 0; i < spec.parts.Count; i++)
            {
                DesignPartSpec? entry = spec.parts[i];
                if (entry == null) continue;

                int qty = Math.Max(1, entry.quantity);
                ShipPartDef? catalogDef = catalogLookup(entry.defName);

                if (entry.category == "thruster")
                {
                    s.thrusterCount += qty;
                    if (catalogDef != null)
                    {
                        if (catalogDef.massFlowRate <= 0f) continue;   // 目录已收录且没给流率 ⇒ 该件不是工作引擎
                        float ve = catalogDef.exhaustVelocity > 0f
                            ? catalogDef.exhaustVelocity
                            : DefaultExhaustVelocity;
                        s.massFlow += catalogDef.massFlowRate * qty;
                        thrustSum += (double)ve * catalogDef.massFlowRate * qty;
                        s.catalogThrusters += qty;
                        TakePropellant(ref s.propellantDefName, catalogDef.propellantDefName);
                        continue;
                    }

                    var thrusterProps = FindComp<CompProperties_Thruster>(thingDefLookup(entry.defName));
                    if (thrusterProps != null)
                    {
                        float ve = thrusterProps.exhaustVelocity > 0f
                            ? thrusterProps.exhaustVelocity
                            : DefaultExhaustVelocity;
                        s.massFlow += thrusterProps.massFlowRate * qty;
                        thrustSum += (double)ve * thrusterProps.massFlowRate * qty;
                        TakePropellant(ref s.propellantDefName, thrusterProps.propellantDefName);
                    }
                    else
                    {
                        // 目录/建筑都没有：保持旧调试兜底（调试 spec 的 Def 本来就不存在）
                        s.massFlow += DefaultMassFlowPerThruster * qty;
                        thrustSum += (double)DefaultExhaustVelocity * DefaultMassFlowPerThruster * qty;
                    }
                }
                else if (entry.category == "tank")
                {
                    s.tankCount += qty;
                    if (catalogDef != null)
                    {
                        if (catalogDef.tankCapacityKg <= 0f) continue;   // 目录已收录且没给容量 ⇒ 该件不是工作储罐
                        s.capacity += catalogDef.tankCapacityKg * qty;
                        s.catalogTanks += qty;
                        TakePropellant(ref s.propellantDefName, catalogDef.propellantDefName);
                        continue;
                    }

                    var tankProps = FindComp<CompProperties_PropellantTank>(thingDefLookup(entry.defName));
                    if (tankProps != null)
                    {
                        s.capacity += tankProps.capacity * qty;
                        TakePropellant(ref s.propellantDefName, tankProps.propellantDefName);
                    }
                    else
                    {
                        s.capacity += DefaultTankCapacity * qty;
                    }
                }
            }

            if (s.massFlow > 0f && thrustSum > 0.0)
                s.exhaustVelocity = (float)(thrustSum / s.massFlow);
            return s;
        }

        private static T? FindComp<T>(ThingDef? def) where T : CompProperties
        {
            if (def?.comps == null) return null;
            for (int i = 0; i < def.comps.Count; i++)
            {
                if (def.comps[i] is T match) return match;
            }
            return null;
        }

        private static void TakePropellant(ref string current, string? candidate)
        {
            if (current.Length == 0 && !string.IsNullOrEmpty(candidate))
                current = candidate;
        }
    }
}
