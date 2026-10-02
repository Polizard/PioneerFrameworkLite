using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **RCS 尺寸口径：姿态无关**（2026-10-02 用户裁定 ①）。
    ///
    /// ## 为什么要有这条
    /// `SignalStrength` 的尺寸项原先取 `GetAabbSystem` 的半对角线，而那个 AABB 是**旋转后**的世界轴对齐投影
    /// ⇒ 姿态一变尺寸就变；而 `InvalidateSignal` 的三个驱动点（部件集 / 点火 / 开机）里**没有"旋转"**
    /// ⇒ S 会**静默停在旧值**上。裁定：RCS 暂时与姿态无关 ⇒ 改用**船体系**包围盒半对角线（`AabbHalfSize`，
    /// 对该船是常数）。
    ///
    /// ## 这条判据的分辨率（不许用沉默冒充覆盖）
    /// · ✅ 钉住三件：①尺寸量 = **局部盒**半对角线（= |`boundsSizeShip`| 的盒半对角线，可手算复核）；
    ///   ②**姿态不变**；③**随部件变**（证明它读的是尺寸，不是常数）。
    /// · ⚠ 验不了两件，各有归属：**(a)** "世界系 AABB 半对角线会随姿态变"那半边对照 ——
    ///   `GetAabbSystem` 读 `Verse.Find.TickManager`，而这个 getter 在无 Game 时**直接 NRE**（`?.` 救不了）
    ///   ⇒ 归**游戏内**验收器（"转 45° 后 S 逐位不变"，两侧都 `InvalidateSignal` 强制重算 ⇒ 有牙齿）；
    ///   **(b)** 群级 `RecomputeDerived` 的聚合路径 —— headless 无 `Current.Game` ⇒ 实体循环被跳过
    ///   （与 `SignalInvalidationTests` 已登记的同一限制）。
    /// </summary>
    public class SignalAttitudeTests
    {
        /// <summary>绕 X 轴 45° 的四元数（= sin22.5°, 0, 0, cos22.5°）。分量直构，见本文件里那条 ECall 说明。</summary>
        private static readonly Quaternion RotX45 = new Quaternion(0.38268343f, 0f, 0f, 0.92387953f);

        private static SpacePart Box(double x, double y, double z)
        {
            var part = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_Cube",
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(x, y, z),
                hp = 1000f,
            };
            return part;
        }

        [Fact]
        public void RcsSizeMeasure_IsLocalBoxHalfDiagonal_AndAttitudeInvariant()
        {
            var e = new SpaceEntity(7001, 0);
            e.SetParts(new List<SpacePart> { Box(40.0, 20.0, 80.0) });

            // ① 口径 = 局部盒半对角线（可手算复核：|(40,20,80)| / 2 = 45.825756…）
            double expected = System.Math.Sqrt(40.0 * 40.0 + 20.0 * 20.0 + 80.0 * 80.0) * 0.5;
            Assert.Equal(expected, e.AabbHalfSize.magnitude, 9);

            // ② 姿态不变（裁定 ① 的正文）
            // ⚠ 四元数**直接构造分量**，不用 `Quaternion.Euler`——后者走 Unity 原生 ECall，
            //   headless 下抛 `SecurityException: ECall 方法必须打包到系统模块中`（本轮实测踩到）。
            double before = e.AabbHalfSize.magnitude;
            e.SetRotation(RotX45);
            Assert.Equal(before, e.AabbHalfSize.magnitude, 12);
            e.SetRotation(new Quaternion(0.2705981f, 0.6532815f, 0.2705981f, 0.6532815f));   // 任意轴/角
            Assert.Equal(before, e.AabbHalfSize.magnitude, 12);

            // ③ 随部件变（证明这条读的是尺寸而非常数）
            e.SetParts(new List<SpacePart> { Box(10.0, 10.0, 10.0) });
            Assert.True(e.AabbHalfSize.magnitude < before,
                $"换小盒后尺寸应变小：{before:F3} → {e.AabbHalfSize.magnitude:F3}");
        }

        [Fact]
        public void MemorySeconds_Flows_ComponentToPartToEntity()
        {
            // [V3]（2026-10-02 裁定 ②）记忆时长的量纲链：组件 → 部件 → 实体级（群级那一段 headless 验不了）
            var e = new SpaceEntity(7003, 0);
            var part = Box(40.0, 20.0, 80.0);
            part.AddComponent(new SensorArrayComponentData
            {
                passiveRangeMeters = 1000f,
                memorySeconds = 30f,
            });
            e.SetParts(new List<SpacePart> { part });

            Assert.Equal(30f, part.Stats.memorySeconds, 6);
            Assert.Equal(30f, e.AggregatedStats.memorySeconds, 6);
            Assert.Equal(1000f, e.AggregatedStats.sensorRange, 6);   // 同批落地的对照项：灵敏度照旧
        }
    }
}
