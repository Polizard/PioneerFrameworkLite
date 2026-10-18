using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **太阳 = 圆片（倒数第二层）· 星空盒（倒数第一层）**。
    ///
    /// 用户 2026-10-11 四条裁定（先讨论后动手）：
    /// 1. **大小 = 角大小**（不是屏幕像素）⇒ `角半径 = atan(Sun.radius / 根行星.orbitRadius)`（`PFViewSunDisc`）；
    /// 2. **只改 PFView**：世界视图那条"太阳就画原版"照旧 ⇒ 两边数值不同是**已知且接受**的
    ///    （PFView 这套按本 mod 自己的数据算：3000 km ÷ 120,000 km ⇒ 半径 ≈ 1.43°，而世界视图那套是原版三个数折算的 4.1°）；
    /// 3. 圆片固定在**倒数第二层**（除星空盒外任何东西都能遮挡它）；星空盒 = **倒数第一层**；
    /// 4. 星空**复用世界视图那套星表**（同种子 ⇒ 同一片星空），且**大气里按「透明度 = 1 − 天空 α」淡出**；
    ///    含图编队地图背景**保持世界口径**（那条走世界相机，不在这里改）。
    ///
    /// 为什么"层次"能这么简单地实现对：两个都是**排在场景之前**的绘制——
    /// 星空盒画序 `RenderQueue.Background`(1000)、太阳盘 `1900`、其余场景从 2000 起 ⇒
    /// **后画的自然盖住先画的**（不写深度、也不做任何远裁剪/深度竞争）⇒ "除星空外谁都能遮挡太阳"是**确定性**的，
    /// 不再依赖"把太阳摆到某个有限距离"那套脆弱假设。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        /// <summary>太阳圆片的摆放距离（视图单位）：固定"远在一切之上"（画序 + 不写深度已保证层次，这里只是取个不在近/远裁剪面上的位置）。</summary>
        private const float SunPlaceDistView = FarClip * 0.4f;

        /// <summary>太阳圆片的画序：星空之上、大气壳之下（⇒ 会被壳蒙一层雾）。口径见 `PFViewLayers`。</summary>
        private const int SunDiscRenderQueue = PFViewLayers.SunDisc;

        private static Material? sunDiscMat;
        private static Texture2D? sunDiscTex;
        private static Mesh? sunDiscMesh;
        private static Mesh? starfieldMesh;
        private static Material? starfieldMat;
        private static Color starfieldBaseColor = Color.white;
        private static int starfieldSeed = int.MinValue;

        /// <summary>本帧的太阳角半径（度）与四边形世界边长（由 `UpdateSunDisc` 每帧写）。</summary>
        private float sunDiscAngularRadiusDeg = PFViewSunDisc.DefaultAngularRadiusDeg;
        private float sunDiscQuadSize = 1f;

        /// <summary>探针限频（-1 = 还没跑过）。</summary>
        private long sunProbeTick = -1;

        /// <summary>
        /// 每帧一次：**太阳圆片的角半径** = `atan(Sun.radius / 根行星.orbitRadius)`（`PFViewSunDisc`）。
        /// "当前行星系环绕太阳的半径"取**根行星**（飞船在月球上时仍取地球 —— 月球绕的是地球，不是太阳）。
        /// </summary>
        private void UpdateSunDisc(CelestialBodyManager mgr)
        {
            var star = mgr.GetStar();
            int shipBodyId = ResolveFocusBodyId();
            int rootId = mgr.GetRootPlanetId(shipBodyId);
            var root = rootId >= 0 ? mgr.GetBodyById(rootId) : null;
            sunDiscAngularRadiusDeg = PFViewSunDisc.AngularRadiusDeg(
                star != null ? star.radius : 0.0,
                root != null ? root.orbitRadius : 0.0);
            // 四边形按"实心芯 = 名义角半径"放大（贴图芯只占半宽 `DiscCoreFraction`；外沿柔边略出一点）
            sunDiscQuadSize = PFViewSunDisc.QuadSizeForDisc(SunPlaceDistView, sunDiscAngularRadiusDeg);
        }

        /// <summary>太阳在视图里的方向（与方向光**同一份** ⇒ 摆位与明暗面永远一致）。</summary>
        private Vector3 SunDiscDir(Vector3 camPos)
        {
            Vector3 d = sunDirectionView;
            if (d.sqrMagnitude < 1e-6f) d = -camPos;
            if (d.sqrMagnitude < 1e-6f) d = Vector3.forward;
            return d.normalized;
        }

        /// <summary>
        /// 读数：**太阳此刻在不在视野里 / 是不是被天体挡住**（2026-10-11 加，随 `[PFView·太阳盘]` 一行打）。
        /// 用途 = 用户报「没看到太阳」时一眼区分三种情况：
        /// ① **视野外**（夹角 &gt; 半视野）⇒ 右拖转镜头（相机永远看向焦点，太阳常在背后 —— 正常）；
        /// ② **被某天体挡住** ⇒ 相机恰好在背光侧、太阳在天体盘面后面（**这是对的**，等于日食位置）；
        /// ③ 两者都不是 ⇒ 那才是渲染真有问题。
        /// </summary>
        private string SunInViewNote(CelestialBodyManager mgr, double tick, DVector3 origin)
        {
            if (renderCamera == null) return "太阳盘：相机未就绪";
            Vector3 camPos = renderCamera.transform.position;
            Vector3 dir = SunDiscDir(camPos);

            float angle = Vector3.Angle(renderCamera.transform.forward, dir);
            float halfV = renderCamera.fieldOfView * 0.5f;
            float aspect = (float)renderCamera.pixelWidth / Mathf.Max(1, renderCamera.pixelHeight);
            float halfH = Mathf.Atan(Mathf.Tan(halfV * Mathf.Deg2Rad) * aspect) * Mathf.Rad2Deg;

            // 被天体挡住？（相机→太阳 与 相机→天体 的夹角 < 该天体在该距离上的角半径）
            string? occludedBy = null;
            if (mgr != null)
            {
                foreach (var b in mgr.GetAllBodies())
                {
                    float bodyR = (float)(b.radius * MetersToView);
                    Vector3 vp = AbsToView(mgr.GetBodyPositionAbs(b.bodyId, tick), origin);
                    Vector3 toBody = vp - camPos;
                    float d = toBody.magnitude;
                    if (d <= bodyR * 1.001f) continue;        // 相机就在这天体上/内部 ⇒ 不算"挡住"
                    float ang = Vector3.Angle(toBody, dir);
                    float bodyAng = Mathf.Asin(Mathf.Clamp01(bodyR / d)) * Mathf.Rad2Deg;
                    if (ang < bodyAng) { occludedBy = b.defName; break; }
                }
            }

            bool inView = angle < Mathf.Min(halfV, halfH) - sunDiscAngularRadiusDeg;   // 扣掉圆盘自身半径
            string state = occludedBy != null
                ? $"**被 {occludedBy} 挡住**（相机在背光侧 ⇒ 属正常）"
                : inView
                    ? "**视野内**（看不见才是问题）"
                    : "视野外（右拖转镜头找它；相机永远看向焦点，太阳常在背后）";
            return $"视线夹角 {angle:F1}°（半视野 竖 {halfV:F0}° / 横 {halfH:F0}°）⇒ {state}";
        }

        /// <summary>
        /// 读数：**这一帧的绘制资源与摆位**（网格 / 材质 / 队列 / 摆位距离 / 世界尺寸）。
        /// 用途 = 把"没画出来"和"画了看不见"一眼分开（2026-10-11 连着踩了两次"看不见太阳"）。
        /// </summary>
        private string SunDiscDrawNote()
        {
            var mesh = GetSunDiscMesh();
            var mat = GetSunDiscMaterial();
            return $"绘制：网格 {(mesh != null ? "OK" : "**缺**")}（UV {(mesh != null ? "OK" : "-")}）· 材质 {(mat != null && mat.shader != null ? mat.shader.name : "**缺**")}（q {(mat != null ? mat.renderQueue : -1)}）· 摆位 {SunPlaceDistView:G3} · 尺寸 {sunDiscQuadSize:G3}";
        }

        /// <summary>
        /// **探针**（限频 300 物理秒；只在"太阳在视野内"时做）：**从渲染目标里读回圆盘处与旁边天空的像素**，
        /// 用数字回答"太阳到底画上了没有"。为什么要有它（2026-10-11）：连着两轮"看不见太阳"
        /// （① 背面剔除，② 网格没 UV ⇒ 全透明），**眼睛看不见时压根分不清"没画"和"画了看不见"**
        /// ⇒ 这条读数把结论直接落在日志里（"看不见"的下一次报告只需带这一行）。
        /// </summary>
        private void ProbeSunDisc(double tick)
        {
            if (renderCamera == null || renderTexture == null) return;
            long now = (long)tick;
            if (sunProbeTick >= 0 && now - sunProbeTick < 300) return;
            sunProbeTick = now;

            Vector3 camPos = renderCamera.transform.position;
            Vector3 vp = renderCamera.WorldToViewportPoint(camPos + SunDiscDir(camPos) * SunPlaceDistView);
            if (vp.z <= 0f) return;                                     // 在相机背后：不探（此时看不见是对的）

            int w = renderCamera.pixelWidth, h = renderCamera.pixelHeight;
            // 取样块 = **整个圆盘**（芯的像素直径）：圆盘画上时芯会填掉块里 ≈π/4 = 0.79 的面积 ⇒
            // 用"亮像素**占比**"当判据（**不是**取最大 —— 一颗星就可能把 max 顶到 1 ⇒ 假绿；星是软点，占比只有千分之几）
            int block = Mathf.Clamp(Mathf.RoundToInt(
                PFViewSunDisc.DiameterPixels(sunDiscAngularRadiusDeg, renderCamera.fieldOfView, h)), 3, 128);
            int cx = Mathf.RoundToInt(vp.x * w), cy = Mathf.RoundToInt(vp.y * h);
            int refX = Mathf.Clamp(cx + block * 2, 0, Mathf.Max(0, w - block));

            float centerFrac = ReadBlockBrightFraction(cx, cy, block, out float centerMean);
            float skyFrac = ReadBlockBrightFraction(refX, cy, block, out float skyMean);

            Log.Message($"[PFView·太阳盘·探针] 圆盘处 ({Mathf.Clamp(cx, 0, Mathf.Max(0, w - block))},{Mathf.Clamp(cy, 0, Mathf.Max(0, h - block))}) {block}px：亮像素占比 {centerFrac:F2}（均红 {centerMean:F2}）↔ 旁边同尺寸块 ({refX},{Mathf.Clamp(cy, 0, Mathf.Max(0, h - block))})：{skyFrac:F2}（均红 {skyMean:F2}）｜{(centerFrac > 0.30f ? "**圆盘在**（实心芯 ≈0.79 的占比；暖白 ⇒ 红通道近 1）"
                                   : centerFrac < 0.05f ? "**没画出来**（或与天空同色；若上一行写着「被 X 挡住」⇒ 那是被挡，正常）"
                                                        : "有点亮但不满 ⇒ 半透明/偏小/只有边缘")}");
        }

        /// <summary>
        /// 读 RT 上一小块像素：**亮像素（红通道 &gt; 0.5）占比** + 平均红通道。
        /// 用"占比"而不是"最大值"：星空是**软点**（一颗星只占几个像素）⇒ 占比对星几乎不敏感，
        /// 而一块实心圆盘会占满 ≈π/4。守卫：矩形必须完全落在 RT 内（否则 Unity 报错）。
        /// </summary>
        private float ReadBlockBrightFraction(int x, int y, int size, out float meanRed)
        {
            meanRed = -1f;
            int w = renderCamera.pixelWidth, h = renderCamera.pixelHeight;
            int x0 = Mathf.Clamp(x, 0, Mathf.Max(0, w - size));
            int y0 = Mathf.Clamp(y, 0, Mathf.Max(0, h - size));
            var prev = RenderTexture.active;
            Texture2D? tmp = null;
            try
            {
                RenderTexture.active = renderTexture;
                tmp = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tmp.ReadPixels(new Rect(x0, y0, size, size), 0, 0);
                tmp.Apply(false, false);
                var pix = tmp.GetPixels32();
                int bright = 0, sum = 0;
                for (int i = 0; i < pix.Length; i++)
                {
                    int r = pix[i].r;
                    sum += r;
                    if (r > 127) bright++;                 // 0.5 阈值（暖白圆盘 ≈250 ⇒ 轻松过线）
                }
                meanRed = pix.Length > 0 ? sum / (float)pix.Length / 255f : -1f;
                return pix.Length > 0 ? bright / (float)pix.Length : -1f;
            }
            catch (System.Exception ex)
            {
                Log.Warning("[PFView·太阳盘·探针] 读回失败：" + ex.Message);
                return -1f;
            }
            finally
            {
                RenderTexture.active = prev;
                if (tmp != null) UnityEngine.Object.Destroy(tmp);
            }
        }

        /// <summary>
        /// 画太阳圆片：**一个朝向相机的四边形**，角半径见 `UpdateSunDisc`，摆在 `SunPlaceDistView` 处。
        /// 材质 = 原版透明 shader（**不写深度**；实测该 shader **Cull Back**）+ 代码生成的**软边圆盘**贴图
        /// ⇒ 层次 = 星空之上、场景之下。
        /// ⚠ **朝向按 `PFViewSunDisc.QuadFacingAxis`**（quadMesh 正面 = −Z ⇒ +Z 背对相机）——
        ///   第一版把 +Z 转向相机 ⇒ 整片被背面剔除 ⇒ 用户症状"有星空、没太阳"（2026-10-11）。
        /// </summary>
        private void DrawSunDisc(Vector3 camPos)
        {
            var mat = GetSunDiscMaterial();
            var mesh = GetSunDiscMesh();
            if (mat == null || mesh == null) return;

            Vector3 pos = camPos + SunDiscDir(camPos) * SunPlaceDistView;
            Vector3 face = PFViewSunDisc.QuadFacingAxis(camPos, pos);
            Quaternion rot = Quaternion.LookRotation(face, PFViewSunDisc.SafeUp(face));
            Graphics.DrawMesh(mesh, Matrix4x4.TRS(pos, rot, Vector3.one * sunDiscQuadSize), mat, SceneLayer);
        }

        /// <summary>
        /// 圆片网格：**自己的四边形（带 UV）** —— 不用 `quadMesh`：**它没 UV**
        /// ⇒ 径向圆盘贴图会整片采到角上的透明 texel ⇒ "画了但看不见"（2026-10-11 踩过，见 `PFViewSunDisc.DiscAlphaAt`）。
        /// </summary>
        private static Mesh? GetSunDiscMesh()
        {
            if (sunDiscMesh != null) return sunDiscMesh;
            try
            {
                PFViewSunDisc.DiscQuadGeometry(out var verts, out var uvs, out var tris);
                var m = new Mesh { name = "PF_SunDiscQuad" };
                m.vertices = verts;
                m.uv = uvs;
                m.triangles = tris;
                m.RecalculateNormals();
                sunDiscMesh = m;
                return m;
            }
            catch (System.Exception ex)
            {
                Log.Warning("[PFView·太阳盘] 圆片网格创建失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>圆片材质：原版透明 shader（不写深度）+ 软边圆盘贴图；画序 1900（星空之上、场景之下）。</summary>
        private static Material? GetSunDiscMaterial()
        {
            if (sunDiscMat != null) return sunDiscMat;
            var tex = GetSunDiscTexture();
            if (tex == null) return null;
            sunDiscMat = new Material(ShaderDatabase.Transparent)
            {
                mainTexture = tex,
                color = new Color(1f, 0.97f, 0.90f, 1f),      // 暖白（原版太阳那味）
                renderQueue = SunDiscRenderQueue,
            };
            return sunDiscMat;
        }

        /// <summary>
        /// 圆盘贴图（128×128，代码生成）：**硬芯 + 软边**（用户要的是"一个圆"，不是"一团光斑"）。
        /// 芯（r ≤ `PFViewSunDisc.DiscCoreFraction` = 0.90）不透明，0.90→1.0 用 smoothstep 收到 0 ⇒ 边缘不出现生硬锯齿。
        /// 摆位侧按同一个常数放大 ⇒ **实心圆盘的角半径 = 名义角半径**。
        /// </summary>
        private static Texture2D? GetSunDiscTexture()
        {
            if (sunDiscTex != null) return sunDiscTex;
            try
            {
                const int N = 128;
                var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { name = "PF_SunDisc" };
                var px = new Color32[N * N];
                const float half = (N - 1) * 0.5f;
                for (int y = 0; y < N; y++)
                {
                    for (int x = 0; x < N; x++)
                    {
                        float dx = (x - half) / half;
                        float dy = (y - half) / half;
                        float a = PFViewSunDisc.DiscAlphaAt(dx, dy);                 // 与摆位/用例同一个口径
                        px[y * N + x] = new Color32((byte)(255f * a), (byte)(247f * a), (byte)(230f * a), (byte)(255f * a));
                    }
                }
                tex.SetPixels32(px);
                tex.Apply();
                tex.wrapMode = TextureWrapMode.Clamp;
                sunDiscTex = tex;
                return tex;
            }
            catch (System.Exception ex)
            {
                Log.Warning("[PFView·太阳盘] 圆盘贴图创建失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// **倒数第一层：星空盒**（用户裁定：复用世界视图那套星表 ⇒ **同种子 = 同一片星空**；
        /// 大气里「透明度 = 1 − 天空 α」淡出）。
        /// · 星表/软点/加性材质口径全部取自 `WorldStarfield`（材质**克隆**一份：每帧按亮度缩放颜色，不能动共享份）；
        /// · 壳心搬到 **PFView 的相机**；锚定用**惯性系的太阳方向**（PFView 是惯性视角 ⇒ 星星不随行星自转打转；
        ///   世界视图那边锚的是固连系太阳方向 —— 各自与自己的太阳一致 ✓）；
        /// · 整片按 `PFViewSunDisc.StarShellScale` **等比放大**（PFView 近裁剪面可到 5000 单位 ⇒ 壳必须挪到它之外；等比 ⇒ 星点角大小不变）；
        /// · 画序 `RenderQueue.Background`(1000) ⇒ 在太阳圆片（1900）与整个场景之前 = **谁都能挡它、它挡不住谁**。
        /// </summary>
        private void DrawStarfield()
        {
            if (renderCamera == null) return;
            var world = Find.World;
            if (world == null) return;

            if (starfieldSeed != world.info.Seed)
            {
                starfieldMesh = WorldStarfield.GetOrBuildMesh(world.info.Seed);
                starfieldSeed = world.info.Seed;
            }
            if (starfieldMesh == null) return;

            if (starfieldMat == null)
            {
                var shared = WorldStarfield.GetStarMaterial();
                if (shared == null) return;
                starfieldMat = new Material(shared) { renderQueue = PFViewLayers.Starfield };
                starfieldBaseColor = starfieldMat.color;                  // 记下基色（之后按亮度缩放它）
            }

            float brightness = Mathf.Clamp01(1f - skyAlpha);               // 进大气淡出（天空吃满 α 时看不见星）
            starfieldMat.color = new Color(starfieldBaseColor.r * brightness, starfieldBaseColor.g * brightness,
                starfieldBaseColor.b * brightness, 1f);

            float scale = PFViewSunDisc.StarShellScale(renderCamera.nearClipPlane, WorldStarfield.ShellRadius);
            Vector3 camPos = renderCamera.transform.position;
            Quaternion rot = Quaternion.LookRotation(SunDiscDir(camPos), Vector3.up);
            Graphics.DrawMesh(starfieldMesh, Matrix4x4.TRS(camPos, rot, Vector3.one * scale), starfieldMat, SceneLayer);
        }
    }
}
