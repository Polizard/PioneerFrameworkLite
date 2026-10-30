using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    public class PFViewQuadGeometryTests
    {
        [Fact]
        public void SettlementQuad_HasUvs_CenterAligned_AndBothWindings()
        {
            PFViewQuadGeometry.Create(out var verts, out var uvs, out var tris);

            Assert.Equal(4, verts.Length);
            Assert.Equal(4, uvs.Length);
            Assert.Equal(12, tris.Length);          // 正反各两片（绕序不赌）

            // ① UV 必须铺满：对角跨度 ≈ √2；全 (0,0) 就是那个"看不见"的 bug
            float span = (uvs[3] - uvs[0]).magnitude;
            Assert.True(span > 1f, "UV 必须铺满整片（对角跨度 ≈ √2），实际 " + span);

            // ② 片中心对应 UV 中心（径向贴图 ⇒ 圆心必须在片心）
            Vector2 uvCenter = (uvs[0] + uvs[3]) * 0.5f;
            Assert.True((uvCenter - new Vector2(0.5f, 0.5f)).magnitude < 1e-5f,
                "UV 中心必须落在片心（否则圆盘偏心）");

            // ③ 所有三角形都必须落在 z = 0 平面上、且法线为 ±Z（两个方向都要有 ⇒ 任一套绕序被剔除都还在）
            bool sawMinusZ = false, sawPlusZ = false;
            for (int t = 0; t < tris.Length; t += 3)
            {
                Vector3 a = verts[tris[t]], b = verts[tris[t + 1]], c = verts[tris[t + 2]];
                Assert.True(Mathf.Abs(a.z) < 1e-6f && Mathf.Abs(b.z) < 1e-6f && Mathf.Abs(c.z) < 1e-6f, "四边形必须共面于 z=0");
                Vector3 n = Vector3.Cross(b - a, c - a).normalized;
                Assert.True(Mathf.Abs(Mathf.Abs(n.z) - 1f) < 1e-4f, "法线必须是 ±Z");
                if (n.z < 0f) sawMinusZ = true; else sawPlusZ = true;
            }
            Assert.True(sawMinusZ && sawPlusZ, "两面都要发（绕序不赌：Cull Back 的 shader 自己挑朝相机那面）");
        }
    }
}
