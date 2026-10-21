using System;
using System.Collections.Generic;
using System.Globalization;
using PioneerFramework.Combat;
using PioneerFramework.Components;
using PioneerFramework.GroundAssembly;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **多part 一键验收**（2026-10-17，用户："我其实是希望有一键测试的"）。
    ///
    /// 同步跑完、不打等待：装配器（普适 `ShipPartDef` 目录 + 舱盒/追加件）· 批 0 惯量锚点 ·
    /// 批 3 逐 part 最近命中 · 批 5 图链（舱盒 `linkedMap` / 追加件不挂图）· 批 5 推进解析（目录=唯一出处）。
    /// 结论走 `PflTestKit`（PASS/FAIL/UNCHECKED 统一词表）；**PFView 盒体可见性仍是人眼项**，
    /// 登记为 UNCHECKED，不冒充 PASS。
    /// </summary>
    public static class MultipartAcceptanceRunner
    {
        public static void Run()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Messages.Message("多part 验收：SpaceGameComp 未找到", MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (PerfAcceptanceRunner.Running)
            {
                Messages.Message("多part 验收：先等「一键验收」跑完（避免两套用例抢 PflTestKit 的当前槽）",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            PflTestKit.Reset();
            PflTestKit.SnapshotGroups();   // 2026-10-17：结束时按快照清掉本轮调试资产（防叠场/留场）
            try
            {
                RunAssembler();
                RunPartMutation();
                RunPartPose();
                RunPropulsionResolution();
                var inertiaGroup = RunInertia();
                RunHitSelection();
                RunDamagedParts(spaceGame, inertiaGroup);
                RunMultiMap(spaceGame);
                Run256Scale(spaceGame);
                RunCancelSemantics();
                RunCollisionMath();
                RunMapOnPart(spaceGame);
            }
            catch (Exception ex)
            {
                Log.Error("[多part验收] 异常：" + ex);
            }

            PflTestKit.PrintVerdict("多part 一键验收（批 0 惯量 / 批 3 每 part 命中 / 批 5 装配·推进·图链 / B5 损伤件 / B6 多图）");
            Messages.Message(
                $"多part 验收：通过 {PflTestKit.Passed}｜失败 {PflTestKit.Failed}｜未覆盖 {PflTestKit.Uncovered}｜无法断言 {PflTestKit.UncheckedCount}",
                MessageTypeDefOf.TaskCompletion, false);
            PflTestKit.CleanupSince();
        }

        /// <summary>M1：普适目录 + 装配器（舱盒 Part[0]、追加件 −Y、质量 = Σ零件、不挂图）。</summary>
        private static void RunAssembler()
        {
            PflTestKit.Begin("M1 装配器：舱盒 + 普适目录追加件", "多part");
            var spec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "PF_ShipPart_Thruster_Debug", category = "thruster", quantity = 1 },
                    new() { defName = "PF_ShipPart_Tank_Debug", category = "tank", quantity = 2 },
                    new() { defName = "PF_ShipPart_Stage_Debug", category = "stage", quantity = 1 },
                }
            };
            var parts = DesignPartAssembler.BuildParts(spec, 40, 40, out SpacePart cabin);

            PflTestKit.Check(PflTestKit.Kind.Invariant, parts.Count == 5, "件数=5（舱盒+推进器+储罐×2+分离环）", parts.Count.ToString());
            PflTestKit.Check(PflTestKit.Kind.Invariant,
                cabin.partId == 1 && cabin.positionShip.sqrMagnitude == 0.0, "舱盒=Part[0] 且在原点",
                $"partId={cabin.partId} pos={cabin.positionShip}");
            PflTestKit.Check(PflTestKit.Kind.Invariant, cabin.linkedMapId == -1, "装配器不填 linkedMapId（留给图链）");
            PflTestKit.Check(PflTestKit.Kind.Value,
                Math.Abs(parts[1].positionShip.y + 27.0) < 1e-6, "首件（推进器）中心 y=-27", parts[1].positionShip.y.ToString("F3"));
            PflTestKit.Check(PflTestKit.Kind.Value,
                Math.Abs(parts[4].positionShip.y + 42.0) < 1e-6, "末件（分离环）中心 y=-42", parts[4].positionShip.y.ToString("F3"));

            bool appendedUnlinked = true;
            double total = cabin.Mass;
            for (int i = 1; i < parts.Count; i++)
            {
                if (parts[i].linkedMapId >= 0) appendedUnlinked = false;
                total += parts[i].Mass;
            }
            PflTestKit.Check(PflTestKit.Kind.Invariant, appendedUnlinked, "追加件全部不挂图（linkedMapId=-1）");
            double cabinBase = cabin.isSealed ? SpacePart.SealedPlaceholderMassKg : 1.0;
            double expectedMass = cabinBase + 500.0 + 2 * 200.0 + 100.0;
            PflTestKit.Check(PflTestKit.Kind.Value,
                Math.Abs(total - expectedMass) < 0.5, "质量=Σ零件（舱盒 1/封闭100kg + 目录质量）", total.ToString("F0"));
            PflTestKit.End();
        }

        /// <summary>B3a：零件自身姿态（OBB AABB / Ray-OBB；identity 旧路径保持）。</summary>
        private static void RunPartPose()
        {
            PflTestKit.Begin("B3 零件姿态：OBB AABB / Ray-OBB", "多part");
            var e = new SpaceEntity(9102, 0);
            var p = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_Cube",
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(4, 2, 2),
                rotationShip = DQuaternion.AngleAxis(System.Math.PI / 2.0, DVector3.up),
                hp = 100f,
            };
            p.AddComponent(new HullMassComponent { mass = 100f });
            e.SetParts(new List<SpacePart> { p });

            e.GetPartAabbSystem(0, out DVector3 min, out DVector3 max);
            PflTestKit.Check(PflTestKit.Kind.Value,
                Math.Abs(min.x + 1.0) < 1e-9 && Math.Abs(min.y + 1.0) < 1e-9
                && Math.Abs(min.z + 2.0) < 1e-9 && Math.Abs(max.z - 2.0) < 1e-9,
                "旋转 90° ⇒ AABB 长轴换到 z",
                $"({min.x:F2},{min.y:F2},{min.z:F2})-({max.x:F2},{max.y:F2},{max.z:F2})");

            bool hit = CombatManager.TryPickHitPart(e, new DVector3(0, 0, -10), new DVector3(0, 0, 1), 20.0,
                out int idx, out _, out double t);
            PflTestKit.Check(PflTestKit.Kind.Value, hit && idx == 0 && Math.Abs(t - 0.4) < 1e-9,
                "Ray-OBB 命中旋转后长轴盒（t=0.4）", $"hit={hit} idx={idx} t={t:F3}");

            bool miss = !CombatManager.TryPickHitPart(e, new DVector3(1.5, 0, -10), new DVector3(0, 0, 1), 20.0,
                out _, out _, out _);
            PflTestKit.Check(PflTestKit.Kind.Invariant, miss,
                "旋转后 x=1.5 射线不命中（旧 AABB 会误判）");

            // B3b：OBB-SAT 窄相（旋转长轴盒 vs 小盒，重叠 0.3）
            var satA = new List<CombatManager.PartAabb>
            {
                new(DVector3.zero, new DVector3(2, 1, 1),
                    DQuaternion.AngleAxis(System.Math.PI / 2.0, DVector3.up), 0),
            };
            var satB = new List<CombatManager.PartAabb>
            {
                new(new DVector3(1.2, 0, 0), new DVector3(0.5, 0.5, 0.5), DQuaternion.identity, 0),
            };
            bool sat = CombatManager.TryFindDeepestPartPair(satA, satB, out DVector3 satN, out double satDepth, out _);
            PflTestKit.Check(PflTestKit.Kind.Value,
                sat && Math.Abs(satDepth - 0.3) < 1e-9 && satN.x > 0.99,
                "OBB-SAT 窄相（旋转长轴盒 depth=0.3 / 法线 +X）",
                "hit=" + sat + " depth=" + satDepth.ToString("F3", CultureInfo.InvariantCulture)
                + " n.x=" + satN.x.ToString("F3", CultureInfo.InvariantCulture));

            // B3b：形状网格生成（视觉排版另看，数据层在这里钉住）
            Mesh? cyl = PartShapeMeshes.For(InertiaTensor.Shape.Cylinder, null);
            Mesh? sph = PartShapeMeshes.For(InertiaTensor.Shape.Sphere, null);
            PflTestKit.Check(PflTestKit.Kind.Invariant,
                cyl != null && cyl.vertexCount > 0 && sph != null && sph.vertexCount > 0,
                "Cylinder/Sphere 渲染网格已生成",
                $"cylV={cyl?.vertexCount ?? 0} sphV={sph?.vertexCount ?? 0}");
            PflTestKit.End();
        }

        /// <summary>B1：运行时 part 集合（Add 分配 id / 质量与包围盒 / Move 失效 AABB / Remove 回原状）。</summary>
        private static void RunPartMutation()
        {
            PflTestKit.Begin("B1 运行时 part 集合：Add/Remove/Move", "多part");
            SpacePart Make(int id, double x, float mass)
            {
                var p = new SpacePart
                {
                    partId = id,
                    defName = "PF_ShipPart_Cube",
                    positionShip = new DVector3(x, 0, 0),
                    boundsSizeShip = new DVector3(2, 2, 2),
                    hp = 100f,
                };
                p.AddComponent(new HullMassComponent { mass = mass });
                return p;
            }

            var e = new SpaceEntity(9100, 0);
            e.SetParts(new List<SpacePart> { Make(1, 0, 100) });

            var added = Make(0, 10, 50);
            bool okAdd = e.AddPart(added);
            PflTestKit.Check(PflTestKit.Kind.Invariant, okAdd && added.partId == 2,
                "AddPart 自动分配 id=2", $"id={added.partId}");
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(e.TotalMass - 150f) < 1e-3,
                "质量 = Σ parts", e.TotalMass.ToString("F0", CultureInfo.InvariantCulture));

            e.GetAabbSystem(out _, out DVector3 max0);
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(max0.x - 11.0) < 1e-6,
                "新增 part 扩包围盒（max.x=11）", max0.x.ToString("F1", CultureInfo.InvariantCulture));

            e.MovePart(2, new DVector3(20, 0, 0));
            e.GetAabbSystem(out _, out DVector3 max1);
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(max1.x - 21.0) < 1e-6,
                "MovePart 失效 AABB 缓存（max.x=21）", max1.x.ToString("F1", CultureInfo.InvariantCulture));

            bool okRemove = e.RemovePart(2);
            PflTestKit.Check(PflTestKit.Kind.Invariant, okRemove && e.Parts.Count == 1,
                "RemovePart 移除干净", $"parts={e.Parts.Count}");
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(e.TotalMass - 100f) < 1e-3,
                "移除后质量回原值", e.TotalMass.ToString("F0", CultureInfo.InvariantCulture));
            PflTestKit.End();
        }

        /// <summary>批 5 余下：目录是推进的唯一出处（ve/ṁ/罐容/工质都取自 `ShipPartDef`）。</summary>
        private static void RunPropulsionResolution()
        {
            PflTestKit.Begin("批 5 推进：目录=唯一出处", "多part");
            var spec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "PF_ShipPart_Thruster_Debug", category = "thruster", quantity = 1 },
                    new() { defName = "PF_ShipPart_Tank_Debug", category = "tank", quantity = 2 },
                }
            };

            var s = PartPropulsionResolver.Resolve(spec);
            PflTestKit.Check(PflTestKit.Kind.Invariant, s.catalogThrusters == 1 && s.catalogTanks == 2,
                "两件都由目录供数", $"catalog={s.catalogThrusters}/{s.catalogTanks}");
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(s.massFlow - 10f) < 1e-3,
                "推进器 ṁ=10 kg/s（目录值）", s.massFlow.ToString("F2", CultureInfo.InvariantCulture));
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(s.exhaustVelocity - 3000f) < 1e-3,
                "ve=3000 m/s（目录值）", s.exhaustVelocity.ToString("F1", CultureInfo.InvariantCulture));
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(s.capacity - 2000f) < 1e-3,
                "储罐容量=2×1000 kg（目录值）", s.capacity.ToString("F0", CultureInfo.InvariantCulture));
            PflTestKit.Check(PflTestKit.Kind.Invariant, s.propellantDefName == "PF_Propellant_Chemical",
                "工质取自目录", s.propellantDefName);
            PflTestKit.End();
        }

        /// <summary>批 0：多件惯量锚点 + 事件惰性（重读不重算）。</summary>
        private static SpaceEntityGroup? RunInertia()
        {
            PflTestKit.Begin("批 0 惯量：多件合成 + 事件惰性", "多part");
            var group = DebugShipSpawner.SpawnMultiPartDebugShip();
            if (group == null)
            {
                PflTestKit.Unchecked("多part调试船", "SpawnMultiPartDebugShip 返回 null");
                PflTestKit.End();
                return null;
            }

            var r = group.InertiaResult;
            PflTestKit.Check(PflTestKit.Kind.Invariant, r.HasData, "群级惯量有数据");
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(r.p1 - 583298.47570393) < 0.01, "I₁=583298.48", r.p1.ToString("F2"));
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(r.p2 - 528211.97490608) < 0.01, "I₂=528211.97", r.p2.ToString("F2"));
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(r.p3 - 77659.54938999) < 0.01, "I₃=77659.55", r.p3.ToString("F2"));
            PflTestKit.Check(PflTestKit.Kind.Value,
                Math.Abs(r.comX - 1.30) < 1e-6 && Math.Abs(r.comY - 0.10) < 1e-6 && Math.Abs(r.comZ + 1.40) < 1e-6,
                "质心=(1.30,0.10,−1.40)", $"{r.comX:F3},{r.comY:F3},{r.comZ:F3}");
            PflTestKit.Check(PflTestKit.Kind.Invariant, r.type == InertiaTensor.InertiaType.Asymmetric, "分类=一般（非对称）");

            int count = SpaceEntityGroup.InertiaRecomputeCount;
            _ = group.InertiaResult;
            _ = group.InertiaResult;
            PflTestKit.Check(PflTestKit.Kind.Invariant,
                SpaceEntityGroup.InertiaRecomputeCount == count, "静止重读不重算（计数不变）",
                $"{count} → {SpaceEntityGroup.InertiaRecomputeCount}");
            PflTestKit.End();
            return group;
        }

        /// <summary>批 3：逐 part 最近命中（最近件 / 缝隙 / 已毁跳过）。</summary>
        private static void RunHitSelection()
        {
            PflTestKit.Begin("批 3 命中：逐 part 最近件", "多part");

            SpacePart Box(int id, double y, double size) => new()
            {
                partId = id,
                defName = "PF_ShipPart_Cube",
                positionShip = new DVector3(0, y, 0),
                boundsSizeShip = new DVector3(size, size, size),
                hp = 100f,
            };

            var entity = new SpaceEntity(9001, 0);
            var first = Box(1, 0.0, 10.0);
            entity.SetParts(new List<SpacePart> { first, Box(2, 20.0, 10.0) });

            bool hit = CombatManager.TryPickHitPart(entity, new DVector3(0, -20, 0), new DVector3(0, 1, 0), 60.0,
                out int idx, out var part, out double t);
            PflTestKit.Check(PflTestKit.Kind.Invariant,
                hit && idx == 0 && part.partId == 1 && Math.Abs(t - 0.25) < 1e-9,
                "最近件赢（Part[0]，t=0.25）", $"hit={hit} idx={idx} t={t:F3}");

            var gapEntity = new SpaceEntity(9002, 0);
            gapEntity.SetParts(new List<SpacePart> { Box(1, -20.0, 4.0), Box(2, 20.0, 4.0) });
            bool gapHit = CombatManager.TryPickHitPart(gapEntity, new DVector3(0, -10, 0), new DVector3(0, 1, 0), 20.0,
                out _, out _, out _);
            PflTestKit.Check(PflTestKit.Kind.Invariant, !gapHit, "零件缝隙穿过=不命中");

            first.isDestroyed = true;
            bool secondHit = CombatManager.TryPickHitPart(entity, new DVector3(0, -20, 0), new DVector3(0, 1, 0), 60.0,
                out idx, out part, out _);
            PflTestKit.Check(PflTestKit.Kind.Invariant,
                secondHit && idx == 0 && part.partId == 1, "已毁件仍可命中（B5 残骸）⇒ 命中 Part[0]", $"idx={idx}");

            PflTestKit.CheckUnchecked("PFView 盒体可见性（含损毁件仍画）",
                "视觉项：一键验收只测数据/几何，不替代人眼");
            PflTestKit.End();
        }

        /// <summary>B5：损伤件乙语义（D2）——损毁件仍在/有质量/参与命中；功能/容量出局；储罐漏光；关键件。</summary>
        private static void RunDamagedParts(SpaceGameComp sg, SpaceEntityGroup? inertiaGroup)
        {
            PflTestKit.Begin("B5 损伤件：残骸有质量/仍可命中/功能出局", "多part");

            SpacePart MakePart(int id, DVector3 pos, DVector3 size, float mass)
            {
                var p = new SpacePart
                {
                    partId = id,
                    defName = "PF_ShipPart_Cube",
                    positionShip = pos,
                    boundsSizeShip = size,
                    hp = 100f,
                };
                p.AddComponent(new HullMassComponent { mass = mass });
                return p;
            }

            var e = new SpaceEntity(sg.AllocEntityId(), 0);
            var hull = MakePart(1, DVector3.zero, new DVector3(4, 4, 4), 1000f);
            var side = MakePart(2, new DVector3(10, 0, 0), new DVector3(2, 2, 2), 500f);
            e.SetParts(new List<SpacePart> { hull, side });

            double massBefore = e.TotalMass;
            double boundsBefore = e.BoundsSize.x;
            PflTestKit.Check(PflTestKit.Kind.Invariant, e.HasIntactKeyPart, "关键件=首件（无挂图件兜底）");
            e.MarkPartDestroyed(side.partId);
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(e.TotalMass - massBefore) < 0.5,
                "损毁件质量仍计入", $"{massBefore:F0} → {e.TotalMass:F0}");
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(e.BoundsSize.x - boundsBefore) < 1e-9,
                "损毁件仍计入包围盒", $"{boundsBefore:F1} → {e.BoundsSize.x:F1}");
            PflTestKit.Check(PflTestKit.Kind.Invariant, e.HasIntactKeyPart, "非关键件损毁不判失能");

            bool hitDestroyed = CombatManager.TryPickHitPart(e, new DVector3(10, -20, 0), new DVector3(0, 1, 0), 60.0,
                out _, out var hitPart, out _);
            PflTestKit.Check(PflTestKit.Kind.Invariant, hitDestroyed && hitPart.partId == side.partId,
                "损毁残骸仍可命中（D-P7）", $"part={hitPart?.partId}");

            e.MarkPartDestroyed(hull.partId);
            PflTestKit.Check(PflTestKit.Kind.Invariant, !e.HasIntactKeyPart, "关键件损毁 ⇒ 失能（不解体）");

            // 功能出局：损毁的传感器件不再贡献探测距离
            var sensorEntity = new SpaceEntity(sg.AllocEntityId(), 0);
            var sensorHull = MakePart(1, DVector3.zero, new DVector3(4, 4, 4), 1000f);
            var sensorPart = MakePart(2, new DVector3(0, 0, 6), new DVector3(2, 2, 2), 100f);
            sensorPart.AddComponent(new SensorArrayComponentData { passiveRangeMeters = 1_000_000f });
            sensorEntity.SetParts(new List<SpacePart> { sensorHull, sensorPart });
            double rangeBefore = sensorEntity.AggregatedStats.sensorRange;
            sensorEntity.MarkPartDestroyed(sensorPart.partId);
            PflTestKit.Check(PflTestKit.Kind.Value,
                rangeBefore > 0.0 && Math.Abs(sensorEntity.AggregatedStats.sensorRange) < 1e-6,
                "损毁件功能出局（传感器）", $"{rangeBefore:F0} → {sensorEntity.AggregatedStats.sensorRange:F0}");

            // 储罐损毁：漏光（池 −300 kg）+ 容量出局（500 → 0）
            PropellantDef prop = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Chemical");
            if (prop == null)
            {
                PflTestKit.CheckUnchecked("B5 储罐漏光", "缺 PF_Propellant_Chemical");
                PflTestKit.End();
                return;
            }

            var tankEntity = new SpaceEntity(sg.AllocEntityId(), 0);
            var tankHull = MakePart(1, DVector3.zero, new DVector3(4, 4, 4), 1000f);
            var tankPart = MakePart(2, new DVector3(0, 0, 6), new DVector3(2, 2, 2), 200f);
            tankPart.AddComponent(new PropellantTankComponentData
            {
                propellantDefName = prop.defName,
                capacityKg = 500f,
                massKg = 0f
            });
            tankEntity.SetParts(new List<SpacePart> { tankHull, tankPart });
            tankEntity.AddPropulsion(new PropulsionData
            {
                propellant = prop,
                ve = 3000f,
                massFlow = 10f,
                mass = 300f,
                capacity = 500f
            });

            var tankComp = tankPart.GetComponent<PropellantTankComponentData>()!;
            PflTestKit.Check(PflTestKit.Kind.Value,
                Math.Abs(tankEntity.GetPropellantMass(prop) - 300.0) < 1e-6
                && Math.Abs(tankComp.massKg - 300.0) < 1e-6,
                "漏光前：池/镜像=300 kg", $"{tankEntity.GetPropellantMass(prop):F1}/{tankComp.massKg:F1}");

            tankEntity.MarkPartDestroyed(tankPart.partId);
            PflTestKit.Check(PflTestKit.Kind.Value,
                Math.Abs(tankEntity.GetPropellantMass(prop)) < 1e-6 && Math.Abs(tankComp.massKg) < 1e-6,
                "储罐损毁漏光：池清零 + 镜像清零",
                $"{tankEntity.GetPropellantMass(prop):F1}/{tankComp.massKg:F1}");
            PflTestKit.Check(PflTestKit.Kind.Value,
                Math.Abs(tankEntity.TotalPropellantCapacity) < 1e-6
                && Math.Abs(tankEntity.GetTankCapacitySum(prop.defName)) < 1e-6,
                "储罐损毁容量出局（池容量/件容量=0）",
                $"{tankEntity.TotalPropellantCapacity:F0}/{tankEntity.GetTankCapacitySum(prop.defName):F0}");

            // 群级惯量同样计入残骸：毁掉偏心件后总质量/COM 不变（惯量随残骸保留）。
            if (inertiaGroup != null)
            {
                var r0 = inertiaGroup.InertiaResult;
                SpaceEntity? dbgEntity = null;
                foreach (int eid in inertiaGroup.GetEntityIds())
                {
                    dbgEntity = sg.GetEntityById(eid);
                    if (dbgEntity != null) break;
                }
                if (dbgEntity != null)
                {
                    dbgEntity.MarkPartDestroyed(2);   // 偏心 800 kg 侧件（非关键）
                    var r1 = inertiaGroup.InertiaResult;
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        r1.HasData && Math.Abs(r1.mass - r0.mass) < 1e-6
                        && Math.Abs(r1.comX - r0.comX) < 1e-6 && Math.Abs(r1.comZ - r0.comZ) < 1e-6,
                        "群级惯量：残骸仍计质量/COM",
                        $"m {r0.mass:F0}→{r1.mass:F0} comX {r0.comX:F3}→{r1.comX:F3}");
                }
            }

            PflTestKit.End();
        }

        /// <summary>B6：多舱室/多图——两图各自挂 part、坐标按 part、销毁一张主图顺延。</summary>
        private static void RunMultiMap(SpaceGameComp sg)
        {
            PflTestKit.Begin("B6 多舱室：两图 / 各自 part / 主图顺延", "多part");
            var group = DebugShipSpawner.SpawnDualCabinDebugShip();
            if (group == null)
            {
                PflTestKit.Unchecked("双舱室调试船", "SpawnDualCabinDebugShip 返回 null");
                PflTestKit.End();
                return;
            }

            SpaceEntity? entity = null;
            foreach (int eid in group.GetEntityIds())
            {
                entity = sg.GetEntityById(eid);
                if (entity != null) break;
            }
            if (entity == null)
            {
                PflTestKit.Unchecked("双舱室实体", "group 无实体");
                PflTestKit.End();
                return;
            }

            var ids = entity.GetMapIds();
            PflTestKit.Check(PflTestKit.Kind.Invariant, ids.Count == 2,
                "实体挂 2 张图", $"maps={ids.Count}");
            if (ids.Count != 2)
            {
                PflTestKit.End();
                return;
            }
            PflTestKit.Check(PflTestKit.Kind.Invariant, ids.Contains(entity.MapId),
                "MapId 是其中一张（主图）", $"MapId={entity.MapId}");

            int map1Id = ids[0], map2Id = ids[1];
            var part1 = entity.GetPartForMap(map1Id);
            var part2 = entity.GetPartForMap(map2Id);
            var mc1 = entity.GetMapComp(map1Id);
            var mc2 = entity.GetMapComp(map2Id);
            PflTestKit.Check(PflTestKit.Kind.Invariant,
                part1 != null && part2 != null && part1.partId != part2.partId
                && mc1 != null && mc2 != null
                && mc1.ownerPartId == part1.partId && mc2.ownerPartId == part2.partId,
                "每图 ownerPartId = 各自 part",
                $"P{mc1?.ownerPartId}/P{mc2?.ownerPartId}");

            // 坐标：第二张图的中心 → 第二件舱盒位置；反算回中心格
            if (mc2 != null && part2 != null)
            {
                var map2 = SpaceMapGenerator.FindPocketMap(map2Id);
                if (map2 != null)
                {
                    Vector3 centerShip = MapCoordMapper.MapCellToShip(map2, map2.Center);
                    IntVec3 back = MapCoordMapper.ShipToMapCell(map2,
                        new Vector3((float)part2.positionShip.x, (float)part2.positionShip.y, (float)part2.positionShip.z));
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        Math.Abs(centerShip.x - part2.positionShip.x) < 0.01
                        && Math.Abs(centerShip.y - part2.positionShip.y) < 0.01
                        && Math.Abs(centerShip.z - part2.positionShip.z) < 0.01
                        && back == map2.Center,
                        "地图坐标按挂图 part：中心↔part 位置互逆",
                        $"中心=({centerShip.x:F1},{centerShip.y:F1},{centerShip.z:F1}) back=({back.x},{back.z})");
                }

                // 质量各写回自己 part：结构质量（空图 1kg）→ 舱盒 HullMass
                var hull1 = part1?.GetComponent<HullMassComponent>();
                var hull2 = part2.GetComponent<HullMassComponent>();
                double e1 = Math.Max(mc1?.StructureMassKg ?? 0.0, 1.0);
                double e2 = Math.Max(mc2.StructureMassKg, 1.0);
                PflTestKit.Check(PflTestKit.Kind.Value,
                    hull1 != null && hull2 != null
                    && Math.Abs(hull1.mass - e1) < 0.5 && Math.Abs(hull2.mass - e2) < 0.5,
                    "每图结构质量写回各自 part",
                    $"{hull1?.mass ?? 0:F0}/{e1:F0} · {hull2?.mass ?? 0:F0}/{e2:F0}");
                // B6：调试件一开一封；封闭件质量 = 结构 + 100kg 占位，开放件不加
                PflTestKit.Check(PflTestKit.Kind.Invariant,
                    part1 != null && !part1.isSealed && part2.isSealed,
                    "封闭/非封闭按件（调试件一开一封）",
                    $"P1 sealed={part1?.isSealed} P2 sealed={part2.isSealed}");
                PflTestKit.Check(PflTestKit.Kind.Value,
                    part1 != null && Math.Abs(part1.Mass - e1) < 0.5
                    && Math.Abs(part2.Mass - (e2 + SpacePart.SealedPlaceholderMassKg)) < 0.5,
                    "封闭件 +100kg 占位（开放件不加）",
                    $"P1 {part1?.Mass:F0}≈{e1:F0} · P2 {part2.Mass:F0}≈{e2 + SpacePart.SealedPlaceholderMassKg:F0}");
            }

            // B6：所有图的工质都作数；推进开关只切断该图的网络，不抹掉其工质质量
            if (mc1 != null && mc2 != null)
            {
                PropellantDef chem = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Chemical");
                if (chem != null)
                {
                    mc1.RegisterEngine(chem, 3000f, 10f);
                    mc2.RegisterEngine(chem, 3000f, 10f);
                    mc1.AddPropellantMass(chem, 100.0);
                    mc2.AddPropellantMass(chem, 50.0);

                    var src = sg.GetGroupPropulsionSource(group);
                    float flow = 0f;
                    if (src != null)
                        for (int i = 0; i < src.Networks.Count; i++) flow += src.Networks[i].MassFlow;
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        src != null && Math.Abs(src.TotalPropellantMass - 150.0) < 1e-6
                        && Math.Abs(flow - 20f) < 1e-3f,
                        "多图聚合：两图工质/推力都作数",
                        $"fuel={src?.TotalPropellantMass:F0} flow={flow:F0}");

                    mc2.SetSystemEnabled(MapSystemFlags.Propulsion, false);
                    var srcOff = sg.GetGroupPropulsionSource(group);
                    float flowOff = 0f;
                    if (srcOff != null)
                        for (int i = 0; i < srcOff.Networks.Count; i++) flowOff += srcOff.Networks[i].MassFlow;
                    PflTestKit.Check(PflTestKit.Kind.Value,
                        srcOff != null && Math.Abs(srcOff.TotalPropellantMass - 150.0) < 1e-6
                        && Math.Abs(flowOff - 10f) < 1e-3f,
                        "切断图2推进：推力减半、工质仍作数",
                        $"fuel={srcOff?.TotalPropellantMass:F0} flow={flowOff:F0}");
                    mc2.SetSystemEnabled(MapSystemFlags.Propulsion, true);
                }
                else
                {
                    PflTestKit.CheckUnchecked("多图聚合", "缺 PF_Propellant_Chemical");
                }
            }

            // 销毁第二张图（移除第二件舱盒）：第一张图必须继续可用，主图不能指向死图
            if (part2 != null)
            {
                int deadMapId = map2Id;
                bool removed = entity.RemovePart(part2.partId, PartRemovalPolicy.DestroyMap);
                PflTestKit.Check(PflTestKit.Kind.Invariant,
                    removed && entity.GetMapIds().Count == 1
                    && entity.MapId == map1Id && entity.HasMap
                    && SpaceMapGenerator.FindPocketMap(deadMapId) == null,
                    "销毁一张图 ⇒ 另一张仍可用、主图顺延",
                    $"removed={removed} maps={entity.GetMapIds().Count} MapId={entity.MapId} deadGone={SpaceMapGenerator.FindPocketMap(deadMapId) == null}");
            }

            PflTestKit.End();
        }

        /// <summary>B7：256 件整船——装配/包围盒/群级惯量/图链的规模验收。</summary>
        private static void Run256Scale(SpaceGameComp sg)
        {
            PflTestKit.Begin("B7 256 件：装配 / 包围盒 / 惯量 / 图链", "多part");
            var group = DebugShipSpawner.Spawn256PartDebugShip();
            if (group == null)
            {
                PflTestKit.Unchecked("256 件基准船", "Spawn256PartDebugShip 返回 null");
                PflTestKit.End();
                return;
            }

            SpaceEntity? entity = null;
            foreach (int eid in group.GetEntityIds())
            {
                entity = sg.GetEntityById(eid);
                if (entity != null) break;
            }
            if (entity == null)
            {
                PflTestKit.Unchecked("256 件实体", "group 无实体");
                PflTestKit.End();
                return;
            }

            PflTestKit.Check(PflTestKit.Kind.Invariant, entity.Parts.Count == DesignPartAssembler.MaxTotalParts,
                "件数 = 256（含舱盒）", entity.Parts.Count.ToString());
            PflTestKit.Check(PflTestKit.Kind.Value, entity.AggregatedStats.mass > 0f,
                "实体总质量 > 0", entity.AggregatedStats.mass.ToString("F0"));
            PflTestKit.Check(PflTestKit.Kind.Value,
                entity.BoundsSize.x > 0 && entity.BoundsSize.y > 500 && entity.BoundsSize.z > 0,
                "包围盒覆盖 255 层堆叠",
                $"{entity.BoundsSize.x:F0}×{entity.BoundsSize.y:F0}×{entity.BoundsSize.z:F0}");

            var r = group.InertiaResult;
            PflTestKit.Check(PflTestKit.Kind.Invariant, r.HasData && r.mass > 0.0,
                "群级惯量有数据（256 件合成）", $"mass={r.mass:F0}");
            PflTestKit.Check(PflTestKit.Kind.Invariant,
                entity.GetMapIds().Count == 1 && entity.Parts[0].linkedMapId == entity.MapId,
                "图链保持（舱盒挂图）", $"maps={entity.GetMapIds().Count}");
            PflTestKit.End();
        }

        /// <summary>步骤 1：取消目标消旋 / 断开引导撤回节点（纯群级语义，无需铺场）。</summary>

        private static void RunCancelSemantics()
        {
            PflTestKit.Begin("取消目标/断开引导的语义", "多part");
            var g = new SpaceEntityGroup(1, 0);
            g.pendingAlignDir = new DVector3(0, 0, 1);
            g.angularVelocity = new DVector3(0.5, 0, 0);
            g.ClearManeuvers();
            PflTestKit.Check(PflTestKit.Kind.Invariant,
                g.angularVelocity.sqrMagnitude < 1e-24, "清自动目标 ⇒ 消旋",
                g.angularVelocity.magnitude.ToString("E2"));

            g.angularVelocity = new DVector3(0.5, 0, 0);
            g.pendingAlignDir = DVector3.zero;
            g.ClearManeuvers();
            PflTestKit.Check(PflTestKit.Kind.Value,
                Math.Abs(g.angularVelocity.x - 0.5) < 1e-12, "无自动目标时自由自转保留",
                g.angularVelocity.x.ToString("F3"));

            var h = new SpaceEntityGroup(2, 0);
            h.RegisterRendezvousGuidance(3, 1000.0, prograde: true, useNetworks: null);
            h.plannedManeuvers.Add(new SpaceEntityGroup.PlannedManeuver
            { active = true, executeTick = 100, owner = PlanOwner.Guidance, ladderEpoch = 0 });
            h.plannedManeuvers.Add(new SpaceEntityGroup.PlannedManeuver
            { active = true, executeTick = 200, owner = PlanOwner.Manual, ladderEpoch = 0 });
            h.ClearRendezvousGuidance();
            PflTestKit.Check(PflTestKit.Kind.Invariant,
                h.plannedManeuvers.Count == 1 && h.plannedManeuvers[0].owner == PlanOwner.Manual,
                "断开引导撤回旧阶梯节点、保留手动条目", h.plannedManeuvers.Count.ToString());
            PflTestKit.End();
        }

        /// <summary>批 3 真碰撞的几何/惯量响应（最小穿透轴、part 世界 AABB、Δω=I⁻¹h）。</summary>
        private static void RunCollisionMath()
        {
            PflTestKit.Begin("批 3 碰撞几何：最小穿透轴 / part AABB / 角冲量响应", "多part");

            var n = CombatManager.MinPenetrationAxis(1.0, 2.0, 3.0,
                DVector3.zero, new DVector3(0.5, 0, 0), out double depth);
            PflTestKit.Check(PflTestKit.Kind.Value,
                Math.Abs(depth - 1.0) < 1e-12 && n.x > 0.99, "最小穿透轴 = X（深度 1）",
                $"n=({n.x:F2},{n.y:F2},{n.z:F2}) depth={depth:F3}");

            var e = new SpaceEntity(9003, 0);
            e.SetParts(new List<SpacePart>
            {
                new()
                {
                    partId = 1, defName = "PF_ShipPart_Cube",
                    positionShip = new DVector3(0, 10, 0), boundsSizeShip = new DVector3(4, 6, 8), hp = 100f,
                },
            });
            bool partAabbOk = e.GetPartAabbSystem(0, out DVector3 min, out DVector3 max)
                && Math.Abs(min.y - 7.0) < 1e-9 && Math.Abs(max.y - 13.0) < 1e-9;
            PflTestKit.Check(PflTestKit.Kind.Value, partAabbOk, "part 世界 AABB（y 7~13）",
                partAabbOk ? $"({min.x:F1},{min.y:F1},{min.z:F1})-({max.x:F1},{max.y:F1},{max.z:F1})" : "fail");

            var r = InertiaTensor.Compose(new List<InertiaTensor.PartContribution>
            {
                new() { mass = 1.0, cxx = 5.0, cyy = 5.0, czz = 5.0 },
            });
            var dw = InertiaTensor.InverseInertiaMultiply(r, 1.0, 2.0, 3.0);
            PflTestKit.Check(PflTestKit.Kind.Value,
                Math.Abs(dw.x - 0.2) < 1e-12 && Math.Abs(dw.y - 0.4) < 1e-12 && Math.Abs(dw.z - 0.6) < 1e-12,
                "等惯 Δω=h/5", $"({dw.x:F2},{dw.y:F2},{dw.z:F2})");

            // 2026-10-17：碰撞后两侧转向符号（用户报「同向转」）。头对头（接触点在中心连线、
            // 冲量沿连线）必须 Δω=0；偏置接触两侧 Δω 反向（点积 < 0）。
            DVector3 dwHeadA = CombatManager.ComputeCollisionDeltaOmega(r, DQuaternion.identity,
                DVector3.zero, new DVector3(3, 0, 0), new DVector3(-10, 0, 0));
            PflTestKit.Check(PflTestKit.Kind.Invariant, dwHeadA.magnitude < 1e-12,
                "头对头接触 Δω=0（不应自转）", $"|Δω|={dwHeadA.magnitude:E2}");
            DVector3 dwOffA = CombatManager.ComputeCollisionDeltaOmega(r, DQuaternion.identity,
                DVector3.zero, new DVector3(3, 1, 0), new DVector3(-10, 0, 0));
            DVector3 dwOffB = CombatManager.ComputeCollisionDeltaOmega(r, DQuaternion.identity,
                new DVector3(36, 0, 0), new DVector3(3, 1, 0), new DVector3(10, 0, 0));
            PflTestKit.Check(PflTestKit.Kind.Invariant,
                dwOffA.z > 0.0 && dwOffB.z < 0.0 && DVector3.Dot(dwOffA, dwOffB) < 0.0,
                "偏置接触两侧 Δω 反向（点积<0）",
                $"ΔωA=({dwOffA.y:F2},{dwOffA.z:F2}) ΔωB=({dwOffB.y:F2},{dwOffB.z:F2}) dot={DVector3.Dot(dwOffA, dwOffB):F3}");
            PflTestKit.End();
        }

        /// <summary>批 5：图链（舱盒挂图、追加件不挂、地图 owner 正确、质量=Σ零件）。</summary>
        private static void RunMapOnPart(SpaceGameComp spaceGame)
        {
            PflTestKit.Begin("批 5 图链：舱盒挂图 + 追加件不挂", "多part");
            var group = DebugShipSpawner.SpawnMultipartStationDebug();
            if (group == null)
            {
                PflTestKit.Unchecked("多part地图站", "SpawnMultipartStationDebug 返回 null");
                PflTestKit.End();
                return;
            }

            SpaceEntity? entity = null;
            foreach (int eid in group.GetEntityIds())
            {
                entity = spaceGame.GetEntityById(eid);
                break;
            }
            PflTestKit.Check(PflTestKit.Kind.Invariant, entity != null && entity.Parts.Count == 5, "站=5 件 part",
                entity != null ? entity.Parts.Count.ToString() : "entity=null");
            if (entity != null)
            {
                PflTestKit.Check(PflTestKit.Kind.Invariant,
                    entity.MapId >= 0 && entity.Parts[0].linkedMapId == entity.MapId,
                    "舱盒 linkedMap == entity.MapId", $"map={entity.MapId} cabinLink={entity.Parts[0].linkedMapId}");
                bool appendedUnlinked = true;
                double total = 0.0;
                for (int i = 0; i < entity.Parts.Count; i++)
                {
                    total += entity.Parts[i].Mass;
                    if (i > 0 && entity.Parts[i].linkedMapId >= 0) appendedUnlinked = false;
                }
                PflTestKit.Check(PflTestKit.Kind.Invariant, appendedUnlinked, "追加件不挂图");

                var map = SpaceMapGenerator.FindPocketMap(entity.MapId);
                var mapComp = map?.GetComponent<SpaceMapComp>();
                PflTestKit.Check(PflTestKit.Kind.Invariant,
                    mapComp != null && mapComp.ownerEntityId == entity.EntityId,
                    "地图 ownerEntityId == 实体", mapComp != null ? mapComp.ownerEntityId.ToString() : "map=null");

                if (map != null && mapComp != null)
                {
                    PflTestKit.Check(PflTestKit.Kind.Value, mapComp.StructureMassKg > 0.0,
                        "地图结构质量 > 0（站内建筑/屋顶已计入）",
                        mapComp.StructureMassKg.ToString("F0", CultureInfo.InvariantCulture));

                    double cabinMass = Math.Max(mapComp.StructureMassKg, 1.0)
                        + (entity.Parts[0].isSealed ? SpacePart.SealedPlaceholderMassKg : 0.0);
                    double expected = cabinMass + 500.0 + 2 * 200.0 + 100.0;
                    PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(total - expected) < 0.5,
                        "质量=Σ零件（舱盒=地图结构质量+封闭占位）", $"总={total:F0} 期望={expected:F0}");

                    RunMapMassChecks(map, mapComp);
                    RunTankFuelChecks(group, entity, mapComp);
                }
            }
            PflTestKit.End();
        }

        /// <summary>B4a：tank/engine 组件 + 油量按容量分布到 tank、燃料质量移动 COM。</summary>
        private static void RunTankFuelChecks(SpaceEntityGroup group, SpaceEntity entity, SpaceMapComp mc)
        {
            var tankParts = new List<SpacePart>();
            SpacePart? enginePart = null;
            for (int i = 0; i < entity.Parts.Count; i++)
            {
                var p = entity.Parts[i];
                if (p == null) continue;
                if (p.GetComponent<PropellantTankComponentData>() != null) tankParts.Add(p);
                if (enginePart == null && p.GetComponent<EngineComponentData>() != null) enginePart = p;
            }
            PflTestKit.Check(PflTestKit.Kind.Invariant, tankParts.Count == 2,
                "目录 tank×2 挂 tank 组件", tankParts.Count.ToString(CultureInfo.InvariantCulture));
            PflTestKit.Check(PflTestKit.Kind.Invariant, enginePart != null,
                "推进器挂 EngineComponentData");
            var engine = enginePart?.GetComponent<EngineComponentData>();
            PflTestKit.Check(PflTestKit.Kind.Value,
                engine != null && Math.Abs(engine.exhaustVelocity - 3000f) < 1e-3
                && Math.Abs(engine.massFlowRate - 10f) < 1e-3,
                "引擎组件参数=目录（ve=3000 / ṁ=10）",
                engine != null
                    ? engine.exhaustVelocity.ToString("F0", CultureInfo.InvariantCulture) + "/"
                      + engine.massFlowRate.ToString("F0", CultureInfo.InvariantCulture)
                    : "null");

            double capSum = 0.0;
            for (int i = 0; i < tankParts.Count; i++)
                capSum += tankParts[i].GetComponent<PropellantTankComponentData>()!.capacityKg;
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(capSum - 2000.0) < 1e-6,
                "tank 容量Σ=2000（与推进解析一致）", capSum.ToString("F0", CultureInfo.InvariantCulture));

            DVector3 torque = group.GetEngineThrustTorque(new DVector3(1, 0, 0), 1000.0);
            PflTestKit.Check(PflTestKit.Kind.Value, torque.magnitude > 1.0,
                "单引擎偏心 ⇒ 力矩非零", torque.magnitude.ToString("F1", CultureInfo.InvariantCulture));

            PropellantDef chemical = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Chemical");
            if (chemical == null || tankParts.Count == 0)
            {
                PflTestKit.CheckUnchecked("B4 油量分布", "缺工质 Def 或 tank 组件");
                return;
            }

            double comBefore = group.InertiaResult.comY;
            mc.AddPropellantMass(chemical, 1000.0);
            double sum = 0.0;
            for (int i = 0; i < tankParts.Count; i++)
                sum += tankParts[i].GetComponent<PropellantTankComponentData>()!.massKg;
            PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(sum - 1000.0) < 1e-6,
                "Σtank = 池 1000 kg", sum.ToString("F1", CultureInfo.InvariantCulture));

            double comAfter = group.InertiaResult.comY;
            PflTestKit.Check(PflTestKit.Kind.Value, comAfter < comBefore - 0.5,
                "燃料质量让 COM 向 tank 端移动",
                "comY " + comBefore.ToString("F2", CultureInfo.InvariantCulture) + " → "
                + comAfter.ToString("F2", CultureInfo.InvariantCulture));

            mc.AddPropellantMass(chemical, -1000.0);
            sum = 0.0;
            for (int i = 0; i < tankParts.Count; i++)
                sum += tankParts[i].GetComponent<PropellantTankComponentData>()!.massKg;
            PflTestKit.Check(PflTestKit.Kind.Value,
                Math.Abs(sum) < 1e-6 && Math.Abs(group.InertiaResult.comY - comBefore) < 1e-6,
                "抽走油量 ⇒ tank/COM 回原", "Σtank=" + sum.ToString("F1", CultureInfo.InvariantCulture));

            // B5：地图侧储罐损毁 —— 池同步扣掉该罐镜像、镜像清零、件容量出局
            if (tankParts.Count >= 2)
            {
                mc.AddPropellantMass(chemical, 1000.0);
                var doomed = tankParts[0];
                var doomedTank = doomed.GetComponent<PropellantTankComponentData>()!;
                double mirrored = doomedTank.massKg;
                double capSumBefore = entity.GetTankCapacitySum(chemical.defName);
                entity.MarkPartDestroyed(doomed.partId);
                PflTestKit.Check(PflTestKit.Kind.Value,
                    Math.Abs(mc.GetPropellantMass(chemical) - (1000.0 - mirrored)) < 1e-6
                    && Math.Abs(doomedTank.massKg) < 1e-6
                    && Math.Abs(entity.GetTankCapacitySum(chemical.defName) - (capSumBefore - doomedTank.capacityKg)) < 1e-6,
                    "地图储罐损毁：池扣镜像 / 镜像清零 / 容量出局",
                    $"池={mc.GetPropellantMass(chemical):F0} 镜像={doomedTank.massKg:F0} 件容量Σ={entity.GetTankCapacitySum(chemical.defName):F0}");
            }
        }

        /// <summary>Map 质量维护：建筑生成/拆除、屋顶覆盖/拆除、工质加注/消耗（2026-10-17 用户裁定）。</summary>
        private static void RunMapMassChecks(Map map, SpaceMapComp mc)
        {
            var s0 = mc.MapMassSnapshot();
            PflTestKit.Check(PflTestKit.Kind.Value,
                Math.Abs(s0.total - (s0.building + s0.roofCells * SpaceMapComp.RoofCellMassKg + s0.propellant)) < 1e-6,
                "Map 质量=建筑+屋顶+工质",
                $"建筑{s0.building:F0} + 屋顶{s0.roofCells}格 + 工质{s0.propellant:F0} = {s0.total:F0} kg");

            // ── 建筑生成/拆除 ──
            IntVec3 cell = FindFreeCell(map);
            if (cell.IsValid)
            {
                Thing wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel);
                GenSpawn.Spawn(wall, cell, map, Rot4.North);
                var s1 = mc.MapMassSnapshot();
                float rawWallMass = wall.GetStatValue(StatDefOf.Mass);
                double wallMass = rawWallMass > 0f && !float.IsNaN(rawWallMass) ? rawWallMass : 1.0;
                PflTestKit.Check(PflTestKit.Kind.Value,
                    Math.Abs((s1.building - s0.building) - wallMass) < 0.01,
                    "建墙 ⇒ 建筑质量 +墙质量", $"+{s1.building - s0.building:F1} kg（墙 {wallMass:F1} kg）");

                wall.DeSpawn(DestroyMode.Vanish);
                var s2 = mc.MapMassSnapshot();
                PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(s2.building - s0.building) < 0.01,
                    "拆墙 ⇒ 建筑质量回落", $"{s1.building:F1} → {s2.building:F1} kg");
            }
            else
            {
                PflTestKit.CheckUnchecked("建筑质量事件", "找不到空格子放测试墙");
            }

            // ── 屋顶覆盖/拆除（1 kg/格）──
            RoofDef roof = DefDatabase<RoofDef>.GetNamedSilentFail("RoofConstructed")
                ?? DefDatabase<RoofDef>.GetNamedSilentFail("RoofRock");
            IntVec3 roofCell = FindUnroofedCell(map);
            if (roof != null && roofCell.IsValid)
            {
                var before = mc.MapMassSnapshot();
                map.roofGrid.SetRoof(roofCell, roof);
                var after = mc.MapMassSnapshot();
                PflTestKit.Check(PflTestKit.Kind.Value,
                    after.roofCells == before.roofCells + 1
                    && Math.Abs(after.total - before.total - SpaceMapComp.RoofCellMassKg) < 1e-6,
                    "盖屋顶 ⇒ +1 格 / +1 kg",
                    $"{before.roofCells} → {after.roofCells} 格，总 {before.total:F0} → {after.total:F0} kg");

                map.roofGrid.SetRoof(roofCell, null);
                var restored = mc.MapMassSnapshot();
                PflTestKit.Check(PflTestKit.Kind.Value,
                    restored.roofCells == before.roofCells && Math.Abs(restored.total - before.total) < 1e-6,
                    "拆屋顶 ⇒ 回落到原值", $"{restored.roofCells} 格，总 {restored.total:F0} kg");
            }
            else
            {
                PflTestKit.CheckUnchecked("屋顶质量事件", roof == null ? "缺 RoofConstructed/RoofRock" : "找不到无屋顶格子");
            }

            // ── 工质加注/消耗 ──
            PropellantDef chemical = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Chemical");
            if (chemical != null)
            {
                double total0 = mc.MapMassKg;
                mc.AddPropellantMass(chemical, 100.0);
                PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(mc.MapMassKg - total0 - 100.0) < 1e-6,
                    "加注 100 kg ⇒ Map 质量 +100", $"{total0:F0} → {mc.MapMassKg:F0} kg");
                mc.AddPropellantMass(chemical, -100.0);
                PflTestKit.Check(PflTestKit.Kind.Value, Math.Abs(mc.MapMassKg - total0) < 1e-6,
                    "消耗/抽走 100 kg ⇒ 回落", mc.MapMassKg.ToString("F0"));
            }
            else
            {
                PflTestKit.CheckUnchecked("工质质量事件", "缺 PF_Propellant_Chemical");
            }
        }

        private static IntVec3 FindFreeCell(Map map)
        {
            for (int r = 0; r <= 12; r++)
            {
                foreach (IntVec3 cell in CellRect.CenteredOn(map.Center, r).Cells)
                {
                    if (cell.InBounds(map) && map.thingGrid.ThingsListAt(cell).Count == 0)
                        return cell;
                }
            }
            return IntVec3.Invalid;
        }

        private static IntVec3 FindUnroofedCell(Map map)
        {
            for (int r = 0; r <= 12; r++)
            {
                foreach (IntVec3 cell in CellRect.CenteredOn(map.Center, r).Cells)
                {
                    if (cell.InBounds(map) && map.roofGrid.RoofAt(cell) == null)
                        return cell;
                }
            }
            return IntVec3.Invalid;
        }
    }
}
