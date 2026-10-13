using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// PFView 地表**烘焙**这条线的口径钉子。
    ///
    /// 2026-10-10 用户裁定"只烘焙有 tile 覆盖的区域，其他的部分用基底填充" ⇒ 烘焙从"整球等距圆柱"
    /// 改成"数据区球冠补丁（方位等距）"。于是新增三条必须钉住的不变量：
    /// ① 球冠能**从格心反推**（不读 `PlanetCoverage` 那类配置 ⇒ 每颗天体的层都成立）；
    /// ② `PatchUv` 与 `PatchDir` **互为逆**（补丁网格与烘焙图是这两条各自生成的 ⇒ 它们一致性 = 不错位）；
    /// ③ 补丁网格的径向外扩必须**大于网格弦高**（否则环心缩进基底球里，贴图一片片透出来）。
    ///
    /// 原 `PFViewTerrainTiersTests`（档位判据 + 可见帽 + 自转换系那一批）随"近档分格"整件归档
    /// （`Source/已弃置/PFViewTiles-近档分格-20261010.cs`）—— 那些判据已无生产调用者。
    /// `TileTangentFrame` 那条仍留着：现在它同时是"格内贴图 UV"和"补丁球冠正交基"的**唯一出处**。
    /// </summary>
    public class PFViewSurfaceBakeTests
    {
        // ════════════════════════════════════════════════════
        //  切平面基（两条用途共用的唯一出处）
        // ════════════════════════════════════════════════════

        static void AssertFrame(Vector3 center)
        {
            PFViewSurfaceBake.TileTangentFrame(center, out var t1, out var t2);
            Vector3 n = center.normalized;

            Assert.True(Mathf.Abs(t1.magnitude - 1f) < 1e-4f, "t1 不是单位向量：" + t1.magnitude);
            Assert.True(Mathf.Abs(t2.magnitude - 1f) < 1e-4f, "t2 不是单位向量：" + t2.magnitude);
            Assert.True(Mathf.Abs(Vector3.Dot(t1, n)) < 1e-3f, "t1 不垂直于格法线");
            Assert.True(Mathf.Abs(Vector3.Dot(t2, n)) < 1e-3f, "t2 不垂直于格法线");
            Assert.True(Mathf.Abs(Vector3.Dot(t1, t2)) < 1e-3f, "t1/t2 不互相正交");
        }

        [Fact]
        public void Frame_IsOrthonormal_AtEquatorAndPoles()
        {
            AssertFrame(new Vector3(1f, 0f, 0f));          // 赤道
            AssertFrame(new Vector3(0.6f, 0.2f, -0.5f));   // 一般位置
            AssertFrame(new Vector3(0f, 1f, 0f));          // 北极（触发 up 兜底分支）
            AssertFrame(new Vector3(0f, -1f, 0f));         // 南极
        }

        [Fact]
        public void Frame_DegenerateCenter_FallsBackToUp()
        {
            // 退化输入不许抛、也不许给零向量（调用方会拿去归一化）
            PFViewSurfaceBake.TileTangentFrame(Vector3.zero, out var t1, out var t2);
            Assert.True(t1.magnitude > 0.9f && t2.magnitude > 0.9f);
        }

        // ════════════════════════════════════════════════════
        //  球冠反推（从格心 ⇒ 中心 + 半角）
        // ════════════════════════════════════════════════════

        /// <summary>在一个轴周围撒一圈"冠内"方向（纯算术 ✓ 不用 Quaternion 那族 ECall）。</summary>
        static List<Vector3> SyntheticCap(Vector3 axis, float halfAngleDeg, int count, int seed)
        {
            axis = axis.normalized;
            Vector3 helper = Mathf.Abs(axis.y) > 0.9f ? Vector3.right : Vector3.up;
            Vector3 t1 = (helper - axis * Vector3.Dot(helper, axis)).normalized;
            Vector3 t2 = Vector3.Cross(t1, axis).normalized;

            var rnd = new System.Random(seed);
            var dirs = new List<Vector3>(count);
            for (int i = 0; i < count; i++)
            {
                // 冠内均匀撒点：cosθ 在 [cos(half), 1] 上均匀（球面均匀），方位角均匀
                float cosT = Mathf.Lerp(Mathf.Cos(halfAngleDeg * Mathf.Deg2Rad), 1f, (float)rnd.NextDouble());
                float theta = Mathf.Acos(Mathf.Clamp(cosT, -1f, 1f));
                float phi = (float)(rnd.NextDouble() * 2.0 * System.Math.PI);
                Vector3 d = axis * Mathf.Cos(theta)
                          + (t1 * Mathf.Cos(phi) + t2 * Mathf.Sin(phi)) * Mathf.Sin(theta);
                dirs.Add(d.normalized);
            }
            return dirs;
        }

        [Fact]
        public void Cap_FromTileCenters_FindsAxisAndHalfAngle()
        {
            Vector3 axis = new Vector3(0.31f, 0.62f, -0.72f).normalized;   // 随便一个轴（≈ 真实那个 36°N/-90°E 的同类）
            var dirs = SyntheticCap(axis, 30f, 400, 20261010);

            Assert.True(PFViewSurfaceBake.ComputeCap(dirs, out Vector3 center, out float halfDeg));
            Assert.True(Vector3.Dot(center, axis) > 0.999f, "球冠中心偏了：" + Vector3.Dot(center, axis));
            Assert.True(Mathf.Abs(halfDeg - 30f) < 2f, "半角偏了：" + halfDeg);
        }

        [Fact]
        public void Cap_DegenerateInputs_ReturnFalse()
        {
            Assert.False(PFViewSurfaceBake.ComputeCap(new List<Vector3>(), out _, out _));
            // 方向恰好对消（没有可辨的中心）
            Assert.False(PFViewSurfaceBake.ComputeCap(
                new List<Vector3> { Vector3.forward, Vector3.back }, out _, out _));
            // 全零向量 = "格心没生成"，不许据此报一个假的中心
            Assert.False(PFViewSurfaceBake.ComputeCap(
                new List<Vector3> { Vector3.zero, Vector3.zero }, out _, out _));
        }

        // ════════════════════════════════════════════════════
        //  补丁映射（PatchUv ↔ PatchDir）
        // ════════════════════════════════════════════════════

        /// <summary>与 `BuildSphereMesh` 逐字同一算式：球面 uv ⇒ 方向（球网格 UV 口径的唯一出处）。</summary>
        static Vector3 SphereDir(float u, float v)
            => new Vector3(
                Mathf.Sin(Mathf.PI * v) * Mathf.Cos(2f * Mathf.PI * u),
                Mathf.Cos(Mathf.PI * v),
                Mathf.Sin(Mathf.PI * v) * Mathf.Sin(2f * Mathf.PI * u));

        [Fact]
        public void PatchUv_CenterIsMiddle_RimIsDiskEdge()
        {
            Vector3 center = new Vector3(0f, 1f, 0f);              // 极区也要成立（旧版等距圆柱在这里会糊）
            PFViewSurfaceBake.TileTangentFrame(center, out var t1, out var t2);
            const float thetaMax = 54f * Mathf.Deg2Rad;

            Assert.True(Vector3.Distance(
                PFViewSurfaceBake.PatchUv(center, center, t1, t2, thetaMax), new Vector2(0.5f, 0.5f)) < 1e-4f,
                "冠心必须是图心");

            // 半角方向上 ⇒ 盘边（半径 0.5）
            Vector3 rim = (center * Mathf.Cos(thetaMax) + t1 * Mathf.Sin(thetaMax)).normalized;
            Vector2 rimUv = PFViewSurfaceBake.PatchUv(rim, center, t1, t2, thetaMax);
            Assert.True(Mathf.Abs(rimUv.x - 1f) < 1e-3f, "盘边 x 不对：" + rimUv.x);
            Assert.True(Mathf.Abs(rimUv.y - 0.5f) < 1e-3f, "盘边 y 不对：" + rimUv.y);

            // 半径随夹角单调
            float prev = -1f;
            for (int i = 0; i <= 10; i++)
            {
                float th = thetaMax * i / 10f;
                Vector3 d = (center * Mathf.Cos(th) + t2 * Mathf.Sin(th)).normalized;
                Vector2 uv = PFViewSurfaceBake.PatchUv(d, center, t1, t2, thetaMax);
                float r = Mathf.Sqrt((uv.x - 0.5f) * (uv.x - 0.5f) + (uv.y - 0.5f) * (uv.y - 0.5f));
                Assert.True(r > prev - 1e-5f, "半径不单调：" + r + " vs " + prev);
                prev = r;
            }
        }

        [Fact]
        public void Patch_MapsRoundTrip_AndKeepsCapInsideDisk()
        {
            // 用一个真实量级：球冠中心 = 球面 uv(0.25,0.30) 那个方向（≈ 36°N/‑90°E 的同类），半角 54° + 2° 余量
            Vector3 axis = SphereDir(0.25f, 0.30f);
            var dirs = SyntheticCap(axis, 54f, 600, 20261011);

            Assert.True(PFViewSurfaceBake.ComputeCap(dirs, out Vector3 center, out float halfDeg));
            PFViewSurfaceBake.TileTangentFrame(center, out var t1, out var t2);
            float thetaMax = (Mathf.Min(halfDeg, 178f) + PFViewSurfaceBake.PatchMarginDeg) * Mathf.Deg2Rad;

            Assert.True(Vector3.Distance(
                PFViewSurfaceBake.PatchUv(axis, center, t1, t2, thetaMax), new Vector2(0.5f, 0.5f)) < 0.03f,
                "球冠轴应落在图心附近");

            foreach (var d in dirs)
            {
                Vector2 uv = PFViewSurfaceBake.PatchUv(d, center, t1, t2, thetaMax);
                float dx = (uv.x - 0.5f) * 2f, dy = (uv.y - 0.5f) * 2f;

                // ① 数据区内的格心**必须**落在盘内（烘焙自检 1 的同一判据）
                Assert.True(dx * dx + dy * dy <= 1.0001f, "格心跑到盘外了：r²=" + (dx * dx + dy * dy));

                // ② uv ⇒ dir 必须回到原方向（补丁网格 = PatchDir ⇒ 网格与图不错位）
                Vector3 back = PFViewSurfaceBake.PatchDir(uv, center, t1, t2, thetaMax);
                Assert.True(Vector3.Dot(back, d) > 0.99999f, "往返失配：" + Vector3.Dot(back, d));
            }
        }

        [Fact]
        public void SurfaceStacking_PatchHugsTheGround_BaseSinksForDepthMargin()
        {
            // 2026-10-11 用户实测："**低飞 Group 视角朝向地表时烘焙渲染会消失**"。
            // 病根：上一批为压过深度分辨率把**补丁抬到 6e-3（地球 30 km）** ⇒ 低飞（< 30 km）的相机跑到了补丁下面
            // ⇒ 朝下看到的是基底球。定死分工：**补丁贴地（小抬升）**、**基底球下沉（买深度余量）**。
            const float earthThetaMax = 0.977f;                       // 54° + 2° 余量
            const float planetDistView = 5e5f;                        // 行星在 5e5 单位（5000 km）
            const float closestCamDist = 30f;                          // 取景限制的最近一档 = 300 m
            const float earthRadiusView = 5e5f;

            float eps = PFViewSurfaceBake.PatchEpsilon(earthThetaMax, 64);
            float shellFactor = PFViewAtmosphere.ShellRadiusFactor(5000000.0, 100000.0);   // = 1.01

            // ① 补丁抬升必须小到"低飞不会钻到它下面"（地球 ≤ 10 km；空天飞机通常也在 10 km 以上才叫低飞）
            Assert.True(eps <= 2e-3f, "补丁抬升必须小（地球 ≤ 10 km），否则低飞相机会钻到补丁下面：" + eps);
            Assert.True(eps < shellFactor - 1f, "补丁抬升必须小于大气壳的抬升，否则补丁会顶穿壳：" + eps);

            // ② 深度余量由**基底球下沉**买：下沉量必须明显大于行星处的 δz（最贴近取景那一档）
            float deltaZ = PFViewAtmosphere.DepthResolution(planetDistView, closestCamDist * 0.35f);
            float sink = PFViewSurfaceBake.BaseSinkFactor * earthRadiusView;
            Assert.True(sink > deltaZ * 2f,
                string.Format("基底下沉必须 > 2×δz（sink={0:F0} 单位 · δz={1:F0} 单位）", sink, deltaZ));
            Assert.True(PFViewSurfaceBake.BaseSinkFactor < 0.02f, "下沉别大到肉眼可辨（< 2% 半径）");
        }

        [Fact]
        public void PatchEpsilon_ExceedsMeshSag_AndNeverZero()
        {
            foreach (float halfDeg in new[] { 5f, 20f, 54f, 90f })
            {
                float thetaMax = halfDeg * Mathf.Deg2Rad;
                float eps = PFViewSurfaceBake.PatchEpsilon(thetaMax, 64);
                float step = thetaMax / 64f;
                float sag = 1f - Mathf.Cos(step * 0.5f);       // 网格弦高（相对半径）
                Assert.True(eps > sag * 3f, string.Format("外扩不够（{0}°）：eps={1:E2} sag={2:E2}", halfDeg, eps, sag));
            }
            Assert.True(PFViewSurfaceBake.PatchEpsilon(0.01f, 64) > 0f, "极小冠也不许给 0 外扩（会与基底球共面打架）");
        }
    }
}
