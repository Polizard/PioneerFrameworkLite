using System;
using System.Collections.Generic;
using System.IO;
using PioneerFramework;
using PioneerFramework.Components;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **§11 双血条（结构值 hp / 维护值 condition）的判据**（2026-10-22 部件功能化）。
    ///
    /// 四条设计判据（设计稿 §11.6）在这里逐条钉住：
    /// · 新件 `condition = 1` ⇒ 聚合**逐位不变**（`× 1.0` 精确，不是"差不多"）；
    /// · 效率曲线 `eff = clamp((c − 0.2) / 0.8, 0, 1)`：`0.19` 停机、`0.2` 停机、`0.6 → 0.5`、`1 → 1`；
    /// · 结构 50 ⇒ 维护上限 0.5 ⇒ 效率上限 0.375（结构伤顺带压死上限）；
    /// · 摧毁 ⇒ `condition` 归零（`ApplyDamage` 与 `MarkPartDestroyed` 两条路径口径一致）。
    /// 另加：磨损只由事件驱动、夹在 [0, 上限]；读档时坏数据（维护值高于结构上限）被夹回。
    /// </summary>
    public class PartConditionTests
    {
        private static SpacePart MakePart()
        {
            var p = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_Cube",
                boundsSizeShip = new DVector3(2, 2, 2),
            };
            p.AddComponent(new HullMassComponent { mass = 500f });
            p.AddComponent(new SensorArrayComponentData { passiveRangeMeters = 1_000_000f, ecmResist = 0.5f });
            p.AddComponent(new JammerComponentData { jamStrength = 2f });
            return p;
        }

        [Fact]
        public void NewPart_EfficiencyIsExactlyOne_AndStatsAreBitwiseUnchanged()
        {
            var p = MakePart();

            Assert.True(p.Efficiency == 1f, "新件效率必须恰为 1.0f（×1.0 才逐位不变）");

            var s = p.Stats;
            Assert.True(s.sensorRange == 1_000_000f, $"sensorRange={s.sensorRange:R}（应为原值，不许有乘法漂移）");
            Assert.True(s.ecmResist == 0.5f, $"ecmResist={s.ecmResist:R}");
            Assert.True(s.jamStrength == 2f, $"jamStrength={s.jamStrength:R}");
            Assert.True(s.mass == 500f, $"mass={s.mass:R}");
        }

        [Theory]
        [InlineData(1.00f, 1.000f)]
        [InlineData(0.60f, 0.500f)]
        [InlineData(0.20f, 0.000f)]
        [InlineData(0.19f, 0.000f)]
        [InlineData(0.00f, 0.000f)]
        public void Efficiency_FollowsDesignCurve(float targetCondition, float expectedEfficiency)
        {
            var p = MakePart();
            p.ApplyWear(1f - targetCondition);

            Assert.Equal(expectedEfficiency, p.Efficiency, 6);
        }

        [Fact]
        public void StructureDamage_CapsCondition_SoEfficiencyCeilingIs0375()
        {
            var p = MakePart();

            p.ApplyDamage(50f);   // hp 100 → 50，condition 上限 = 0.5

            Assert.Equal(50f, p.hp, 3);
            Assert.Equal(0.5f, p.condition, 6);       // 结构伤顺带压维护值
            Assert.Equal(0.375f, p.Efficiency, 6);    // (0.5 − 0.2) / 0.8
            Assert.Equal(375_000f, p.Stats.sensorRange, 3);   // 1e6 × 0.375
        }

        [Fact]
        public void Repair_RaisesStructure_ButDoesNotRestoreWear()
        {
            var p = MakePart();
            p.ApplyDamage(50f);
            float wornCondition = p.condition;

            Assert.Equal(50f, p.Repair(1000f), 3);    // 修回满结构

            Assert.Equal(SpacePart.MaxHp, p.hp, 3);
            Assert.Equal(wornCondition, p.condition, 6);   // 维修 ≠ 保养（§11.5：维护值是独立事件通道）
        }

        [Fact]
        public void ApplyWear_ClampsToZero_AndReportsAppliedAmount()
        {
            var p = MakePart();

            Assert.Equal(0.3f, p.ApplyWear(0.3f), 6);
            Assert.Equal(0.7f, p.condition, 6);

            Assert.Equal(0.7f, p.ApplyWear(5f), 6);   // 只扣到 0
            Assert.Equal(0f, p.condition, 6);

            Assert.Equal(0f, p.ApplyWear(0.1f), 6);   // 已归零：不再扣
            Assert.Equal(0f, p.Efficiency, 6);
        }

        [Fact]
        public void ApplyWear_IsRejectedOnDestroyedPart()
        {
            var p = MakePart();
            p.ApplyDamage(SpacePart.MaxHp + 1f);
            Assert.True(p.isDestroyed);

            Assert.Equal(0f, p.ApplyWear(0.1f), 6);
            Assert.Equal(0f, p.condition, 6);
            Assert.Equal(0f, p.Efficiency, 6);
        }

        [Fact]
        public void Destroy_ByDamage_ZeroesCondition()
        {
            var p = MakePart();
            p.ApplyDamage(SpacePart.MaxHp + 10f);

            Assert.True(p.isDestroyed);
            Assert.Equal(0f, p.condition, 6);
            Assert.Equal(0f, p.Efficiency, 6);
        }

        [Fact]
        public void Destroy_ByMarkPartDestroyed_AlsoZeroesCondition()
        {
            // 第二条摧毁路径（战斗侧手工标记 / 调试件）：口径必须与 ApplyDamage 一致。
            var e = new SpaceEntity(1, 0);
            var p = MakePart();
            e.SetParts(new List<SpacePart> { p });

            e.MarkPartDestroyed(p.partId);

            Assert.True(p.isDestroyed);
            Assert.Equal(0f, p.condition, 6);
        }

        [Fact]
        public void EfficiencyZero_StopsFunctionalStats_ButMassStays()
        {
            var p = MakePart();
            p.ApplyWear(0.81f);   // condition 0.19 ⇒ 完全停机

            var s = p.Stats;
            Assert.Equal(0f, s.sensorRange, 6);
            Assert.Equal(0f, s.jamStrength, 6);
            Assert.True(s.mass == 500f, "质量绝不乘效率（§11.4）");
        }

        [Fact]
        public void Condition_ScribesAndIsClampedToStructureCapOnLoad()
        {
            // 坏数据：维护值 0.9 高于结构上限 0.4（50/100）。读档时必须夹回 0.4。
            var p = new SpacePart
            {
                partId = 9,
                defName = "PF_ShipPart_Cube",
                boundsSizeShip = new DVector3(2, 2, 2),
                hp = 40f,
                condition = 0.9f,
            };

            string path = Path.Combine(Path.GetTempPath(), $"pfl_condition_{Guid.NewGuid():N}.xml");
            try
            {
                if (Scribe.mode != LoadSaveMode.Inactive) Scribe.ForceStop();
                Scribe.saver.InitSaving(path, "part");
                p.ExposeData();
                Scribe.saver.FinalizeSaving();

                var dst = new SpacePart();
                Scribe.loader.InitLoading(path);
                dst.ExposeData();
                Scribe.ExitNode();
                Scribe.mode = LoadSaveMode.Inactive;

                Assert.Equal(40f, dst.hp, 3);
                Assert.Equal(0.4f, dst.condition, 6);
                Assert.Equal(0.25f, dst.Efficiency, 6);   // (0.4 − 0.2) / 0.8
            }
            finally
            {
                if (Scribe.mode != LoadSaveMode.Inactive) Scribe.ForceStop();
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
