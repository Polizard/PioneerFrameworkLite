using System;
using System.IO;
using PioneerFramework;
using PioneerFramework.Components;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **`SpacePart` 的 Scribe 落盘**（2026-10-17 · 批 -1 新增；用户令"变化剧烈 ⇒ 多下探针多跑测试"）。
    ///
    /// 背景：`isIndestructible` 曾**漏盘**（批 -1 修复 #1：读档后站体失去不可毁性 ⇒ 可被摧毁并连锁毁图）。
    /// 本文件把两半分别钉死：
    /// · **写出侧**（`SaveSide_...`）：断言 XML 里字段在场——直接物证，不依赖任何类型解析；
    /// · **读入侧**（`LoadSide_...`）：标量字段往返（含 `isIndestructible`）。
    ///
    /// ⚠ **组件（多态 Deep 节点）的读入在 headless 下不可达**（实测取证：加载侧靠
    /// `GenTypes.GetTypeInAnyAssembly` 解析 `<li Class="…">`，而它只枚举**游戏注册的程序集** ⇒
    /// 测试进程里解析恒 null、节点静默变 null）。组件往返留游戏内覆盖（读档后站体仍不可毁 = 同一张网）。
    ///
    /// 范式（headless Scribe 的两个要点；照 `MapArchiveManager` 的生产 idiom）：
    /// ① `InitSaving(path, 根名)` **自己 Enter 根节点** ⇒ `ExposeData` 直接写在根上；`InitLoading` 把
    ///    `curXmlParent` 置于文档元素 ⇒ 同样直接 `ExposeData`；
    /// ② **不能调 `FinalizeLoading()`**（headless 走 `DeepProfiler` → `Prefs.LogVerbose` ⇒ NRE，首跑实测）⇒
    ///    手做"退根 + 置 Inactive"两步。
    /// ⚠ `Scribe.mode/saver/loader` 是进程级静态态 ⇒ try/finally `ForceStop` 收场。
    /// </summary>
    public class SpacePartScribeTests
    {
        private const string XmlRoot = "part";

        private static SpacePart MakePart() => new()
        {
            partId = 7,
            defName = "PF_ShipPart_Cube",
            positionShip = new DVector3(1.5, -2.0, 3.25),
            boundsSizeShip = new DVector3(4, 5, 6),
            hp = 42.5f,
            isDestroyed = true,
            isIndestructible = true,   // ★ 批 -1 修复点：此前漏盘 ⇒ 读档后站体变可毁
            linkedMapId = 55,
            rotationShip = DQuaternion.AngleAxis(0.5, DVector3.up),
            shape = InertiaTensor.Shape.Cylinder,
            stageId = 3,
            blockId = 4,
            stageDisposition = StageDisposition.Keep,
            parentStageId = 2,
            parentId = 9,
        };

        private static string SaveToTemp(SpacePart part)
        {
            string path = Path.Combine(Path.GetTempPath(), $"pfl_spart_{Guid.NewGuid():N}.xml");
            Scribe.saver.InitSaving(path, XmlRoot);
            part.ExposeData();
            Scribe.saver.FinalizeSaving();
            return path;
        }

        private static void SafeStop()
        {
            if (Scribe.mode != LoadSaveMode.Inactive)
                Scribe.ForceStop();
        }

        [Fact]
        public void SaveSide_PinsIsIndestructible_AndFullFieldSet()
        {
            SafeStop();
            string path = SaveToTemp(MakePart());
            try
            {
                string xml = File.ReadAllText(path);
                Assert.Contains("<isIndestructible>True</isIndestructible>", xml);   // ★ 批 -1 修复 #1 物证
                Assert.Contains("<isDestroyed>True</isDestroyed>", xml);
                Assert.Contains("<linkedMapId>55</linkedMapId>", xml);
                Assert.Contains("<hp>42.5</hp>", xml);
                Assert.Contains("<partId>7</partId>", xml);
                Assert.Contains("<positionShip_x>1.5</positionShip_x>", xml);
                Assert.Contains("<rotationShip_w>", xml);
                Assert.Contains("<shape>Cylinder</shape>", xml);
                Assert.Contains("<stageId>3</stageId>", xml);
                Assert.Contains("<blockId>4</blockId>", xml);
                Assert.Contains("<stageDisposition>Keep</stageDisposition>", xml);
                Assert.Contains("<parentStageId>2</parentStageId>", xml);
                Assert.Contains("<parentId>9</parentId>", xml);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void SaveSide_SensorComponent_WritesAllSixFields()
        {
            // ★ 2026-10-17 批 -1：`SensorArrayComponentData.ExposeData` 曾只存 3/6（memorySeconds/
            // activeGainBonus/activeRadiation 漏盘，与它的 AccumulateStats 贡献面对不上）。
            // 组件读入 headless 不可达（见类头注）⇒ 这一半以**写出物证**钉住；读入侧由 Clone 用例
            // （二进制 Write/Read 通道）覆盖。
            SafeStop();
            var part = MakePart();
            part.AddComponent(new SensorArrayComponentData
            {
                passiveRangeMeters = 123_000f,
                ecmResist = 0.5f,
                eccmResist = 0.25f,
                memorySeconds = 45f,
                activeGainBonus = 0.75f,
                activeRadiation = 3.5f,
            });
            string path = SaveToTemp(part);
            try
            {
                string xml = File.ReadAllText(path);
                Assert.Contains("<passiveRangeMeters>123000</passiveRangeMeters>", xml);
                Assert.Contains("<ecmResist>0.5</ecmResist>", xml);
                Assert.Contains("<eccmResist>0.25</eccmResist>", xml);
                Assert.Contains("<memorySeconds>45</memorySeconds>", xml);      // ★ 批 -1：曾漏盘
                Assert.Contains("<activeGainBonus>0.75</activeGainBonus>", xml); // ★ 批 -1：曾漏盘
                Assert.Contains("<activeRadiation>3.5</activeRadiation>", xml);  // ★ 批 -1：曾漏盘
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void LoadSide_ScalarFields_RoundTrip()
        {
            SafeStop();
            string path = SaveToTemp(MakePart());   // 不带组件：多态 Deep 读入 headless 不可达（见类头注）
            try
            {
                var dst = new SpacePart();
                Scribe.loader.InitLoading(path);
                dst.ExposeData();
                Scribe.ExitNode();
                Scribe.mode = LoadSaveMode.Inactive;

                Assert.Equal(7, dst.partId);
                Assert.Equal("PF_ShipPart_Cube", dst.defName);
                Assert.Equal(1.5, dst.positionShip.x, 6);
                Assert.Equal(-2.0, dst.positionShip.y, 6);
                Assert.Equal(3.25, dst.positionShip.z, 6);
                Assert.Equal(4.0, dst.boundsSizeShip.x, 6);
                Assert.Equal(5.0, dst.boundsSizeShip.y, 6);
                Assert.Equal(6.0, dst.boundsSizeShip.z, 6);
                Assert.Equal(42.5f, dst.hp, 3);
                Assert.True(dst.isDestroyed);
                Assert.True(dst.isIndestructible, "★ isIndestructible 必须活过往返（批 -1 修复 #1 的判据）");
                Assert.Equal(55, dst.linkedMapId);
                Assert.Equal(InertiaTensor.Shape.Cylinder, dst.shape);
                Assert.Equal(3, dst.stageId);
                Assert.Equal(4, dst.blockId);
                Assert.Equal(StageDisposition.Keep, dst.stageDisposition);
                Assert.Equal(2, dst.parentStageId);
                Assert.Equal(9, dst.parentId);
                Assert.Equal(MakePart().rotationShip, dst.rotationShip);
            }
            finally
            {
                SafeStop();
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
