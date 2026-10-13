using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 天体表面的**两层**（2026-10-10 用户裁定："我们只烘焙有 tile 覆盖的区域，其他的部分用基底填充"）。
    ///
    /// · **基底球**：无数据区 / 无补丁的天体 —— 背景 biome 贴图 6×3 平铺（原版对"未生成区"的 `UngeneratedPlanetParts`
    ///   是同一口径）。它**不再吃烘焙图**：烘焙产出的是数据区里那张高密度补丁，见下。
    /// · **数据区补丁**：有烘焙补丁时，在基底球之上再画一层（第二张网格 + 补丁图）。
    ///   网格顶点由 `PFViewSurfaceBake.PatchDir` 生成 ⇒ 与烘焙图的映射**互为逆**，错位在构造上不可能；
    ///   两者还共用**同一个 TRS 矩阵**（顶点都在"半径 0.5 的方向空间"里）⇒ 远景等比挪位、LOD、姿态那套换系自动一致。
    ///
    /// 历史：本文件原来还装着"近档：可见分格补丁"（逐格多边形 + 可见帽/视锥裁剪 + 重建去抖 + 覆盖帽处理）。
    /// 用户 2026-10-10 裁定**不论远近均用烘焙贴图、取消档位** ⇒ 那半块整件退役，
    /// 归档在 `Source/已弃置/PFViewTiles-近档分格-20261010.cs`（含取回办法）。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        /// <summary>补丁网格的环数（半径方向）。</summary>
        private const int PatchRings = 64;

        /// <summary>补丁网格的扇区数（方位方向）。</summary>
        private const int PatchSectors = 128;

        // ── 基底球材质：**帽外 / 无补丁**的那部分由它顶上 ──
        private readonly Dictionary<int, Material> baseSphereMaterials = new Dictionary<int, Material>();
        private readonly HashSet<int> baseSphereLogged = new HashSet<int>();

        private Material? GetBaseSphereMaterial(CelestialBodyDef body, PlanetLayer? layer)
        {
            if (baseSphereMaterials.TryGetValue(body.bodyId, out var cached) && cached != null) return cached;

            // 口径（2026-10-11，按用户指示收口）：
            //   ① **Def 里给了 `baseSphereColor` ⇒ 就用它**（"在天体定义里加一个基底球渲染颜色"）——
            //      和烘焙的清屏色（盘缘）走**同一个**转换 ⇒ 基底球与盘缘必然同色，不再靠"猜哪张贴图"；
            //   ② 没给才退回"水格最多的 biome 的贴图"（不是 `backgroundBiome` 那张 —— 那张是给世界着色器做调制的**细节图**）。
            string baseLabel;
            Material mat;
            if (body.baseSphereColor.a > 0f)
            {
                // ⚠⚠ 2026-10-11 修：**必须走"1×1 贴图 + NewSurfaceMaterial"这条路**，不能直接设 `Material.color` ——
                //    绘制那条路（`DrawCelestialSphere`）会在 MaterialPropertyBlock 里**覆盖 `_Color`**，
                //    材料上设的颜色会被抹掉（上一版就因此变成**纯白**：没有贴图、`_Color` 又被覆盖成白 ⇒ 白 × 无 = 白）。
                //    走 1×1 贴图后：采样 = sRGB→线性（与补丁贴图同一语义）+ `_Color = SurfaceReflectance` + 夜面自发光 ⇒
                //    **与烘焙补丁是同一套材料结构**（用户口径："颜色和材料（反射率吸收率）都要对齐"）✓
                var flatTex = GetFlatColorTexture(body);
                if (flatTex == null) return null;
                mat = NewSurfaceMaterial(flatTex);
                baseLabel = string.Format("Def.baseSphereColor 显示色 ({0:F2},{1:F2},{2:F2}) → 1×1 贴图（与补丁同款材质）",
                    body.baseSphereColor.r, body.baseSphereColor.g, body.baseSphereColor.b);
            }
            else
            {
                string texLabel = "（无层）";
                Texture2D? tex = layer != null ? PFViewSurfaceBake.ResolveBaseTexture(layer, out texLabel) : null;
                if (tex == null) return null;         // 取不到 ⇒ 调用方照旧走纯色
                mat = NewSurfaceMaterial(tex);
                mat.mainTextureScale = new Vector2(6f, 3f);   // 世界贴图按 6×3 平铺
                baseLabel = string.Format("水 biome 贴图 {0}（6×3 平铺，sRGB={1}）｜{2}", tex.name, tex.isDataSRGB, texLabel);
            }
            baseSphereMaterials[body.bodyId] = mat;

            if (baseSphereLogged.Add(body.bodyId))
                Log.Message(string.Format("[PFView·基底球] body={0} 口径 = {1}｜activeColorSpace={2}",
                    body.defName, baseLabel, QualitySettings.activeColorSpace));
            return mat;
        }

        // ── 数据区补丁（烘焙产出）：材质 + 网格，按 `PFViewSurfaceBake.Version` 失效 ──
        private readonly Dictionary<int, Material> patchMaterials = new Dictionary<int, Material>();
        private readonly Dictionary<int, Mesh> patchMeshes = new Dictionary<int, Mesh>();
        private readonly HashSet<int> patchLogged = new HashSet<int>();
        private int patchVersion = -1;

        /// <summary>
        /// 取该天体的补丁（网格 + 材质）。没有烘焙补丁 ⇒ false（调用方只画基底球）。
        /// ⚠ `layer` 必需：球冠是从 layer 的格心反推的（旁挂 PNG 里没有球冠信息）。
        /// </summary>
        private bool TryGetSurfacePatch(CelestialBodyDef body, PlanetLayer? layer, out Mesh? mesh, out Material? mat)
        {
            mesh = null;
            mat = null;
            if (layer == null) return false;

            // 补丁图换了（重烤 / 从旁挂加载 / 切世界）⇒ 丢掉本体的网格与材质缓存重建。
            if (patchVersion != PFViewSurfaceBake.Version)
            {
                DestroySurfacePatch();
                patchVersion = PFViewSurfaceBake.Version;
            }

            if (patchMeshes.TryGetValue(body.bodyId, out var cachedMesh) && cachedMesh != null
                && patchMaterials.TryGetValue(body.bodyId, out var cachedMat) && cachedMat != null)
            {
                mesh = cachedMesh;
                mat = cachedMat;
                return true;
            }

            if (!PFViewSurfaceBake.EnsureReady(body.bodyId, layer)) return false;
            if (!PFViewSurfaceBake.TryGetPatch(body.bodyId, out var tex, out Vector3 center, out float thetaMaxRad)
                || tex == null) return false;

            var newMesh = BuildSurfacePatchMesh(center, thetaMaxRad);
            var newMat = NewSurfaceMaterial(tex);
            newMat.mainTextureScale = Vector2.one;        // 补丁图与网格 UV 一一对应，不再平铺
            patchMeshes[body.bodyId] = newMesh;
            patchMaterials[body.bodyId] = newMat;

            if (patchLogged.Add(body.bodyId))
                Log.Message(string.Format("[PFView·补丁] body={0} 球冠半角 {1:F1}° · 网格 {2} 环×{3} 扇 · 贴图 {4}×{4} · sRGB={5}",
                    body.defName, thetaMaxRad * Mathf.Rad2Deg, PatchRings, PatchSectors, tex.width, tex.isDataSRGB));
            mesh = newMesh;
            mat = newMat;
            return true;
        }

        /// <summary>
        /// 造补丁网格：极坐标网格铺在球冠上，顶点 = `PFViewSurfaceBake.PatchDir`（与烘焙图同一映射）。
        /// 半径外扩 ε = `PatchEpsilon`（**必须大于网格弦高**，否则环心会缩到基底球里面去）。
        /// 三角形**双面都发**：绕序不赌（同色 + 不混合 ⇒ 无副作用），这是"标准材质剔背面"那条老坑的正面用法。
        /// </summary>
        private static Mesh BuildSurfacePatchMesh(Vector3 center, float thetaMaxRad)
        {
            PFViewSurfaceBake.TileTangentFrame(center, out Vector3 t1, out Vector3 t2);
            float eps = 1f + PFViewSurfaceBake.PatchEpsilon(thetaMaxRad, PatchRings);

            int rings = PatchRings, sectors = PatchSectors;
            int vertCount = 1 + rings * (sectors + 1);
            var verts = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];

            verts[0] = center * 0.5f * eps;
            normals[0] = center;
            uvs[0] = new Vector2(0.5f, 0.5f);

            int vi = 1;
            for (int r = 1; r <= rings; r++)
            {
                float rr = (float)r / rings;
                for (int s = 0; s <= sectors; s++)
                {
                    float phi = 2f * Mathf.PI * s / sectors;
                    var uv = new Vector2(0.5f + 0.5f * rr * Mathf.Cos(phi), 0.5f + 0.5f * rr * Mathf.Sin(phi));
                    Vector3 dir = PFViewSurfaceBake.PatchDir(uv, center, t1, t2, thetaMaxRad);
                    verts[vi] = dir * 0.5f * eps;
                    normals[vi] = dir;
                    uvs[vi] = uv;
                    vi++;
                }
            }

            var tris = new List<int>(sectors * rings * 12);
            // 中心扇（环 1）
            for (int s = 0; s < sectors; s++)
            {
                int a = 1 + s;
                tris.Add(0); tris.Add(a); tris.Add(a + 1);
                tris.Add(0); tris.Add(a + 1); tris.Add(a);
            }
            // 环间四边形
            for (int r = 1; r < rings; r++)
            {
                int a = 1 + (r - 1) * (sectors + 1);
                int b = 1 + r * (sectors + 1);
                for (int s = 0; s < sectors; s++)
                {
                    tris.Add(a + s); tris.Add(b + s); tris.Add(a + s + 1);
                    tris.Add(a + s + 1); tris.Add(b + s); tris.Add(b + s + 1);
                    tris.Add(a + s); tris.Add(a + s + 1); tris.Add(b + s);
                    tris.Add(a + s + 1); tris.Add(b + s + 1); tris.Add(b + s);
                }
            }

            var mesh = new Mesh { name = "PF_SurfacePatchMesh" };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// 地表材质的**反照率标定**（2026-10-10 用户口径："天体反射率太高了，我们看看实际上的地球是怎么个反射率"）。
        ///
        /// 账：场景方向光 `sunLight.intensity = 5`（RimWorld 的既有口径，船体/浮雕全靠它，不动），
        /// 而物理正确的着色是 `render = albedo × 1.0 × NdotL` ⇒ 地表材质必须把整体乘回 **1/5**。
        /// 之前是 `color = 白` + 自发光 0.55 ⇒ 亮面 ≈ `贴图 × (5·NdotL + 0.55)` = **最亮处是贴图本色的 5.5 倍**
        /// ⇒ 过曝发白（就是"反射率太高"的读数）。现在 `color = 0.20` ⇒ 亮面 ≈ 贴图本色。
        ///
        /// 真实地球的锚点（写在这里免得下次又去查）：
        /// · **Bond 反照率 ≈ 0.306**（全向平均：约 30% 入射阳光被反射回太空）· 几何反照率 ≈ 0.43；
        /// · 分表面：洋面 **0.06** · 森林 0.08–0.15 · 沙漠 0.35–0.40 · 冰雪 0.6–0.9 · 云顶 0.7–0.9。
        /// ⚠ 我们的贴图是**世界地图色**（不是反照率贴图；洋面贴图约 (0.23,0.28,0.35) 远亮于真实洋面 0.06）
        /// ⇒ 这里做的是"把整体拉回物理量级"的标定，不是逐表面反照率重建；想更暗就再乘一个系数。
        /// </summary>
        private const float SurfaceReflectance = 0.20f;

        /// <summary>
        /// 绘制基底球时传给 `DrawCelestialSphere` 的 tint。**它会被写进 MaterialPropertyBlock 覆盖 `_Color`** ⇒
        /// 必须与 `SurfaceReflectance` 同值（覆盖 = 材料值 ⇒ 两条路才一致）。做成常量而不是就地写白，是为了防止再漂。
        /// </summary>
        private static readonly Color SurfaceTint = new Color(SurfaceReflectance, SurfaceReflectance, SurfaceReflectance, 1f);

        /// <summary>
        /// 夜面余辉（自发光）的强度。原来是 0.55（那时没有大气，夜面死黑只能靠自发光顶）；
        /// 现在夜空由**大气表现**负责（天空盒/壳），这里只留一点点地板值 ⇒ 夜面接近真实地球（暗），
        /// 等"城市灯光"那一件落地后再由它点灯。给 0 = 纯物理（夜面全黑）。
        /// </summary>
        private const float SurfaceNightEmission = 0.05f;

        /// <summary>Def 纯色基底球用的 **1×1 sRGB 贴图**缓存（运行时创建 ⇒ 与材质同一条清理纪律）。</summary>
        private readonly Dictionary<int, Texture2D> flatColorTextures = new Dictionary<int, Texture2D>();

        /// <summary>
        /// 把 Def 的 `baseSphereColor`（**显示色**）烘成一张 1×1 贴图：写进去的就是这些字节，
        /// 采样时 Unity 按 sRGB 解码 —— 与补丁贴图（烘焙 PNG，sRGB）**同一语义** ⇒ 两者亮度/色调口径一致。
        /// </summary>
        private Texture2D? GetFlatColorTexture(CelestialBodyDef body)
        {
            if (flatColorTextures.TryGetValue(body.bodyId, out var cached) && cached != null) return cached;
            try
            {
                var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "PF_BaseFlat_" + body.bodyId };
                t.SetPixel(0, 0, new Color(body.baseSphereColor.r, body.baseSphereColor.g, body.baseSphereColor.b, 1f));
                t.Apply();
                t.wrapMode = TextureWrapMode.Clamp;
                flatColorTextures[body.bodyId] = t;
                return t;
            }
            catch (Exception ex)
            {
                Log.Warning("[PFView·基底球] 1×1 纯色贴图创建失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 造一份"贴图 + 低自发光"的表面材质（基底球 / 补丁共用）。
        /// ⚠ 只用 Standard（`renderMat`）不用 `ToonMat`：地形贴图那条路已验证 `_MainTex` 生效，
        /// 而 `PF_Toon` 是否采样主贴图仍未验证；两条路同链，风格才不打架。
        /// </summary>
        private static Material NewSurfaceMaterial(Texture2D tex)
        {
            var mat = new Material(renderMat)
            {
                color = new Color(SurfaceReflectance, SurfaceReflectance, SurfaceReflectance, 1f),
                mainTexture = tex,
            };
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);

            if (mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetTexture("_EmissionMap", tex);
                mat.SetColor("_EmissionColor", new Color(SurfaceNightEmission, SurfaceNightEmission, SurfaceNightEmission));
            }
            return mat;
        }

        /// <summary>
        /// 销毁表面层的运行时资源（基底球材质 + 补丁材质/网格），并**把补丁图从显存里放掉**
        /// （能重新读回来的才放：`PFViewSurfaceBake.UnloadReloadable` —— 收起 PFView 就不该扣着 12 MiB/天体）。
        /// 缓存下次取用时按 `PFViewSurfaceBake.Version` 自动重建；补丁图按需从旁挂 PNG 重新加载。
        /// </summary>
        private void DestroySurfaceResources()
        {
            foreach (var kv in baseSphereMaterials)
                if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            baseSphereMaterials.Clear();
            baseSphereLogged.Clear();

            foreach (var kv in flatColorTextures)
                if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            flatColorTextures.Clear();

            DestroySurfacePatch();
            PFViewSurfaceBake.UnloadReloadable();
        }

        private void DestroySurfacePatch()
        {
            foreach (var kv in patchMaterials)
                if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            patchMaterials.Clear();
            foreach (var kv in patchMeshes)
                if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            patchMeshes.Clear();
            patchLogged.Clear();
        }
    }
}
