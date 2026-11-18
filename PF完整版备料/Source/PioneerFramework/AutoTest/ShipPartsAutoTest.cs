using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Combat;
using PioneerFramework.Components;

namespace PioneerFramework.AutoTest
{
    /// <summary>部件自动测试报告。</summary>
    public class PartTestReport
    {
        public List<string> Failures = new List<string>();
        public int CaseCount;

        public bool AllPassed => Failures.Count == 0;

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[ShipPartsAutoTest] === 部件自动测试报告 ===");
            sb.AppendLine("用例总数: " + CaseCount + " | 失败: " + Failures.Count);
            for (int i = 0; i < Failures.Count; i++)
                sb.AppendLine("  " + Failures[i]);
            sb.AppendLine(AllPassed
                ? "[ShipPartsAutoTest] 全部通过 ✓"
                : "[ShipPartsAutoTest] 存在失败 ✗");
            return sb.ToString();
        }
    }

    /// <summary>
    /// 部件系统自动测试（闭环 1：组件模型 + stats 聚合 + 失效边界）。
    /// 入口：开发者模式 → Debug Actions → "Pioneer Framework" 类别。
    /// 总开关 Enabled 可在 Debug 菜单切换；禁用后 RunAllAndLog 直接返回。
    /// </summary>
    public static class ShipPartsAutoTest
    {
        /// <summary>总开关（Debug 菜单可切换）。</summary>
        public static bool Enabled = true;

        private static PartTestReport _report;

        public static PartTestReport RunAll()
        {
            _report = new PartTestReport();
            RunSuite("注册表", TestRegistry);
            RunSuite("Def 预置组件", TestDefPresets);
            RunSuite("模板二进制 round-trip", TestTemplateBinaryRoundTrip);
            RunSuite("Clone 深拷贝", TestClone);
            RunSuite("全链路(模板→运行时→聚合→失效)", TestPipelineToSpacePartsAndStats);
            RunSuite("伤害回调接线(ApplyDamage→OnDamaged/OnDestroyed)", TestDamageCallbacks);
            return _report;
        }

        /// <summary>每个套件独立容错：异常记为失败，不中断后续套件。</summary>
        private static void RunSuite(string name, Action suite)
        {
            try
            {
                suite();
            }
            catch (Exception ex)
            {
                Check(false, "套件异常: " + name + " → " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void RunAllAndLog()
        {
            if (!Enabled)
            {
                Log.Message("[ShipPartsAutoTest] 已禁用（Debug 菜单可启用）。");
                return;
            }

            var sw = Stopwatch.StartNew();
            var report = RunAll();
            sw.Stop();

            Log.Message(report.ToString());
            Log.Message(string.Format("[ShipPartsAutoTest] 耗时 {0:F0} ms", sw.Elapsed.TotalMilliseconds));

            if (report.AllPassed)
                Messages.Message("部件自动测试全部通过（" + report.CaseCount + " 用例）", MessageTypeDefOf.TaskCompletion);
            else
                Messages.Message("部件自动测试失败 " + report.Failures.Count + " 项，详见日志", MessageTypeDefOf.RejectInput);
        }

        // ════════════════════════════════════════════════
        //  断言
        // ════════════════════════════════════════════════

        private static void Check(bool condition, string name)
        {
            _report.CaseCount++;
            if (!condition)
                _report.Failures.Add("✗ " + name);
        }

        private static void CheckClose(float actual, float expected, string name, float tol = 0.01f)
        {
            Check(Math.Abs(actual - expected) <= tol,
                name + string.Format("（期望 {0:0.##}，实际 {1:0.##}）", expected, actual));
        }

        // ════════════════════════════════════════════════
        //  套件 1：组件注册表
        // ════════════════════════════════════════════════

        private static void TestRegistry()
        {
            Check(ComponentRegistry.Count >= 5,
                "注册表已注册至少 5 个框架组件（实际 " + ComponentRegistry.Count + "）");

            string[] typeIds =
            {
                "EngineComponentData", "PropellantTankComponentData", "CargoComponentData",
                "WeaponMountComponentData", "RoomComponentData"
            };
            foreach (var typeId in typeIds)
            {
                var comp = ComponentRegistry.Create(typeId);
                Check(comp != null, "注册表可实例化 " + typeId);
                Check(comp != null && comp.TypeId == typeId, typeId + " TypeId 匹配");
            }
        }

        // ════════════════════════════════════════════════
        //  套件 2：Def 预置组件
        // ════════════════════════════════════════════════

        private static void TestDefPresets()
        {
            CheckPreset("PF_ShipPart_Engine", typeof(EngineComponentData));
            CheckPreset("PF_ShipPart_PropellantTank", typeof(PropellantTankComponentData));
            CheckPreset("PF_ShipPart_Weapon", typeof(WeaponMountComponentData));
            CheckPreset("PF_ShipPart_CargoHold", typeof(CargoComponentData));
            CheckPreset("PF_ShipPart_RoomModule", typeof(RoomComponentData));
        }

        private static void CheckPreset(string defName, Type expectedCompType)
        {
            var def = ShipPartDefs.GetDef(defName);
            Check(def != null, defName + " Def 存在");
            if (def == null) return;

            Check(def.presetComponents != null && def.presetComponents.Count > 0,
                defName + " 有预置组件");
            if (def.presetComponents == null || def.presetComponents.Count == 0) return;

            Check(expectedCompType.IsInstanceOfType(def.presetComponents[0]),
                defName + " 预置组件类型为 " + expectedCompType.Name);
        }

        // ════════════════════════════════════════════════
        //  套件 3：模板二进制 v4 round-trip
        // ════════════════════════════════════════════════

        private static void TestTemplateBinaryRoundTrip()
        {
            var tmpl = new ShipPartTemplate
            {
                templateName = "auto_test",
                shapeType = ShipPartShape.Cube,
                bounds = new Vector3(2f, 2f, 3f),
                previewColor = new Color(0.5f, 0.5f, 0.5f),
                components = new List<PartComponentData>
                {
                    new EngineComponentData { exhaustVelocity = 30000f, massFlowRate = 5f, dryMass = 500f },
                    new PropellantTankComponentData { dryMass = 500f, wetMass = 5000f }
                }
            };

            ShipPartTemplate loaded;
            using (var ms = new MemoryStream())
            {
                // leaveOpen: BinaryWriter 不得关闭底层流
                using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                    tmpl.WriteBinary(w);
                ms.Position = 0;
                using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8, true))
                    loaded = ShipPartTemplate.ReadBinary(r);
            }

            Check(loaded != null, "模板 v4 二进制可读回");
            if (loaded == null) return;

            Check(loaded.shapeType == tmpl.shapeType, "模板形状 round-trip 一致");
            Check(loaded.bounds == tmpl.bounds, "模板尺寸 round-trip 一致");
            Check(loaded.components.Count == 2, "模板组件数 round-trip 一致（2）");
            if (loaded.components.Count >= 1)
            {
                var engine = loaded.components[0] as EngineComponentData;
                Check(engine != null, "组件[0] 为 Engine");
                if (engine != null)
                {
                    CheckClose(engine.exhaustVelocity, 30000f, "Engine.exhaustVelocity round-trip");
                    CheckClose(engine.massFlowRate, 5f, "Engine.massFlowRate round-trip");
                }
            }
        }

        // ════════════════════════════════════════════════
        //  套件 4：Clone 深拷贝
        // ════════════════════════════════════════════════

        private static void TestClone()
        {
            var src = new EngineComponentData { exhaustVelocity = 30000f, massFlowRate = 5f, dryMass = 500f };
            var dst = src.Clone();

            Check(dst != null, "Clone 返回非空");
            Check(dst != null && !ReferenceEquals(dst, src), "Clone 返回新实例");
            var engine = dst as EngineComponentData;
            Check(engine != null, "Clone 类型保持");
            if (engine != null)
            {
                CheckClose(engine.exhaustVelocity, src.exhaustVelocity, "Clone 保留 exhaustVelocity");
                CheckClose(engine.massFlowRate, src.massFlowRate, "Clone 保留 massFlowRate");
            }
        }

        // ════════════════════════════════════════════════
        //  套件 6：伤害回调接线（ApplyDamage → 组件回调）
        // ════════════════════════════════════════════════

        private static void TestDamageCallbacks()
        {
            var part = new SpacePart { hp = 100f };
            var probe = new DamageProbeComponent();
            part.AddComponent(probe);

            part.ApplyDamage(30f);
            Check(probe.DamagedCount == 1, "OnDamaged 回调触发");
            CheckClose(part.hp, 70f, "ApplyDamage 扣血 30");
            Check(probe.DestroyedCount == 0, "未摧毁不触发 OnDestroyed");

            part.ApplyDamage(100f);
            Check(part.isDestroyed, "hp≤0 标记摧毁");
            Check(probe.DestroyedCount == 1, "OnDestroyed 回调触发");

            part.ApplyDamage(10f);
            Check(probe.DamagedCount == 1, "已摧毁部件不再触发伤害");
        }

        // ════════════════════════════════════════════════
        //  套件 5：模板 → 运行时 → 聚合 → 失效（全链路）
        // ════════════════════════════════════════════════

        private static void TestPipelineToSpacePartsAndStats()
        {
            // 用 Def 名作为 templateName（走 Def 预置组件兜底，无需真实模板文件）
            var ship = new Ship3D();
            ship.parts.Add(new ShipPartInstance { templateName = "PF_ShipPart_Engine" });
            ship.parts.Add(new ShipPartInstance
            {
                templateName = "PF_ShipPart_PropellantTank",
                position = new Vector3(3f, 0f, 0f)
            });

            var spaceParts = ship.ToSpaceParts();
            Check(spaceParts.Count == 2, "ToSpaceParts 生成 2 个部件");
            if (spaceParts.Count < 2) return;

            // 组件拷贝（病灶 2：设计层参数进物理）
            var engineComp = spaceParts[0].GetComponent<EngineComponentData>();
            Check(engineComp != null, "Engine 部件携带 Engine 组件");
            if (engineComp != null)
                CheckClose(engineComp.exhaustVelocity, 30000f, "Engine 组件参数来自 Def 预置");

            Check(spaceParts[1].GetComponent<PropellantTankComponentData>() != null,
                "Tank 部件携带 Tank 组件");

            // 实体级聚合
            var entity = new SpaceEntity(999001);
            entity.SetParts(spaceParts);

            var stats = entity.AggregatedStats;
            // Engine: Cube(massBase=1) + dryMass(500) = 501；Tank: Cube(1) + wetMass(5000) = 5001
            CheckClose(stats.mass, 5502f, "实体质量聚合");
            CheckClose(stats.thrust, 150000f, "实体推力聚合（30000×5）");
            CheckClose(stats.propellant, 4500f, "实体推进剂聚合（5000-500）");

            // 失效边界：摧毁引擎 → 质量重算
            entity.MarkPartDestroyed(spaceParts[0].partId);
            var stats2 = entity.AggregatedStats;
            CheckClose(stats2.mass, 5001f, "摧毁引擎后质量重算");
            CheckClose(stats2.thrust, 0f, "摧毁引擎后推力归零");
            CheckClose(stats2.propellant, 4500f, "摧毁引擎后推进剂不变");
        }

        // ════════════════════════════════════════════════
        //  探针组件（测试专用，嵌套类）
        // ════════════════════════════════════════════════

        /// <summary>测试探针：记录 OnDamaged / OnDestroyed 回调触发次数。</summary>
        public class DamageProbeComponent : PartComponentData
        {
            private int damagedCount;
            private int destroyedCount;

            public int DamagedCount => damagedCount;
            public int DestroyedCount => destroyedCount;

            static DamageProbeComponent()
            {
                ComponentRegistry.Register<DamageProbeComponent>();
            }

            public override void OnDamaged(float damage) => damagedCount++;
            public override void OnDestroyed() => destroyedCount++;

            public override void ExposeData()
            {
                Scribe_Values.Look(ref damagedCount, "damagedCount", 0);
                Scribe_Values.Look(ref destroyedCount, "destroyedCount", 0);
            }

            public override void Write(BinaryWriter w)
            {
                w.Write(damagedCount);
                w.Write(destroyedCount);
            }

            public override void Read(BinaryReader r)
            {
                damagedCount = r.ReadInt32();
                destroyedCount = r.ReadInt32();
            }
        }
    }
}
