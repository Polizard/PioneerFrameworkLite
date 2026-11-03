using System.IO;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **维修 R1/R2 的判据**（2026-10-21，机制缺口第一阶段）。
    ///
    /// 立这条线只为一件事：**打坏之后要有闭环，但"摧毁"必须仍是终态**。
    /// 三条判据对应三个可能被后来人改坏的地方：
    /// · `Repair` 真的回涨 hp 且**夹在 `MaxHp`**（不能修出超血）；
    /// · **已摧毁件拒绝维修**（维修 ≠ 复活）—— 变异 M34 钉住；
    /// · 维修要走 `InvalidateStats`（组件质量/功能随之重算），且组件收到 `OnRepaired`。
    /// </summary>
    public class PartRepairTests
    {
        private sealed class RepairProbe : PartComponentData
        {
            public int Repaired;
            public float RepairedTotal;
            public override void OnRepaired(float amount) { Repaired++; RepairedTotal += amount; }
            public override void ExposeData() { }
            public override void Write(BinaryWriter w) { }
            public override void Read(BinaryReader r) { }
        }

        private static SpacePart MakePart(out RepairProbe probe, float mass = 500f)
        {
            var p = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_Cube",
                boundsSizeShip = new DVector3(2, 2, 2),
            };
            probe = new RepairProbe();
            p.AddComponent(probe);
            p.AddComponent(new HullMassComponent { mass = mass });
            return p;
        }

        [Fact]
        public void Repair_RaisesHp_AndNotifiesComponent()
        {
            var p = MakePart(out var probe);
            p.ApplyDamage(40f);
            Assert.Equal(60f, p.hp, 3);

            float applied = p.Repair(25f);

            Assert.Equal(25f, applied, 3);
            Assert.Equal(85f, p.hp, 3);
            Assert.Equal(1, probe.Repaired);
            Assert.Equal(25f, probe.RepairedTotal, 3);
            Assert.False(p.isDestroyed);
        }

        [Fact]
        public void Repair_DestroyedPart_IsRejected()
        {
            var p = MakePart(out var probe);
            p.ApplyDamage(SpacePart.MaxHp + 10f);        // 打穿 ⇒ 真摧毁
            Assert.True(p.isDestroyed);
            Assert.True(p.hp <= 0f);

            float applied = p.Repair(50f);

            Assert.Equal(0f, applied, 3);
            Assert.True(p.isDestroyed);                  // **终态**：维修不许复活
            Assert.True(p.hp <= 0f);
            Assert.Equal(0, probe.Repaired);             // 一个回调都不许发
        }

        [Fact]
        public void Repair_ClampsAtMaxHp_AndIsNoOpWhenFull()
        {
            var p = MakePart(out var probe);
            p.ApplyDamage(10f);

            Assert.Equal(10f, p.Repair(1000f), 3);       // 只涨回 10
            Assert.Equal(SpacePart.MaxHp, p.hp, 3);

            Assert.Equal(0f, p.Repair(50f), 3);          // 满血：不再通知
            Assert.Equal(1, probe.Repaired);
        }

        [Fact]
        public void Repair_InvalidatesStats_SoComponentsRecompute()
        {
            var p = MakePart(out _, mass: 500f);
            p.ApplyDamage(60f);                          // hp 100 → 40（未摧毁）
            Assert.False(p.isDestroyed);
            _ = p.Stats;                                 // 先把脏读掉 ⇒ 拿到当前版本号
            int versionAfterDamage = p.StatsVersion;

            float applied = p.Repair(1000f);             // 夹到满血 ⇒ 实际 60

            Assert.Equal(60f, applied, 3);
            Assert.Equal(SpacePart.MaxHp, p.hp, 3);
            _ = p.Stats;                                 // 若 Repair 没失效，这里读的是旧缓存、版本不涨
            Assert.True(p.StatsVersion > versionAfterDamage, "维修必须走 InvalidateStats");
            Assert.Equal(500.0, p.Stats.mass, 3);
        }
    }
}
