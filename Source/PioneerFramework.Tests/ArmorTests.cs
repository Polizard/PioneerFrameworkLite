using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **§11.1–11.2 装甲的判据**（2026-10-22 部件功能化）。
    ///
    /// 设计稿的三条硬约束逐条钉死：
    /// · 装甲**只影响伤害计算**：不改质量、不改几何、不参与命中；
    /// · `结构伤害 = dmg × clamp(pen/(pen+armor), 0.05, 1)`，**无装甲 ⇒ 系数恰 1**（旧行为逐位不变）；
    /// · `维护伤害 = dmg × k_wear / MaxHp`，**不吃装甲**（否则重甲件永不失修）。
    /// 另加：单调性（穿透↑不降、装甲↑不增）、下界 0.05、摧毁仍归零、目录→零件的数据拷贝。
    /// </summary>
    public class ArmorTests
    {
        private static ArmorMaterialDef Material(string name, float perMm)
            => new() { defName = name, protectionPerMm = perMm };

        private static SpacePart MakePart(ArmorMaterialDef? material = null, float thicknessMm = 0f, bool indestructible = false)
        {
            var p = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_Cube",
                boundsSizeShip = new DVector3(2, 2, 2),
                armorMaterial = material,
                armorThicknessMm = thicknessMm,
                isIndestructible = indestructible,
            };
            p.AddComponent(new HullMassComponent { mass = 100f });
            return p;
        }

        [Fact]
        public void NoArmor_StructuralFactorIsExactlyOne()
        {
            Assert.True(ArmorMath.StructuralFactor(5f, 0f) == 1f);
            Assert.True(ArmorMath.StructuralFactor(0f, 0f) == 1f);
            Assert.True(ArmorMath.StructuralFactor(1e9f, 0f) == 1f);
        }

        [Fact]
        public void StructuralFactor_FollowsDesignCurve()
        {
            // pen / (pen + armor)
            Assert.Equal(0.5f, ArmorMath.StructuralFactor(20f, 20f), 6);
            Assert.Equal(0.2f, ArmorMath.StructuralFactor(5f, 20f), 6);
            Assert.Equal(100f / 120f, ArmorMath.StructuralFactor(100f, 20f), 6);
            // 下界：穿透 0 也蹭掉 5%
            Assert.Equal(ArmorMath.MinStructuralFactor, ArmorMath.StructuralFactor(0f, 20f), 6);
            Assert.Equal(ArmorMath.MinStructuralFactor, ArmorMath.StructuralFactor(-5f, 20f), 6);
        }

        [Fact]
        public void StructuralFactor_MonotoneInPenetrationUp_AndInArmorDown()
        {
            float last = -1f;
            for (int pen = 0; pen <= 100; pen += 5)
            {
                float f = ArmorMath.StructuralFactor(pen, 20f);
                Assert.True(f >= last - 1e-6f, $"穿透增加时系数降了：pen={pen} f={f} last={last}");
                last = f;
            }

            last = 2f;
            for (int armor = 0; armor <= 200; armor += 10)
            {
                float f = ArmorMath.StructuralFactor(20f, armor);
                Assert.True(f <= last + 1e-6f, $"装甲增加时系数升了：armor={armor} f={f} last={last}");
                last = f;
            }
        }

        [Fact]
        public void NoArmor_KeepsOldStructuralDamage_AndAddsWear()
        {
            var p = MakePart();
            p.ApplyProjectileDamage(20f, 5f);

            Assert.Equal(80f, p.hp, 4);                                  // 结构：20 点全吃（旧行为）
            // 结构伤压上限到 0.8，再扣维护伤 20×0.003 = 0.06
            Assert.Equal(0.74f, p.condition, 5);
            Assert.Equal(ArmorMath.WearAmount(20f), 0.06f, 6);
        }

        [Fact]
        public void Armor_ReducesStructuralDamage_ButWearIsUnchanged()
        {
            var steel = Material("PF_Armor_Steel", 2f);   // 2 防护/mm
            var armored = MakePart(steel, 10f);            // 装甲值 20
            var bare = MakePart();

            armored.ApplyProjectileDamage(20f, 20f);       // 系数 20/(20+20) = 0.5
            bare.ApplyProjectileDamage(20f, 20f);

            Assert.Equal(90f, armored.hp, 4);              // 只吃 10
            Assert.Equal(80f, bare.hp, 4);

            // 维护伤不吃装甲：两边**扣的量**必须一样（只是各自上限不同 ⇒ 绝对值不同）
            Assert.Equal(0.9f - ArmorMath.WearAmount(20f), armored.condition, 5);
            Assert.Equal(0.8f - ArmorMath.WearAmount(20f), bare.condition, 5);
        }

        [Fact]
        public void Armor_HasFloorOfFivePercent()
        {
            var composite = Material("PF_Armor_Composite", 2f);
            var p = MakePart(composite, 100f);   // 装甲值 200

            p.ApplyProjectileDamage(100f, 1f);   // 1/(1+200) ≪ 0.05 ⇒ 取下界

            Assert.Equal(95f, p.hp, 3);          // 只吃 5 点
        }

        [Fact]
        public void Overkill_ThroughArmor_StillDestroys_AndZeroesCondition()
        {
            var p = MakePart(Material("PF_Armor_Composite", 2.6f), 50f);

            p.ApplyProjectileDamage(10_000f, 10f);

            Assert.True(p.isDestroyed);
            Assert.True(p.hp <= 0f);
            Assert.True(p.condition == 0f);
        }

        [Fact]
        public void IndestructiblePart_TakesNoDamageAndNoWear()
        {
            var p = MakePart(Material("x", 1f), 10f, indestructible: true);
            p.ApplyProjectileDamage(100f, 100f);

            Assert.Equal(SpacePart.MaxHp, p.hp, 4);
            Assert.Equal(1f, p.condition, 6);
        }

        [Fact]
        public void Armor_DoesNotChangeMassOrGeometry()
        {
            var bare = MakePart();
            var armored = MakePart(Material("x", 2.6f), 100f);

            Assert.True(armored.ArmorValue == 260f);
            Assert.True(armored.Stats.mass == bare.Stats.mass, "装甲不参与质量（§11.1）");
            Assert.Equal(bare.boundsSizeShip, armored.boundsSizeShip);
        }

        [Fact]
        public void ArmorValue_IsThicknessTimesProtection()
        {
            Assert.Equal(12f, MakePart(Material("steel", 1f), 12f).ArmorValue, 6);
            Assert.Equal(65f, MakePart(Material("composite", 2.6f), 25f).ArmorValue, 6);
            Assert.Equal(0f, MakePart(null, 25f).ArmorValue, 6);          // 有厚度没材料
            Assert.Equal(0f, MakePart(Material("x", 2f), 0f).ArmorValue, 6); // 有材料没厚度
        }

        [Fact]
        public void Assembler_CopiesArmorFromCatalogDef()
        {
            var composite = Material("PF_Armor_Composite", 2.6f);
            var def = new ShipPartDef
            {
                defName = "Test_Armored",
                label = "测试装甲件",
                category = "misc",
                massKg = 100f,
                armorMaterial = composite,
                armorThicknessMm = 25f,
                presets = new List<PartComponentPreset>(),
            };
            var spec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "Test_Armored", category = "misc", quantity = 1 },
                },
            };

            var parts = DesignPartAssembler.BuildParts(spec, 10, 10, out _, _ => def);

            Assert.Equal(2, parts.Count);
            Assert.Same(composite, parts[1].armorMaterial);
            Assert.Equal(25f, parts[1].armorThicknessMm);
            Assert.Equal(65f, parts[1].ArmorValue, 6);
        }
    }
}
