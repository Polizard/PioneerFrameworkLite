using System.Collections.Generic;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **B7：256 件规模的纯层读数**——AABB 收集 / 惯量合成 / 无 NaN。
    /// 渲染槽位与游戏内飞行需游戏内验收；这里只锁纯数学与几何成本。
    /// </summary>
    public class Multipart256Bench
    {
        private readonly ITestOutputHelper _out;
        public Multipart256Bench(ITestOutputHelper output) => _out = output;

        [Fact]
        public void AabbAndInertia_256Parts_FiniteAndMeasured()
        {
            const int n = 256;
            var e = new SpaceEntity(1, 0);
            var parts = new List<SpacePart>(n);
            for (int i = 0; i < n; i++)
            {
                var p = new SpacePart
                {
                    partId = i + 1,
                    defName = "PF_ShipPart_Cube",
                    positionShip = new DVector3(0, -i * 2.0, 0),
                    boundsSizeShip = new DVector3(2, 2, 2),
                    hp = 100f,
                };
                p.AddComponent(new Components.HullMassComponent { mass = 100f });
                parts.Add(p);
            }
            e.SetParts(parts);

            e.GetAabbSystem(out DVector3 min, out DVector3 max);
            Assert.True(max.y - min.y > 500.0);
            Assert.True(double.IsFinite(min.x) && double.IsFinite(max.y));

            var contributions = new List<InertiaTensor.PartContribution>(n);
            for (int i = 0; i < n; i++)
            {
                var p = parts[i];
                contributions.Add(new InertiaTensor.PartContribution
                {
                    mass = p.Mass,
                    px = p.positionShip.x,
                    py = p.positionShip.y,
                    pz = p.positionShip.z,
                    cxx = 100.0, cyy = 100.0, czz = 100.0,
                });
            }

            // 预热后取 1000 次均值（Debug 口径，只做相对量级）
            for (int i = 0; i < 10; i++) InertiaTensor.Compose(contributions);
            var sw = Stopwatch.StartNew();
            const int reps = 1000;
            InertiaTensor.InertiaResult r = default;
            for (int i = 0; i < reps; i++) r = InertiaTensor.Compose(contributions);
            sw.Stop();
            Assert.True(r.HasData && r.mass > 0.0 && double.IsFinite(r.comY));
            _out.WriteLine($"[B7 纯层] 256 件：AABB y≈{max.y - min.y:F0} m；惯量合成 {sw.Elapsed.TotalMilliseconds * 1000.0 / reps:F1} µs/次（Debug）");
        }
    }
}
