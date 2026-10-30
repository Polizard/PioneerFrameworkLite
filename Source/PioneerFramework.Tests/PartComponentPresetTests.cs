using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using PioneerFramework;
using PioneerFramework.Components;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **§4 目录预置 + §3 四个哑组件的判据**（2026-10-22 部件功能化）。
    ///
    /// 设计稿给的判据是"同一份预置装配出的 `SpacePart`，与手写 `AddComponent` 的结果**逐字段相等**"，
    /// 所以本文件的重心是**等价性**，不是"跑通"：
    /// · 装配器按预置造件 vs 手写造件 ⇒ `ShipStats` 全字段逐位相等；
    /// · 四个新组件只写自己那几个槽（质量保持 0，等结构件给）；
    /// · 效率曲线生效（0.6 ⇒ 0.5），货舱容量不随维护值缩（几何量）；
    /// · **XML 目录契约**：每个元素都绑得到字段、每个 typeId 都注册了、每个参数键都被组件认领
    ///   （拼错会静默留默认值，这是本库审计点过名的同族事故）。
    /// </summary>
    public class PartComponentPresetTests
    {
        // ── 装配等价 ────────────────────────────────────────────────

        private static ShipPartDef ReactorDef() => new()
        {
            defName = "Test_Reactor",
            label = "测试反应堆",
            category = "reactor",
            massKg = 800f,
            boundsX = 2f,
            boundsY = 3f,
            boundsZ = 2f,
            stackLengthMeters = 3f,
            presets = new List<PartComponentPreset>
            {
                new()
                {
                    typeId = "ReactorComponentData",
                    parameters = new List<PartComponentPresetParam>
                    {
                        new() { key = "powerGenWatts", value = "120000" },
                        new() { key = "heatProductionWatts", value = "240000" },
                    },
                },
            },
        };

        private static void AssertStatsEqual(ShipStats expected, ShipStats actual, string where)
        {
            Assert.True(expected.mass == actual.mass, $"{where}: mass {expected.mass:R} vs {actual.mass:R}");
            Assert.True(expected.thrust == actual.thrust, $"{where}: thrust {expected.thrust:R} vs {actual.thrust:R}");
            Assert.True(expected.powerGen == actual.powerGen, $"{where}: powerGen {expected.powerGen:R} vs {actual.powerGen:R}");
            Assert.True(expected.powerDraw == actual.powerDraw, $"{where}: powerDraw {expected.powerDraw:R} vs {actual.powerDraw:R}");
            Assert.True(expected.heatProduction == actual.heatProduction, $"{where}: heatProduction {expected.heatProduction:R} vs {actual.heatProduction:R}");
            Assert.True(expected.heatDissipation == actual.heatDissipation, $"{where}: heatDissipation {expected.heatDissipation:R} vs {actual.heatDissipation:R}");
            Assert.True(expected.propellant == actual.propellant, $"{where}: propellant {expected.propellant:R} vs {actual.propellant:R}");
            Assert.True(expected.powerStorage == actual.powerStorage, $"{where}: powerStorage {expected.powerStorage:R} vs {actual.powerStorage:R}");
            Assert.True(expected.shieldCapacity == actual.shieldCapacity, $"{where}: shieldCapacity {expected.shieldCapacity:R} vs {actual.shieldCapacity:R}");
            Assert.True(expected.sensorRange == actual.sensorRange, $"{where}: sensorRange {expected.sensorRange:R} vs {actual.sensorRange:R}");
            Assert.True(expected.cargoCapacity == actual.cargoCapacity, $"{where}: cargoCapacity {expected.cargoCapacity:R} vs {actual.cargoCapacity:R}");
            Assert.True(expected.jamStrength == actual.jamStrength, $"{where}: jamStrength {expected.jamStrength:R} vs {actual.jamStrength:R}");
            Assert.True(expected.ecmResist == actual.ecmResist, $"{where}: ecmResist {expected.ecmResist:R} vs {actual.ecmResist:R}");
            Assert.True(expected.eccmResist == actual.eccmResist, $"{where}: eccmResist {expected.eccmResist:R} vs {actual.eccmResist:R}");
            Assert.True(expected.activeGainBonus == actual.activeGainBonus, $"{where}: activeGainBonus {expected.activeGainBonus:R} vs {actual.activeGainBonus:R}");
            Assert.True(expected.activeRadiation == actual.activeRadiation, $"{where}: activeRadiation {expected.activeRadiation:R} vs {actual.activeRadiation:R}");
            Assert.True(expected.memorySeconds == actual.memorySeconds, $"{where}: memorySeconds {expected.memorySeconds:R} vs {actual.memorySeconds:R}");
        }

        [Fact]
        public void PresetAssembly_EqualsHandWrittenComponents()
        {
            var def = ReactorDef();
            var spec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "Test_Reactor", category = "reactor", quantity = 1 },
                },
            };

            var parts = DesignPartAssembler.BuildParts(spec, 10, 10, out _, _ => def);
            Assert.Equal(2, parts.Count);   // 舱盒 + 1

            var presetPart = parts[1];
            var presetReactor = presetPart.GetComponent<ReactorComponentData>();
            Assert.NotNull(presetReactor);
            Assert.Equal(120000f, presetReactor!.powerGenWatts);
            Assert.Equal(240000f, presetReactor.heatProductionWatts);

            // 手写对照：同几何、同结构质量、同组件参数
            var hand = new SpacePart
            {
                partId = presetPart.partId,
                defName = presetPart.defName,
                positionShip = presetPart.positionShip,
                boundsSizeShip = presetPart.boundsSizeShip,
                shape = presetPart.shape,
                hp = presetPart.hp,
            };
            hand.AddComponent(new HullMassComponent { mass = 800f });
            hand.AddComponent(new ReactorComponentData { powerGenWatts = 120000f, heatProductionWatts = 240000f });

            AssertStatsEqual(hand.Stats, presetPart.Stats, "预置 vs 手写");
            Assert.Equal(hand.components.Count, presetPart.components.Count);
        }

        // ── 四个新组件 ──────────────────────────────────────────────

        private static SpacePart NewPartWithAllFourRoles()
        {
            var p = new SpacePart { partId = 1, defName = "PF_ShipPart_Cube", boundsSizeShip = new DVector3(2, 2, 2) };
            p.AddComponent(new ReactorComponentData { powerGenWatts = 120000f, heatProductionWatts = 240000f });
            p.AddComponent(new RadiatorComponentData { dissipationWatts = 300000f });
            p.AddComponent(new CargoComponentData { capacityKg = 20000f });
            p.AddComponent(new WeaponMountComponentData { powerDrawWatts = 50000f, heatProductionWatts = 60000f });
            return p;
        }

        [Fact]
        public void FourNewComponents_WriteOnlyTheirOwnSlots_AndNoMass()
        {
            var p = NewPartWithAllFourRoles();

            var s = p.Stats;
            Assert.Equal(120000f, s.powerGen);
            Assert.Equal(300000f, s.heatProduction);      // 反应堆 240k + 挂点 60k
            Assert.Equal(300000f, s.heatDissipation);
            Assert.Equal(20000f, s.cargoCapacity);
            Assert.Equal(50000f, s.powerDraw);
            Assert.Equal(0f, s.mass);                     // 四个组件都不贡献质量（结构件给）
        }

        [Fact]
        public void FourNewComponents_ScaleWithEfficiency_ExceptCargoCapacity()
        {
            var p = NewPartWithAllFourRoles();
            p.ApplyWear(0.4f);   // condition 0.6 ⇒ eff ≈ 0.5
            Assert.Equal(0.5f, p.Efficiency, 6);

            // 注意：这里**不能**用逐位相等——效率曲线是 (c−0.2)/0.8 的浮点除法，
            // condition=0.6 落到 0.50000006（差 1 ULP）⇒ 120000×eff = 60000.0078。
            // "逐位不变"只承诺**未磨损**那条快路径（Efficiency 恰为 1.0f，见 PartConditionTests）。
            var s = p.Stats;
            AssertNear(60000f, s.powerGen, "powerGen");
            AssertNear(150000f, s.heatProduction, "heatProduction");
            AssertNear(150000f, s.heatDissipation, "heatDissipation");
            AssertNear(25000f, s.powerDraw, "powerDraw");
            Assert.Equal(20000f, s.cargoCapacity);        // 容量是几何量，不随维护值缩
        }

        private static void AssertNear(float expected, float actual, string where)
        {
            float tolerance = Math.Abs(expected) * 1e-6f + 1e-3f;
            Assert.True(Math.Abs(actual - expected) <= tolerance,
                $"{where}: 期望 {expected:R}，实际 {actual:R}（容差 {tolerance:R}）");
        }

        [Fact]
        public void ApplyPreset_RejectsUnknownKeyAndBadNumber()
        {
            Assert.False(new ReactorComponentData().ApplyPreset("nope", "1"));
            Assert.False(new CargoComponentData().ApplyPreset("capacityKg", "not-a-number"));
            // 陷阱：Def 字段名 `tankCapacityKg` ≠ 组件参数键 `capacityKg`（参数键由组件自己定义）
            Assert.False(new PropellantTankComponentData().ApplyPreset("tankCapacityKg", "1"));
            Assert.True(new PropellantTankComponentData().ApplyPreset("capacityKg", "1"));
            Assert.True(new CargoComponentData().ApplyPreset("capacityKg", "1.5"));
        }

        [Fact]
        public void ComponentClone_UsesWriteReadChannel_ForAllFields()
        {
            var src = new WeaponMountComponentData
            {
                weaponDefName = "PF_Weapon_Laser",
                mountOffsetShip = new DVector3(1.5, -2.5, 3.5),
                mountRotationShip = DQuaternion.AngleAxis(0.75, DVector3.up),
                ammoCapacity = 60,
                powerDrawWatts = 50000f,
                heatProductionWatts = 60000f,
            };

            var dst = Assert.IsType<WeaponMountComponentData>(src.Clone());

            Assert.Equal("PF_Weapon_Laser", dst.weaponDefName);
            Assert.Equal(src.mountOffsetShip, dst.mountOffsetShip);
            Assert.Equal(src.mountRotationShip, dst.mountRotationShip);
            Assert.Equal(60, dst.ammoCapacity);
            Assert.Equal(50000f, dst.powerDrawWatts);
            Assert.Equal(60000f, dst.heatProductionWatts);
        }

        // ── XML 目录契约（防"元素拼错 ⇒ 值静默留默认"）───────────────

        private static string RepoRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "1.7", "Defs")) && Directory.Exists(Path.Combine(d.FullName, "Source")))
                    return d.FullName;
            throw new DirectoryNotFoundException("Repository root");
        }

        [Fact]
        public void CatalogXml_EveryElementAndPresetKeyBindsToCode()
        {
            ComponentRegistry.RegisterBuiltIns();

            string path = Path.Combine(RepoRoot(), "1.7", "Defs", "ShipParts", "ShipParts.xml");
            var doc = XDocument.Load(path);
            var baseFields = new HashSet<string>(StringComparer.Ordinal)
            {
                "defName", "label", "labelShort", "description", "ignoreConfigErrors", "modExtensions",
            };

            int defsChecked = 0;
            foreach (var def in doc.Root!.Elements("ShipPartDef"))
            {
                string name = def.Element("defName")?.Value ?? "<缺 defName>";
                defsChecked++;

                Assert.False(string.IsNullOrEmpty(def.Element("category")?.Value), $"{name}: 缺 <category>");

                foreach (var el in def.Elements())
                {
                    string field = el.Name.LocalName;
                    if (field == "presets")
                    {
                        AssertPresetList(name, el);
                        continue;
                    }
                    bool declared = typeof(ShipPartDef).GetField(field, BindingFlags.Public | BindingFlags.Instance) != null
                        || baseFields.Contains(field);
                    Assert.True(declared, $"{name}: <{field}> 不是 ShipPartDef 的字段（RimWorld 会静默忽略）");
                }
            }

            Assert.True(defsChecked >= 3, $"解析到的 ShipPartDef 数量={defsChecked}");
        }

        private static void AssertPresetList(string defName, XElement presets)
        {
            foreach (var li in presets.Elements("li"))
            {
                string typeId = li.Element("typeId")?.Value ?? "";
                Assert.False(string.IsNullOrEmpty(typeId), $"{defName}: 预置条目缺 <typeId>");

                var component = ComponentRegistry.Create(typeId);
                Assert.True(component != null, $"{defName}: 预置组件 typeId=\"{typeId}\" 未注册");

                var parameters = li.Element("parameters");
                if (parameters == null) continue;

                foreach (var param in parameters.Elements("li"))
                {
                    string key = param.Element("key")?.Value ?? "";
                    string value = param.Element("value")?.Value ?? "";
                    Assert.False(string.IsNullOrEmpty(key), $"{defName}: {typeId} 的参数缺 <key>");
                    Assert.True(component!.ApplyPreset(key, value),
                        $"{defName}: 组件 {typeId} 不认识参数 \"{key}\"（值 \"{value}\"）");
                }

                // §5 的"成对补入"：反应堆填了发电就必须填产热（否则热闭环永远不超温）。
                if (component is ReactorComponentData reactor && reactor.powerGenWatts > 0f)
                    Assert.True(reactor.heatProductionWatts > 0f,
                        $"{defName}: 反应堆有发电但没填 heatProductionWatts（产热必须与发电成对）");
            }
        }
    }
}
