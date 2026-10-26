using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **把原版的太阳与星星画出来**（2026-10-09，用户："我希望我们把太阳也画出来" /
    /// "别用 PFView 的太阳，我就是说原版的太阳" / "星星也补上"）。
    ///
    /// ## 原版这两样为什么看不见（读码核过）
    /// 它们都被画在**球心外 20 单位**：`GlobalDrawLayer_Sun` 一张 quad（`WorldMaterials.Sun`、尺寸 15、
    /// 局部 `forward*20`）；`GlobalDrawLayer_Stars` **1500 张** quad（`WorldMaterials.Stars`、边长 1–3.8、
    /// 离太阳方向 0.8 以内再缩到 35%、随机自转），两样都铺在**天穹层**。
    /// 而行星半径是 **100 单位** ⇒ 都在**行星肚子里**；天穹相机先画它们，世界相机随后
    /// `clearFlags = Depth` 再画行星 ⇒ **行星永远盖住**。"太空里没有太阳也没有星星"不是漏画，是画在球心里。
    ///
    /// ## 做法：照原版画，只把它们挪出球心
    /// 共同点：**材质与层全用原版的**（`WorldMaterials.Sun/Stars` + `WorldCameraManager.WorldSkyboxLayer`）、
    /// 朝向写法照原版（面片法线 = 局部 +Y ⇒ `LookRotation(Cross(dir, up), dir)`）；
    /// 挂在 `WorldRenderer.DrawWorldLayers()` 的后置 ⇒ **世界视图 + renderWorld 地图的背景模式两条路都覆盖**。
    /// 放天穹层的另一个好处：遮挡靠**渲染顺序**（世界相机清深度后重画行星）而不是材质自己的 ZTest
    /// ⇒ 原版材质拿来直接用不会出错。
    ///
    /// · **太阳**：方向取 `GenCelestial.CurSunPositionInWorldSpace()`（与给行星打光是**同一出处**
    ///   ⇒ 太阳在哪儿、哪半球被照亮永远一致）；站位 = 相机每帧平移的"无限远比"（视差 0、拉远也不掉出画面），
    ///   上限 `min(世界相机 farClip, 天穹相机 farClip) × 0.5`（天穹相机那份才是硬约束）；
    ///   视大小**只用原版自己的数**：尺寸 15 ÷ 2 ÷（最近视距 125 − 原版摆位 20）≈ 4.1°。
    /// · **星星**：分布/数量/尺寸**全照原版**（同款 `Rand.Seed = world.info.Seed` ⇒ 与原版同一片星空的分布），
    ///   整片壳心搬到相机上、半径改成 300 单位 —— 原版"半径 20 配 1–3.8 单位"那个比例（≈2.9°–10.9°）是给
    ///   "贴着球心看"设计的，挪出来会糊成一片大光斑；**边长保持原版 1–3.8 不变**、只把壳放大
    ///   ⇒ 视直径 0.19°–0.73°，像星星。壳心 = 相机（无限远比：视差 0，相机走到哪星空跟到哪），
    ///   整片按 `LookRotation(sunDir)` 旋转 = **原版同款锚定**（星空相对太阳固定，不随行星自转打转）。
    /// </summary>
    internal static class WorldSunBillboard
    {
        /// <summary>原版太阳的尺寸（视图单位）= `GlobalDrawLayer_Sun` 里那张 quad 的边长。</summary>
        private const float VanillaSunQuadSize = 15f;

        /// <summary>原版把那张 quad 摆在**离球心**这么远（`GlobalDrawLayer_Sun`：层原点 + 局部 `forward*20`）。</summary>
        private const float VanillaSunStandoffFromCenter = 20f;

        /// <summary>世界相机最近视距（球心外，视图单位）= `WorldCameraDriver.MinAltitude`（125）。</summary>
        private const float WorldCameraMinDistance = 125f;

        /// <summary>
        /// 原版太阳"照设计看"的**视半径正切** = (尺寸/2) ÷ (最近视距 − 原版摆位) = 7.5 ÷ 105 ≈ 0.0714，即 ≈ 4.1°。
        /// 三个数全部来自原版自己 ⇒ 不另立一套观感。尺寸按 `2 × 距离 × 本值` 给 ⇒ **屏幕上恒定**
        /// （拉远拉近不会缩成一个点），同时仍是"原版那张 quad 看起来该有多大"。
        /// </summary>
        private const float VanillaSunAngularRadiusTan =
            (VanillaSunQuadSize * 0.5f) / (WorldCameraMinDistance - VanillaSunStandoffFromCenter);

        internal static void Draw()
        {
            if (!VanillaSkySuppression.ReplaceEnabled) return;   // 玩家选择"照原版" ⇒ 我们不画（原版那两层照画）
            var cam = Find.WorldCamera;
            if (cam == null) return;

            Vector3 sunDir = GenCelestial.CurSunPositionInWorldSpace();
            if (sunDir.sqrMagnitude < 1e-6f) return;
            sunDir = sunDir.normalized;

            // 站位：天穹相机那份 farClip 是硬约束（原版 7500）；超了它会先被裁掉，我们就白画。
            float farBudget = cam.farClipPlane;
            var skyboxCam = WorldCameraManager.WorldSkyboxCamera;
            if (skyboxCam != null) farBudget = Mathf.Min(farBudget, skyboxCam.farClipPlane);

            float dist = Mathf.Max(farBudget * 0.5f, 200f);   // 下限 200 单位：别落进行星壳（半径 100）
            Vector3 camPos = cam.transform.position;
            Vector3 pos = camPos + sunDir * dist;
            float size = 2f * dist * VanillaSunAngularRadiusTan;

            // plane10 的法线 = 局部 +Y ⇒ 指向相机才是 billboard（写法与原版 DrawQuadTangentialToPlanet 同一句）
            Vector3 toCam = (camPos - pos).normalized;
            Vector3 tangent = Vector3.Cross(toCam, Vector3.up);
            if (tangent.sqrMagnitude < 1e-6f) tangent = Vector3.right;   // 视线与极轴重合（理论罕见）：换个切向
            Quaternion rot = Quaternion.LookRotation(tangent, toCam);

            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, rot, Vector3.one * size),
                WorldMaterials.Sun, WorldCameraManager.WorldSkyboxLayer);
        }
    }

    /// <summary>
    /// 原版那 1500 颗星：数量/分布/尺寸/淡化/锚定全照原版，**只换掉"星点长什么样"**（详见内层注释与
    /// `GetSoftDotTexture` 的说明）。
    /// </summary>
    internal static class WorldStarfield
    {
        /// <summary>星星数量 = `GlobalDrawLayer_Stars` 同款（1500）。</summary>
        private const int StarCount = 1500;

        /// <summary>单颗星的面片边长范围（视图单位）= `GlobalDrawLayer_Stars.StarsDrawSize` 同款。</summary>
        private static readonly FloatRange StarDrawSize = new(1f, 3.8f);

        /// <summary>离太阳方向多近开始缩小（点积阈值）= 原版 `DistToSunToReduceStarSize` 同款。</summary>
        private const float SunFadeStart = 0.8f;

        /// <summary>该处的缩放下限 = 原版同款（1 → 0.35）。</summary>
        private const float SunFadeMinFactor = 0.35f;

        /// <summary>
        /// 星星壳半径（视图单位）：原版是"半径 20 的球面上摆 1–3.8 单位的面片"，本值把壳挪到球外
        /// （远大于行星/地形尺度 = 半径 100 单位，远小于天穹相机 farClip = 原版 7500）；
        /// 面片边长**保持原版 1–3.8 不变** ⇒ 视直径 0.19°–0.73°（1080p / 60° 视场下 ≈3–13 px）。
        /// </summary>
        internal const float ShellRadius = 300f;   // PFView 的星空盒复用同一份星表与壳半径。

        /// <summary>星色：偏冷白（比纯白更像星）——加性混合下就是"亮点"。</summary>
        private static readonly Color StarColor = new(0.90f, 0.93f, 1f);

        // 网格按世界种子缓存（原版也按种子定分布）。**只缓存种子、不扣 World 引用**（换局不占内存）。
        // 每个不同种子留一张 ~6000 顶点的网格；一局一枚，可忽略。
        private static Mesh? cachedMesh;
        private static int cachedSeed = int.MinValue;

        private static Material? starMaterial;
        private static Texture2D? softDot;

        internal static void Draw()
        {
            if (!VanillaSkySuppression.ReplaceEnabled) return;   // 玩家选择"照原版" ⇒ 我们不画（原版那两层照画）
            var cam = Find.WorldCamera;
            var world = Find.World;
            if (cam == null || world == null) return;

            Mesh? mesh = GetOrBuildMesh(world.info.Seed);
            Material? mat = GetStarMaterial();
            if (mesh == null || mat == null) return;

            Vector3 sunDir = GenCelestial.CurSunPositionInWorldSpace();
            Quaternion rot = sunDir.sqrMagnitude < 1e-6f
                ? Quaternion.identity
                : Quaternion.LookRotation(sunDir.normalized);   // 原版同款锚定：星空相对太阳固定

            Graphics.DrawMesh(mesh, Matrix4x4.TRS(cam.transform.position, rot, Vector3.one),
                mat, WorldCameraManager.WorldSkyboxLayer);
        }

        /// <summary>
        /// 星点材质 = **本模自己的一枚软点** + 原版世界叠加（加性、双面）shader。
        ///
        /// ⚠ 为什么不用 `WorldMaterials.Stars`（用户："我就是说原版的太阳" 那条精神 → 星星也先试了原版）：
        /// 2026-10-09 我把原版那张贴图从 `resources.assets` 里**抠出来量过** —— 材质 `Stars` 的 `_MainTex`
        /// 是一张 **1024×1024** 的"天区补丁"：整张里只有 **8 个亮核（3×3 到 8×8 像素）**，亮像素占比 0.0103%。
        /// 也就是说，一颗"星"只占面片宽度的 **0.3%–0.8%** ⇒ 面片要 **250+ 像素宽**才看得见一个点，
        /// 1500 片就是上百次全屏 alpha-test 重绘（性能上不可能）。原版那张图**在任何合理尺寸下都画不出可见星空**，
        /// 所以星点改为代码生成的软点（无新增美术资源），其余（数量/分布/尺寸/淡化/锚定）仍照原版。
        /// </summary>
        internal static Material? GetStarMaterial()
        {
            if (starMaterial != null) return starMaterial;
            var tex = GetSoftDotTexture();
            if (tex == null) return null;
            starMaterial = MaterialPool.MatFrom(tex, ShaderDatabase.WorldOverlayAdditiveTwoSided, StarColor);
            return starMaterial;
        }

        /// <summary>软点纹理（32×32，代码生成）：中心最亮、向外平滑衰减到 0 ⇒ 加性混合下是一颗柔和的星。</summary>
        private static Texture2D? GetSoftDotTexture()
        {
            if (softDot != null) return softDot;

            const int N = 32;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { name = "PF_StarDot" };
            var px = new Color32[N * N];
            const float half = (N - 1) * 0.5f;
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));   // 球状衰减
                    a = a * a * (3f - 2f * a);                                     // 平滑一下，边缘不硬
                    byte v = (byte)Mathf.RoundToInt(a * 255f);
                    px[y * N + x] = new Color32(v, v, v, v);   // RGB 与 A 同曲线 ⇒ 谁乘谁都是软点
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;

            softDot = tex;
            return softDot;
        }

        internal static Mesh? GetOrBuildMesh(int seed)
        {
            if (cachedMesh != null && cachedSeed == seed) return cachedMesh;

            Rand.PushState();
            try
            {
                Rand.Seed = seed;      // 原版同款：同一片星空的分布

                var verts = new Vector3[StarCount * 4];
                var uvs = new Vector2[StarCount * 4];
                var tris = new int[StarCount * 6];

                int vi = 0, ti = 0;
                for (int i = 0; i < StarCount; i++)
                {
                    Vector3 unit = Rand.UnitVector3;
                    Vector3 pos = unit * ShellRadius;

                    float size = StarDrawSize.RandomInRange;
                    // 靠太阳的星缩小（原版同款口径：网格系 +Z 在绘制时被旋到太阳方向）
                    float d = Vector3.Dot(unit, Vector3.forward);
                    if (d > SunFadeStart)
                        size *= GenMath.LerpDouble(SunFadeStart, 1f, 1f, SunFadeMinFactor, d);

                    // 切向基（与原版 `GetTangentsToPlanet` 同式），再绕法线随机自转（原版同款）
                    Vector3 up = Mathf.Abs(Vector3.Dot(unit, Vector3.up)) > 0.999f ? Vector3.right : Vector3.up;
                    Quaternion basis = Quaternion.LookRotation(unit, up);
                    Quaternion q = Quaternion.AngleAxis(Rand.Range(0f, 360f), unit) * basis;
                    Vector3 t1 = q * Vector3.up;
                    Vector3 t2 = q * Vector3.right;

                    float h = size * 0.5f;
                    verts[vi] = pos - t1 * h - t2 * h;
                    verts[vi + 1] = pos - t1 * h + t2 * h;
                    verts[vi + 2] = pos + t1 * h + t2 * h;
                    verts[vi + 3] = pos + t1 * h - t2 * h;
                    uvs[vi] = new Vector2(0f, 0f);
                    uvs[vi + 1] = new Vector2(0f, 1f);
                    uvs[vi + 2] = new Vector2(1f, 1f);
                    uvs[vi + 3] = new Vector2(1f, 0f);

                    // 朝向：壳心在相机上 ⇒ 看得见的是**朝内**那一面；按法线方向定绕序，不赌约定
                    Vector3 n = Vector3.Cross(verts[vi + 1] - verts[vi], verts[vi + 2] - verts[vi]);
                    bool inward = Vector3.Dot(n, -unit) > 0f;
                    if (inward)
                    {
                        tris[ti++] = vi; tris[ti++] = vi + 1; tris[ti++] = vi + 2;
                        tris[ti++] = vi; tris[ti++] = vi + 2; tris[ti++] = vi + 3;
                    }
                    else
                    {
                        tris[ti++] = vi; tris[ti++] = vi + 2; tris[ti++] = vi + 1;
                        tris[ti++] = vi; tris[ti++] = vi + 3; tris[ti++] = vi + 2;
                    }

                    vi += 4;
                }

                var mesh = new Mesh { name = "PF_Starfield" };
                mesh.vertices = verts;
                mesh.uv = uvs;
                mesh.triangles = tris;
                mesh.RecalculateBounds();     // 视锥剔除要用（壳半径 300 ⇒ 包围盒必须盖满整片壳）

                cachedMesh = mesh;
                cachedSeed = seed;
                return mesh;
            }
            finally
            {
                Rand.PopState();
            }
        }
    }

    /// <summary>
    /// **原版那套天空（太阳 + 1500 颗星）什么时候不画** —— 开关 = 玩家设置里的「替换世界视图的太阳与星空」（**默认开**；
    /// 2026-10-11 用户口径：「搞一个开关，让玩家自己决定要不要替换星空和太阳（默认开启）」）。
    ///
    /// 为什么必须成对（补画 ⇄ 停用）：原版这两层都是**为"贴着球心看"设计的**（`GlobalDrawLayer_Sun` 一张 quad、`GlobalDrawLayer_Stars` 1500 张，
    /// 全摆在**球心外 20 单位**），平时只靠**行星壳**挡着；而星球表面那几支 shader 是**单面**（资产实测 `WorldTerrain`/`WorldOcean`/
    /// `WorldIce`/`UngeneratedPlanetParts` 全 `cull = Back`）⇒ **镜头一旦进到壳里，地形整片不画 ⇒ 原版那套天空整片露出来**。
    /// 2026-10-09 那批（修含图编队地图背景 + 新增我们的太阳/星空）把背景相机放到**舱底外侧** ⇒ 船一落地/低空，镜头就落到视图半径 100 以内
    /// ⇒ **原版太阳（视直径 ≈10.7°）+ 我们那枚（8.2°）同框 =「两个太阳」**（用户实测报回）。
    /// ⇒ **开 = 我们补画 + 停用原版两层；关 = 原版照旧、我们不画**（两条腿一个开关，见 `PioneerFrameworkLiteMod`）。
    ///
    /// ⚠ **只关这两层**：其余 `WorldDrawLayer`（地形 / 云 / 河 / 路 / 发光 / 天体球 …）一概不动 —— 判据见 `ShouldSuppress`（只认真类型，含派生）。
    /// ⚠ 只是**不画**（跳过 `Render`），不碰它们的生成/网格（`Regenerate`）⇒ 不影响世界生成与任何数据。
    /// </summary>
    internal static class VanillaSkySuppression
    {
        /// <summary>玩家开关当前值（**未构造设置实例时按默认"开"**）。每帧读，不缓存 ⇒ 设置里改完下一帧生效。</summary>
        internal static bool ReplaceEnabled => PioneerFrameworkLiteMod.Settings?.replaceWorldSky ?? true;

        /// <summary>
        /// 原版层类型 → 我们的替代子类（**只换这两层**；其余一律 null = 不动）。**纯逻辑 ⇒ headless 可测**
        /// （2026-10-15 反射清理 A-14：原为"按类型跳过 Render"的补丁判据，现为"换 def 的 layerType"的判据）。
        /// </summary>
        internal static System.Type? ReplacementLayerTypeFor(System.Type? vanillaLayerType)
        {
            if (vanillaLayerType == typeof(GlobalDrawLayer_Sun)) return typeof(PF_GlobalDrawLayer_Sun);
            if (vanillaLayerType == typeof(GlobalDrawLayer_Stars)) return typeof(PF_GlobalDrawLayer_Stars);
            return null;
        }

        /// <summary>
        /// 开关"关" ⇒ 原版两层照画（子类 `Visible` 里的那一半）。**纯逻辑 ⇒ headless 可测**。
        /// 两条腿一个开关：开 = 我们补画 + 原版两层不画；关 = 原版照旧 + 我们不画。
        /// </summary>
        internal static bool VanillaLayersVisible(bool replaceEnabled) => !replaceEnabled;

        /// <summary>换层是否已核对就位（成功后**单布尔早退**——帧兜底调用的常态代价）。</summary>
        internal static bool LayerTypesSwapped { get; private set; }

        private static bool IsOurReplacement(System.Type? layerType)
            => layerType == typeof(PF_GlobalDrawLayer_Sun) || layerType == typeof(PF_GlobalDrawLayer_Stars);

        /// <summary>
        /// 把两个原版层的 def 类型换成我们的子类（`GlobalWorldDrawLayerDef.worldDrawLayer` 是公开 Type 字段）。
        ///
        /// **主机制 = XML patch**（`1.7/Patches/GlobalWorldDrawLayers_SkySwap.xml`：def 解析期就定型，无时序问题）。
        /// 本方法 = **兜底 + 启动自报**：1.7 实测"只在 `[StaticConstructorOnStartup]` 里改这个字段"**没生效**
        /// （2026-10-16 机制在册自检抓到 def 仍是 `GlobalDrawLayer_Sun`）⇒ 每次启动核对一遍并打一行读数
        /// （共见 N / 已是我们的 M / 本次替换 K；K&gt;0 = patch 没吃到、靠这里救回）。
        /// 帧兜底（`PF_WorldCameraService`，每 60 帧）覆盖"此刻 def 还没定型/被重建"的极端情形；成功后早退。
        ///
        /// ⚠ 层实例在**开世界时**建（`WorldGrid.InitializeGlobalLayers` 读 def 字段）⇒ 既有存档要**重载**
        /// 才换上新实例（机制在册自检里有对应判据："世界层实例"那一节）。
        /// </summary>
        internal static void EnsureLayerTypesSwapped(string phase, bool logAlways = false)
        {
            if (LayerTypesSwapped) return;

            int seen = 0, alreadyOurs = 0, swapped = 0;
            foreach (GlobalWorldDrawLayerDef def in DefDatabase<GlobalWorldDrawLayerDef>.AllDefsListForReading)
            {
                seen++;
                if (IsOurReplacement(def.worldDrawLayer))
                {
                    alreadyOurs++;
                    continue;
                }

                System.Type? replacement = ReplacementLayerTypeFor(def.worldDrawLayer);
                if (replacement != null)
                {
                    def.worldDrawLayer = replacement;
                    swapped++;
                }
            }

            if (swapped > 0 || alreadyOurs > 0)
                LayerTypesSwapped = true;

            if (logAlways || swapped > 0)
            {
                Log.Message($"[PF·天空] 换层核对（{phase}）：共见 {seen} 条 GlobalWorldDrawLayerDef｜已是我们的子类 {alreadyOurs}｜本次替换 {swapped}"
                    + (LayerTypesSwapped ? "" : "（此刻 def 未定型，帧兜底继续）"));
            }
        }
    }

    /// <summary>
    /// 原版太阳层的替代子类（2026-10-15 反射清理 A-14）：开关开 ⇒ 不画（由我们补画，见
    /// `WorldSunBillboard`），开关关 ⇒ 原版照画。**类必须 public**：引擎用
    /// `Activator.CreateInstance(def.worldDrawLayer)` 跨程序集实例化。
    /// </summary>
    public class PF_GlobalDrawLayer_Sun : GlobalDrawLayer_Sun
    {
        public override bool Visible =>
            VanillaSkySuppression.VanillaLayersVisible(VanillaSkySuppression.ReplaceEnabled) && base.Visible;
    }

    /// <summary>原版星空层的替代子类（同 <see cref="PF_GlobalDrawLayer_Sun"/>）。</summary>
    public class PF_GlobalDrawLayer_Stars : GlobalDrawLayer_Stars
    {
        public override bool Visible =>
            VanillaSkySuppression.VanillaLayersVisible(VanillaSkySuppression.ReplaceEnabled) && base.Visible;
    }

    /// <summary>
    /// **我们补画的太阳与星空**（2026-10-15 反射清理 A-14：替代原先挂在
    /// `WorldRenderer.DrawWorldLayers` 上的 Harmony Postfix）。
    /// def 驱动（`Defs/WorldDrawLayerDefs/PF_SkyBillboards.xml` 的 `GlobalWorldDrawLayerDef`），
    /// 绘制主体仍是 `WorldSunBillboard.Draw()` + `WorldStarfield.Draw()`（两者自带"开关关就早退"）；
    /// 层顺序 = def 载入顺序（本模在 Core 之后）⇒ 在原版两层之后、行星层之前。
    /// ⚠ 构造 `dirty = false`：本层不批处理（星壳按种子缓存网格、太阳每帧算），没有可"重建"的批网格。
    /// </summary>
    public class PF_WorldDrawLayer_SkyBillboards : WorldDrawLayerBase
    {
        public PF_WorldDrawLayer_SkyBillboards()
        {
            dirty = false;
        }

        public override bool Visible => VanillaSkySuppression.ReplaceEnabled;

        public override void Render()
        {
            WorldSunBillboard.Draw();
            WorldStarfield.Draw();
        }
    }

    /// <summary>启动时核对（并兜底）A-14 的层类型替换（def 已载入、世界尚未创建/读档 ⇒ 时机正确）。</summary>
    [StaticConstructorOnStartup]
    internal static class SkyLayerTypeSwap
    {
        static SkyLayerTypeSwap()
        {
            VanillaSkySuppression.EnsureLayerTypesSwapped("启动静态构造", logAlways: true);
        }
    }
}
