using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using RimWorld.Planet;
using PioneerFramework.Launch;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 的**大气表现**（2026-10-10 用户口径：
    /// 「摄像机越深入天空盒越蔚蓝」+「按你说的搞日照」+「壳画在 XML 定义的大气层厚度的一半」+
    /// 「分层：天空吃满 α，物体吃 α × 0.3 的雾」）。
    ///
    /// 三件各自在哪：
    /// · **天空** = PFView 相机自己的 `clear color`（这个场景的"天空盒"就是它）⇒ 每帧按 α 混向 `atmosphereColor`。
    ///   为什么用 clear color 而不是整帧叠色：**天空要满 α、物体只要 0.3**（用户口径）—— 换个方式就得做像素掩码，不值当。
    /// · **壳** = 半径 `R + 厚度/2` 的**双面半透明球**（复用现成球网格），受光 ⇒ 昼夜分界与星球同一出处、免费。
    /// · **雾** = 整帧一层 `α × 0.3` 的大气色（`DrawAtmosphereHaze`，挂在画面之上、HUD 之下）。
    ///
    /// ⚠ 纯表现：不动大气线（L1）的任何物理（阻力/包线/掩码/准入都读 ρ₀/H/厚度，不读颜色），
    ///   也不进烘焙（天空与相机高度相关，静态图里表达不了）。
    /// ⚠ 太阳方向**只用** `sunLight`（PFView 场景那盏方向光）—— 与星球明暗面同一出处，不另算太阳。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        /// <summary>深空底色（无大气时的"天空盒"）。`EnsureResources` 也用它。</summary>
        private static readonly Color SpaceBackdropColor = new(0.005f, 0.005f, 0.03f);

        /// <summary>
        /// 本帧**不画壳**的天体 id（-1 = 都画）。规则（用户 2026-10-10）：
        /// 「大气那个薄层不应该在观察的 Group 位于薄层下时显示，只需要按照高度调整天空盒的颜色就好了」
        /// —— 人已经在大气里了，再套一个"从外面看的球壳"是错的；此时**只有天空盒随高度变蓝**。
        /// 判据：**观察中的编队**的高度 &lt; 壳的高度（= 大气厚度的一半）。
        /// </summary>
        private int shellHiddenBodyId = -1;

        /// <summary>当前帧的天空 α 与色（由 `UpdateAtmosphereSky` 每帧写；雾层与 HUD 读它）。</summary>
        private float skyAlpha;
        private Color skyColor = Color.white;

        /// <summary>限频日志用：上次记录的"高度带"（每 5 km 一档）与天体。</summary>
        private int skyLoggedBand = int.MinValue;
        private int skyLoggedBody = -2;
        private bool skySunlitLogged;

        /// <summary>壳的网格（受光半球；静态资源，与 `sphereMeshLOD*` 同寿；不随窗口销毁）。</summary>
        private static Mesh? atmosphereDayCapMesh;
        /// <summary>昼侧壳材质（键 = bodyId）。</summary>
        private readonly Dictionary<int, Material> shellMaterials = new();
        /// <summary>夜侧壳材质（同上；只差一层更淡的 alpha —— 用户口径"半透明壳覆盖全球"）。</summary>
        private readonly Dictionary<int, Material> shellNightMaterials = new();
        private readonly HashSet<int> shellLogged = new();

        /// <summary>
        /// 每帧一次：算出"相机在哪颗天体的气里、气有多浓"，写进 `skyAlpha/skyColor` 并更新天空盒颜色。
        /// 相机高度取**真位置**（`FarClip` 外被等比挪位的天体在这里天然不可能是"在气里"，因为挪位只在极远处发生）。
        /// </summary>
        private void UpdateAtmosphereSky(CelestialBodyManager mgr, double tick, DVector3 origin)
        {
            if (renderCamera == null) return;

            Vector3 camPos = renderCamera.transform.position;
            float bestAlpha = 0f;
            Color bestColor = SpaceBackdropColor;
            int bestBody = -1;
            float bestAltKm = 0f;
            float bestSunlit = 0f;
            float bestDistView = 0f;

            foreach (var body in mgr.GetAllBodies())
            {
                if (body == null) continue;
                if (body.atmosphereThickness <= 0.0 || body.atmosphereRho0 <= 0.0 || body.atmosphereScaleHeight <= 0.0)
                    continue;                                    // 没大气（月球）⇒ 天空不变蓝、不画壳

                Vector3 bodyPos = AbsToView(mgr.GetBodyPositionAbs(body.bodyId, tick), origin);
                float radiusView = Mathf.Max((float)(body.radius * MetersToView), MinBodyVisRadius);
                float bodyDistView = Vector3.Distance(camPos, bodyPos);
                float altMeters = (bodyDistView - radiusView) / MetersToView;
                float sunlit = PFViewAtmosphere.SunlitFactor(camPos - bodyPos, -sunLightForward);
                float a = PFViewAtmosphere.SkyAlpha(altMeters, body.atmosphereThickness, body.atmosphereRho0,
                    body.atmosphereScaleHeight, sunlit);
                if (a > bestAlpha)
                {
                    bestAlpha = a;
                    bestColor = body.atmosphereColor;
                    bestBody = body.bodyId;
                    bestAltKm = altMeters / 1000f;
                    bestSunlit = sunlit;
                    bestDistView = bodyDistView;
                }
            }

            skyAlpha = bestAlpha;
            skyColor = bestColor;
            renderCamera.backgroundColor = Color.Lerp(SpaceBackdropColor, bestColor, bestAlpha);

            // 壳的可见性：**观察中的编队若在壳下（大气里）就不画壳**（天空盒照旧按高度变蓝）。
            shellHiddenBodyId = -1;
            var watched = GetFocusGroup();
            if (watched != null)
            {
                var wb = mgr.GetBodyById(watched.CelestialBodyId);
                if (wb != null && wb.atmosphereThickness > 0.0)
                {
                    Vector3 gView = AbsToView(GetGroupPosAbs(watched, tick), origin);
                    Vector3 bView = AbsToView(mgr.GetBodyPositionAbs(wb.bodyId, tick), origin);
                    float rView = Mathf.Max((float)(wb.radius * MetersToView), MinBodyVisRadius);
                    float groupAltMeters = (Vector3.Distance(gView, bView) - rView) / MetersToView;
                    if (PFViewAtmosphere.ShellHiddenForGroupAltitude(groupAltMeters, wb.atmosphereThickness))
                        shellHiddenBodyId = wb.bodyId;
                }
            }

            // ── 限频日志：跨"高度带"（5 km）或换天体时才打一行 —— 数值对不对不用眼睛（离线可判的那半）──
            int band = bestBody < 0 ? int.MinValue : Mathf.FloorToInt(bestAltKm / 5f);
            if (band != skyLoggedBand || bestBody != skyLoggedBody || (bestSunlit > 0f) != skySunlitLogged)
            {
                skyLoggedBand = band;
                skyLoggedBody = bestBody;
                skySunlitLogged = bestSunlit > 0f;
                if (bestBody < 0)
                {
                    Log.Message("[PFView·大气] 相机不在任何大气里（天空 = 深空色）");
                }
                else
                {
                    var b = mgr.GetBodyById(bestBody);
                    // 深度余量读数（2026-10-10"壳一闪一闪"的判据）：δz 与该处"壳离地高度"的比值，< 1 就是抢深度。
                    float deltaZ = PFViewAtmosphere.DepthResolution(bestDistView,
                        renderCamera != null ? renderCamera.nearClipPlane : 0.01f);
                    float shellOffset = b != null ? (float)(b.atmosphereThickness * 0.5) * MetersToView : 0f;
                    float margin = deltaZ > 1e-6f && shellOffset > 0f ? shellOffset / deltaZ : float.MaxValue;
                    Log.Message($"[PFView·大气] body={(b != null ? b.defName : bestBody.ToString())} 相机 h={bestAltKm:F1} km｜日照 {bestSunlit:F2} ⇒ 天空 α={bestAlpha:P1}（物体雾 {bestAlpha * PFViewAtmosphere.ObjectHazeFactor:P1}）· 壳半径 ×{(b != null ? PFViewAtmosphere.ShellRadiusFactor(b.radius, b.atmosphereThickness) : 1f):F3}（= R + {(b != null ? b.atmosphereThickness * 0.5 / 1000.0 : 0.0):F0} km）· 深度 δz≈{deltaZ * 10f / 1000f:F1} km（余量 ×{// view 单位 → 米（×10）→ km
                        margin:F1}）");
                }
            }
        }

        /// <summary>方向光的 forward = "太阳 → 天体"（`UpdateLighting` 就是那么写的）⇒ 相机朝太阳 = 取反。</summary>
        private Vector3 sunLightForward => sunLight != null ? sunLight.transform.forward : Vector3.forward;

        /// <summary>
        /// 大气壳：半径 = `R + 大气厚度/2`（XML 定义）· **覆盖全球**（受光半球 + 夜半球都画）。
        /// 口径演化：22:56"厚度一半的半透明壳" ⇒ 23:08"（首版不透明）改成只画受光半球" ⇒
        /// **23:2x 用户："我觉得半透明大气壳覆盖全球也对"** ⇒ 半球网格画两次：朝太阳的用昼侧材质、
        /// 背太阳的用夜侧更淡的材质（alpha = `PFViewAtmosphere.NightShellAlpha`；给 0 = 夜侧没有壳，给同值 = 不分昼夜）。
        /// **日照是几何、不是 shader 属性**（不依赖任何可能被剥掉的 shader 变体）。
        /// </summary>
        private void DrawAtmosphereShell(CelestialBodyDef body, Vector3 renderPos, float renderR, Vector3 toSun)
        {
            if (body.bodyId == shellHiddenBodyId) return;   // 人已经在壳下（大气里）⇒ 不画壳，只靠天空盒（用户口径）
            if (atmosphereDayCapMesh == null || body.atmosphereThickness <= 0.0) return;
            Material? dayMat = GetAtmosphereShellMaterial(body, false);
            Material? nightMat = GetAtmosphereShellMaterial(body, true);
            if (dayMat == null) return;

            float shellR = Mathf.Max(renderR, MinBodyVisRadius)
                         * PFViewAtmosphere.ShellRadiusFactor(body.radius, body.atmosphereThickness);
            Vector3 sun = toSun.sqrMagnitude > 1e-9f ? toSun.normalized : Vector3.up;

            // 网格的极在 +Y ⇒ 转到"相机→太阳"= 受光半球；转到反向 = 夜半球（两个半球拼起来就是整球）
            Matrix4x4 dayMtx = Matrix4x4.TRS(renderPos, Quaternion.FromToRotation(Vector3.up, sun), Vector3.one * shellR * 2f);
            Graphics.DrawMesh(atmosphereDayCapMesh, dayMtx, dayMat, SceneLayer);
            if (nightMat != null)
            {
                Matrix4x4 nightMtx = Matrix4x4.TRS(renderPos, Quaternion.FromToRotation(Vector3.up, -sun), Vector3.one * shellR * 2f);
                Graphics.DrawMesh(atmosphereDayCapMesh, nightMtx, nightMat, SceneLayer);
            }
        }

        /// <summary>
        /// 壳材质：**原版自己的透明 shader**（`ShaderDatabase.Transparent` = `Map/Transparent`，原版画一切半透明覆盖层都用它）。
        ///
        /// ⚠⚠ 2026-10-10 用户实测报回："**你怎么整了一个不透明的大气壳子？**" —— 上一版用的是 Standard 的"透明（Fade）模式"
        /// （`_Mode=3` + `_SrcBlend/_DstBlend` + `_ALPHABLEND_ON`）。病根：Standard 的 `_ALPHABLEND_ON` 是
        /// **`shader_feature`**（不是 `multi_compile`）⇒ 变体只在"构建里有材质用到它"时才编进去，而 RimWorld 没人用 ⇒
        /// **变体被剥掉 ⇒ 关键字不生效、fragment 输出的 alpha 被丢 ⇒ 混合算式退化成 `src*1+dst*0` = 不透明**。
        /// ⇒ 教训：**别把透明性押在"运行期切 shader 模式"上**；要透明就用天生透明的 shader。
        /// "日照"同样不再依赖 shader（改成几何：只画受光半球，见 `DrawAtmosphereShell`）。
        /// </summary>
        private Material? GetAtmosphereShellMaterial(CelestialBodyDef body, bool night)
        {
            var cache = night ? shellNightMaterials : shellMaterials;
            if (cache.TryGetValue(body.bodyId, out var cached) && cached != null) return cached;
            if (night && PFViewAtmosphere.NightShellAlpha <= 0f) return null;   // 夜侧给 0 ⇒ 干脆不画那半

            Color c = body.atmosphereColor;
            c.a = night ? PFViewAtmosphere.NightShellAlpha : PFViewAtmosphere.ShellAlpha;

            var mat = new Material(ShaderDatabase.Transparent)
            {
                color = c,
                mainTexture = BaseContent.WhiteTex,      // 没贴图也要给张白的：这类 shader 是 tex2D(_MainTex)*_Color
                // ⚠ 画序由 `PFViewLayers` 统一裁决（**1950 = 排在行星之前**）：行星随后不透明覆盖壳的盘面部分
                //   ⇒ "壳 vs 行星"不进深度比较 ⇒ 球缘那圈不再逐帧翻面（原来 2900=Transparent 时那圈一直闪）。
                //   代价：盘面那层 18% 蓝雾没了（见 `PFViewLayers` 的注释；想换回来 = 把这个数改回 2900）。
                renderQueue = PFViewLayers.AtmosphereShell,
            };
            cache[body.bodyId] = mat;

            if (!night && shellLogged.Add(body.bodyId))
                Log.Message($"[PFView·大气壳] body={body.defName} shader={(mat.shader != null ? mat.shader.name : "?")}（昼 alpha {PFViewAtmosphere.ShellAlpha:F2} / 夜 alpha {PFViewAtmosphere.NightShellAlpha:F2}）· 覆盖全球的两半球");
            return mat;
        }

        /// <summary>
        /// 壳用的网格：**受光半球**（球面 lat ∈ [0, 90°]，极在 +Y）+ **双面**（三角形正反各发一遍）。
        /// 双面两条理由：① 绕序不赌（仓库老坑："日志说画了 N 个、屏幕全空"）；
        /// ② 球缘那圈自然叠**两层**（正面 + 背面）⇒ 比正对镜头处浓一倍 = **大气辉光贴边**，不用 shader 做 fresnel。
        /// </summary>
        private static Mesh BuildDoubleSidedDayCapMesh(int lats, int longs)
        {
            int half = lats / 2;                                   // 只取上半球（lat 0 = +Y 极，lat half = 赤道）
            int verts = (half + 1) * (longs + 1);
            var v = new Vector3[verts];
            var n = new Vector3[verts];
            var uv = new Vector2[verts];
            int vi = 0;
            for (int lat = 0; lat <= half; lat++)
            {
                float phi = Mathf.PI * lat / lats;
                float y = Mathf.Cos(phi);
                float r = Mathf.Sin(phi);
                for (int lon = 0; lon <= longs; lon++)
                {
                    float theta = 2f * Mathf.PI * lon / longs;
                    Vector3 p = new Vector3(r * Mathf.Cos(theta), y, r * Mathf.Sin(theta));
                    v[vi] = p * 0.5f;
                    n[vi] = p;
                    uv[vi] = new Vector2((float)lon / longs, (float)lat / half);
                    vi++;
                }
            }

            int quads = half * longs;
            var tris = new int[quads * 12];                        // 每格两个三角形 × 双面
            int ti = 0;
            for (int lat = 0; lat < half; lat++)
            {
                for (int lon = 0; lon < longs; lon++)
                {
                    int a = lat * (longs + 1) + lon;
                    int b = a + longs + 1;
                    tris[ti++] = a; tris[ti++] = a + 1; tris[ti++] = b;
                    tris[ti++] = a + 1; tris[ti++] = b + 1; tris[ti++] = b;
                    tris[ti++] = a; tris[ti++] = b; tris[ti++] = a + 1;          // 反面
                    tris[ti++] = a + 1; tris[ti++] = b; tris[ti++] = b + 1;
                }
            }

            var mesh = new Mesh { name = "PF_AtmosphereDayCap" };
            mesh.vertices = v;
            mesh.normals = n;
            mesh.uv = uv;
            mesh.triangles = tris;
            // 显式给白色顶点色：万一透明 shader 会读 COLOR 语义（没给颜色时行为不保证），这里也不出意外
            var colors = new Color32[v.Length];
            for (int i = 0; i < colors.Length; i++) colors[i] = new Color32(255, 255, 255, 255);
            mesh.colors32 = colors;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// 物体吃到的雾：整帧叠一层大气色，α = 天空 α × 0.3（用户口径"分层"）。
        /// 位置刻意在**画面之上、HUD 之下** —— HUD 是仪表，不该被窗外的雾罩住。
        /// </summary>
        private void DrawAtmosphereHaze(Rect inRect)
        {
            if (skyAlpha <= 0f) return;
            float a = skyAlpha * PFViewAtmosphere.ObjectHazeFactor;
            if (a <= 0.002f) return;
            GUI.color = new Color(skyColor.r, skyColor.g, skyColor.b, a);
            GUI.DrawTexture(inRect, BaseContent.WhiteTex);
            GUI.color = Color.white;
        }

        /// <summary>销毁壳材质（运行时创建 ⇒ 与表面材质同一条清理纪律）；网格是静态资源，不销毁。</summary>
        private void DestroyAtmosphereShellMaterials()
        {
            foreach (var kv in shellMaterials)
                if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            shellMaterials.Clear();
            foreach (var kv in shellNightMaterials)
                if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            shellNightMaterials.Clear();
            shellLogged.Clear();
        }
    }
}
