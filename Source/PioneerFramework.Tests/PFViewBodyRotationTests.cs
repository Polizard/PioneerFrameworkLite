using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **自转相位**（PFView 贴图 ↔ 世界视图角度一致性的那一环）的纯核心钉子。
    ///
    /// 背景（2026-10-10 用户实测："世界视图那边 Group 处于陆地上方时，PFView 一侧却不是"）：
    /// PFView 的行星贴图是**天体固连系**的（逐格多边形直接来自 PlanetLayer），飞船位置是 **ECI** 的
    /// ⇒ 画行星必须把贴图按自转相位转过去。这条链条里唯一容易错的是**符号**（踩过：手写 Ry(+ωt)，误差 2ωt 随时间长大）。
    /// 所以判据就一条：**四元数作用 == 唯一权威 `SurfaceFrame.FixedToEci` 的函数作用**（逐向量比）。
    ///
    /// ⚠ **headless 坑（本次踩到）**：断言消息里**不许塞 `Vector3`** —— `Vector3.ToString()` 要
    /// `UnityEngine.SharedInternalsModule`，headless 里抛 `FileNotFoundException`；
    /// 而消息参数是**先求值**的 ⇒ 整条用例会因为"打日志"而红，跟数学对错无关。要打就打分量（x/y/z 是 float，正常）。
    /// </summary>
    public class PFViewBodyRotationTests
    {
        const double EarthOmega = 2.0 * System.Math.PI / 60000.0;   // 地球：1 天 = 60,000 tick（1 tick = 1 物理秒）

        [Fact]
        public void Spin_Quaternion_AgreesWithFixedToEci()
        {
            double[] ticks = { 0.0, 1.0, 1234.5, 15000.0, 30000.0, 60000.0, 123456.0 };
            DVector3[] vecs =
            {
                new DVector3(1, 0, 0), new DVector3(0, 1, 0), new DVector3(0, 0, 1),
                new DVector3(0.3, -0.7, 0.5),
            };

            foreach (double tick in ticks)
            {
                Quaternion q = PFViewBodyRotation.SurfaceFrameRotation(EarthOmega, tick);
                foreach (var v in vecs)
                {
                    Vector3 mine = q * (Vector3)v;
                    Vector3 authority = (Vector3)SurfaceFrame.FixedToEci(v, EarthOmega, tick);
                    float err = (mine - authority).magnitude;
                    Assert.True(err < 1e-3f,
                        string.Format("tick={0}：四元数作用 ({1:F4},{2:F4},{3:F4}) vs 权威 ({4:F4},{5:F4},{6:F4})，差 {7:E2}",
                            tick, mine.x, mine.y, mine.z, authority.x, authority.y, authority.z, err));
                }
            }
        }

        [Fact]
        public void Spin_HalfDayTurnsHalfWay_AndFullDayIsIdentity()
        {
            // 半圈（30,000 s）应把 +X 转到 −X 附近（±1e-4）；整圈回到原位
            Vector3 half = PFViewBodyRotation.SurfaceFrameRotation(EarthOmega, 30000.0) * Vector3.right;
            float halfErr = (half - Vector3.left).magnitude;
            Assert.True(halfErr < 1e-3f, string.Format("半圈应把 +X 转到 −X，实际 ({0:F4},{1:F4},{2:F4})", half.x, half.y, half.z));

            Vector3 v = new Vector3(0.2f, 0.5f, -0.9f);
            Vector3 full = PFViewBodyRotation.SurfaceFrameRotation(EarthOmega, 60000.0) * v;
            float fullErr = (full - v).magnitude;
            Assert.True(fullErr < 1e-3f, string.Format("整圈应回到原方向，实际 ({0:F4},{1:F4},{2:F4})", full.x, full.y, full.z));
        }

        [Fact]
        public void HandWrittenOppositeSign_IsDetected()
        {
            // 反面教材（2026-10-10 真踩过）：手写 Ry(+ωt) = 把 ω 取反 ⇒ 在半数时刻上偏离权威
            double tick = 20000.0;                       // = 1/3 圈 ⇒ 符号错就差 2/3 圈 = 120°
            Quaternion right = PFViewBodyRotation.SurfaceFrameRotation(EarthOmega, tick);
            Quaternion wrong = PFViewBodyRotation.SurfaceFrameRotation(-EarthOmega, tick);

            Vector3 v = (Vector3)SurfaceFrame.FixedToEci(new DVector3(1, 0, 0), EarthOmega, tick);
            Assert.True((right * Vector3.right - v).magnitude < 1e-3f, "正确相位必须对上权威");
            Assert.True((wrong * Vector3.right - v).magnitude > 1.0f,
                "反号相位必须**对不上**权威（这就是那条判据能抓住符号错的原因）");
        }

        [Fact]
        public void RotationFromColumns_RecoversIdentity()
        {
            Quaternion q = PFViewBodyRotation.RotationFromColumns(Vector3.right, Vector3.up, Vector3.forward);
            Assert.True((q * new Vector3(0.4f, -0.2f, 0.8f) - new Vector3(0.4f, -0.2f, 0.8f)).magnitude < 1e-4f,
                "单位矩阵的三列必须还原成单位四元数");
        }

        // ── 自检**取值口径**的回归钉（2026-10-11：假红 494,998 km / 43,454 km）──────────────
        // 病根不是自转，是自检**忘了归一化**：`PlanetLayer.GetTileCenter` 返回的是"layer 半径"长的向量
        // （1 Unity 单位 = 50 km ⇒ 地球层 ≈100、月球层 ≈30），权威侧 `TileToFixedPosition` 里是 `.normalized * R`。

        [Fact]
        public void SurfacePoint_NormalizesLayerSizedTileCenter()
        {
            const double earthRadius = 5000000.0;                 // 地球 5000 km
            Quaternion rot = PFViewBodyRotation.SurfaceFrameRotation(EarthOmega, 1234.5);
            // 地球层格心向量的**真实量级**（layer 半径 = 100 单位）——不是单位向量
            var raw = new Vector3(0.6f, 0.0f, -0.8f) * 100f;      // |raw| = 100，方向仍是单位方向

            DVector3 mine = PFViewBodyRotation.SurfacePointEci(rot, raw, earthRadius);

            // 必须是**地表半径**处的点（修前这里是 100×，于是自检报差 494,998 km = (100−1)×5000 km）
            double rKm = mine.magnitude / 1000.0;
            Assert.True(System.Math.Abs(rKm - 5000.0) < 0.01,
                "自检取的「我这一侧」必须落在天体半径上（修前是 layer 半径倍）：实际 " + rKm.ToString("F2") + " km");
        }

        [Fact]
        public void SurfacePoint_EquivalenceWithAuthorityCaliber_IsExactlyTheRotation()
        {
            // 与权威同口径：mine 应等于"把同一格心方向按 SurfaceFrame 转过去再缩到半径"
            // ⇒ 只差一个旋转作用（这里用同一条旋转 ⇒ 必须恒等）
            const double earthRadius = 5000000.0;
            double tick = 40000.0;
            Quaternion rot = PFViewBodyRotation.SurfaceFrameRotation(EarthOmega, tick);
            var raw = new Vector3(0.0f, 1.0f, 0.0f) * 100f;       // 极点方向（自转不动 ⇒ 好判）

            DVector3 mine = PFViewBodyRotation.SurfacePointEci(rot, raw, earthRadius);
            DVector3 auth = SurfaceFrame.FixedToEci(new DVector3(0.0, earthRadius, 0.0), EarthOmega, tick);

            double errKm = (mine - auth).magnitude / 1000.0;
            Assert.True(errKm < 0.001,
                "自检两侧必须是同一个旋转（极点方向）⇒ 差应 ≈0：实际 " + errKm.ToString("F6") + " km");
        }

        /// <summary>
        /// **参考系档位那一半**（2026-10-12「PFView 参考系按场景选」）：`EciToSurfaceFrameRotation`
        /// 必须是 `SurfaceFrameRotation` 的**逆**，且它自己的作用等于权威 `SurfaceFrame.EciToFixed`
        /// —— 固连档就是把整套场景乘这个旋转（符号同样不许手写）。
        /// </summary>
        [Fact]
        public void EciToFixed_IsTheInverseOf_FixedToEci_AndMatchesTheAuthority()
        {
            double[] ticks = { 0.0, 1.0, 1234.5, 30000.0, 123456.0 };
            DVector3[] vecs =
            {
                new DVector3(1, 0, 0), new DVector3(0, 1, 0), new DVector3(0, 0, 1),
                new DVector3(0.3, -0.7, 0.5),
            };

            foreach (double tick in ticks)
            {
                Quaternion fixedToEci = PFViewBodyRotation.SurfaceFrameRotation(EarthOmega, tick);
                Quaternion eciToFixed = PFViewBodyRotation.EciToSurfaceFrameRotation(EarthOmega, tick);
                foreach (var v in vecs)
                {
                    Vector3 mine = eciToFixed * (Vector3)v;
                    Vector3 auth = (Vector3)SurfaceFrame.EciToFixed(v, EarthOmega, tick);
                    float errAuth = (mine - auth).magnitude;
                    Assert.True(errAuth < 1e-3f,
                        string.Format("EciToSurfaceFrameRotation 的作用必须等于权威 EciToFixed（tick={0} 误差={1}）", tick, errAuth));

                    Vector3 round = eciToFixed * (fixedToEci * (Vector3)v);
                    float errRound = (round - (Vector3)v).magnitude;
                    Assert.True(errRound < 1e-3f,
                        string.Format("两个旋转必须互逆（tick={0} 往返误差={1}）", tick, errRound));
                }
            }
        }
    }
}
