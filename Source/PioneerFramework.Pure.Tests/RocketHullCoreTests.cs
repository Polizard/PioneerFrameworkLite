using System.Collections.Generic;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **火箭外壳几何判据**（2026-10-30 用户规格）：
    /// 24 边 lowpoly（棱柱 + 棱台）· 圆截面 · 引擎算进筒径 · 非活动级/碎片包引擎（底封口）·
    /// 活动级底开口露喷管 · 级间对齐更小编号（上方）一侧 · 0.3 m 空隙归编号较大一侧 ·
    /// 整流罩只认"0 级最上面的载荷件"（棱柱 + 棱台、顶面很小）· 损毁按站上色。
    /// </summary>
    public class RocketHullCoreTests
    {
        private static RocketHullCore.Part P(int stage, double centerZ, double length,
            double halfX, double halfY, bool engine = false, bool destroyed = false, bool payload = false)
            => new()
            {
                stageId = stage,
                centerZ = centerZ,
                lengthZ = length,
                halfX = halfX,
                halfY = halfY,
                isEngine = engine,
                destroyed = destroyed,
                isPayload = payload,
            };

        [Fact]
        public void Sides_Is24()
        {
            Assert.Equal(24, RocketHullCore.Sides);
        }

        [Fact]
        public void SingleActiveStage_EngineCountsInRadius_AndBottomOpensAtEngineTop()
        {
            // 罐：半径 2、长 10、中心 0（z ∈ [-5,5]）；引擎：半径 2.5、长 4、中心 −7（z ∈ [-9,-5]）
            var parts = new List<RocketHullCore.Part>
            {
                P(0, 0, 10, 2, 2),
                P(0, -7, 4, 2.5, 2.5, engine: true),
            };

            var r = RocketHullCore.Build(parts, activeStage: 0, jettisoned: false);

            // "考虑引擎"：筒半径 = 2.5（不是罐的 2）
            Assert.All(r.bands, b => Assert.Equal(2.5, b.rTop, 6));
            Assert.All(r.bands, b => Assert.Equal(2.5, b.rBottom, 6));
            // 活动级：底部在引擎顶面（−5）开口，不封底
            Assert.Equal(-5.0, r.bands[r.bands.Count - 1].zBottom, 6);
            Assert.Empty(r.caps);
            // 顶面 = 该级最上面的件顶面（5）
            Assert.Equal(5.0, r.bands[0].zTop, 6);
        }

        [Fact]
        public void TwoStages_LowerStageTopAlignsToUpperStage_AndGapLivesInsideLowerHull()
        {
            // 0 级（上）：半径 1.5，z ∈ [-5,5]
            // 1 级（下）：半径 2.5，z ∈ [-15.3,-5.3]（与上级之间留了 0.3 m 空隙）
            var parts = new List<RocketHullCore.Part>
            {
                P(0, 0, 10, 1.5, 1.5),
                P(1, -10.3, 10, 2.5, 2.5),
            };

            var r = RocketHullCore.Build(parts, activeStage: 1, jettisoned: false);

            // 取每个级的上/下沿
            double stage0Top = double.MinValue, stage0Bottom = double.MaxValue, stage1Top = double.MinValue;
            foreach (var b in r.bands)
            {
                if (b.stageId == 0) { if (b.zTop > stage0Top) stage0Top = b.zTop; if (b.zBottom < stage0Bottom) stage0Bottom = b.zBottom; }
                if (b.stageId == 1 && b.zTop > stage1Top) stage1Top = b.zTop;
            }

            Assert.Equal(5.0, stage0Top, 6);
            Assert.Equal(-5.0, stage0Bottom, 6);            // 非活动级：包到自己的件底
            Assert.Equal(-5.0, stage1Top, 6);               // 1 级壳顶 = 0 级壳底 ⇒ 外形连续
            Assert.True(stage1Top > -5.3 + 1e-6);           // 0.3 m 空隙落在 1 级壳内（壳顶高于 1 级首件顶面）

            // 1 级顶部是棱台（对齐 0 级半径 1.5 → 2.5）
            var frustum = r.bands.Find(b => b.stageId == 1 && System.Math.Abs(b.rTop - b.rBottom) > 1e-6);
            Assert.Equal(1.5, frustum.rTop, 6);
            Assert.Equal(2.5, frustum.rBottom, 6);

            // 0 级非活动 ⇒ 封底；1 级活动 ⇒ 不封底
            Assert.Contains(r.caps, c => c.stageId == 0);
            Assert.DoesNotContain(r.caps, c => c.stageId == 1);
        }

        [Fact]
        public void JettisonedDebris_EnclosesEngines()
        {
            var parts = new List<RocketHullCore.Part>
            {
                P(1, 0, 10, 2, 2),
                P(1, -7, 4, 2, 2, engine: true),
            };

            var r = RocketHullCore.Build(parts, activeStage: 1, jettisoned: true);

            // 碎片：即使自己是"活动级"也包住引擎 ⇒ 底到 −9（引擎底面）并封底
            Assert.Equal(-9.0, r.bands[r.bands.Count - 1].zBottom, 6);
            Assert.Contains(r.caps, c => c.stageId == 1);
        }

        [Fact]
        public void Fairing_OnlyWhenPayloadIsTopmostOfStageZero()
        {
            var withFairing = new List<RocketHullCore.Part>
            {
                P(0, 0, 4, 2, 2, payload: true),     // 最上面的载荷件（z ∈ [-2,2]）
                P(0, -6, 8, 2, 2),                   // 载荷下面的罐（z ∈ [-10,-2]）
            };
            var r = RocketHullCore.Build(withFairing, activeStage: 0, jettisoned: false);

            Assert.True(r.hasFairing);
            Assert.Equal(-2.0, r.fairingBottomZ, 6);                 // 罩下缘 = 级 0 壳顶
            double diameter = r.fairingBaseRadius * 2.0;
            Assert.Equal(diameter * RocketHullCore.FairingHeightPerDiameter,
                r.fairingTopZ - r.fairingBottomZ, 6);
            Assert.Equal(diameter * RocketHullCore.FairingTopPerDiameter, r.fairingTopRadius, 6);
            Assert.True(r.fairingTopRadius < r.fairingBaseRadius * 0.5);   // "顶面很小"，不是锥尖

            // 罩 = 棱柱 + 棱台两段
            var fairingBands = r.bands.FindAll(b => b.isFairing);
            Assert.Equal(2, fairingBands.Count);
            Assert.Equal(r.fairingBaseRadius, fairingBands[0].rTop, 6);
            Assert.Equal(r.fairingBaseRadius, fairingBands[0].rBottom, 6);   // 下段棱柱
            Assert.Equal(r.fairingTopRadius, fairingBands[1].rTop, 6);       // 上段棱台收到小顶面
            Assert.Equal(r.fairingBaseRadius, fairingBands[1].rBottom, 6);

            // 级 0 的壳从罩下缘开始
            double stage0Top = double.MinValue;
            foreach (var b in r.bands)
                if (!b.isFairing && b.stageId == 0 && b.zTop > stage0Top) stage0Top = b.zTop;
            Assert.Equal(r.fairingBottomZ, stage0Top, 6);

            // 载荷件不在最上面 ⇒ 不画罩
            var payloadBelow = new List<RocketHullCore.Part>
            {
                P(0, 0, 4, 2, 2),                    // 罐在最上面
                P(0, -6, 4, 2, 2, payload: true),    // 载荷在下面
            };
            Assert.False(RocketHullCore.Build(payloadBelow, activeStage: 0, jettisoned: false).hasFairing);
        }

        [Fact]
        public void Damage_IsColoredPerStation()
        {
            // 同一级两站：上站健康、下站损毁（两站同半径，壳是一根筒，但分段上色）
            var parts = new List<RocketHullCore.Part>
            {
                P(0, 3, 6, 2, 2),
                P(0, -3, 6, 2, 2, destroyed: true),
            };

            var r = RocketHullCore.Build(parts, activeStage: 0, jettisoned: false);

            bool anyDestroyed = false, anyHealthy = false;
            foreach (var b in r.bands)
            {
                if (b.zTop <= 3.0 + 1e-6 && b.zBottom >= -3.0 - 1e-6) { /* 跨站段不算 */ }
                if (b.destroyed) anyDestroyed = true; else anyHealthy = true;
            }
            Assert.True(anyDestroyed, "下站应标损毁");
            Assert.True(anyHealthy, "上站应健康");
        }

        [Fact]
        public void Empty_IsEmpty()
        {
            var r = RocketHullCore.Build(new List<RocketHullCore.Part>(), 0, false);
            Assert.False(r.HasGeometry);
            Assert.Empty(r.bands);
            Assert.Empty(r.caps);
        }
    }
}
