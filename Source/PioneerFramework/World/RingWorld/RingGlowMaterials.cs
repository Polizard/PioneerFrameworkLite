using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    /// <summary>
    /// 环「辉光层」材质（2026-10-17 电影感视觉升级，task-6 / IMPL §2.3、§5.1）。
    ///
    /// 为什么单独一个文件：`WorldDrawLayer_RingGlow` 需要三块**加性**内容——边缘光（rim）、
    /// 环带上下大气薄带（ribbon）、警示灯光斑（sprite）。它们共用同一个 shader `PF_RingGlow`，
    /// 只靠 material uniform `_GlowParams.x`（mode）区分；材质由本类集中创建、集中登记到
    /// `RingWorldVisualState`（每帧 uniform 由各绘制层在 Render() 开头刷新）。
    ///
    /// 纪律（IMPL §2.3/§5.1）：
    /// - `Shader.Find("PF_RingGlow")` 取不到 ⇒ 本层整体跳过，只 `Log.Warning` 一次（不抛异常、不黑屏）。
    /// - 贴图 `World/RingWorld/RingGlowDot` 取不到 ⇒ 回退 `BaseContent.WhiteTex`（与 `RingWorldMaterials.Load` 同一防腐模式）。
    /// - renderQueue 固定 3550（在受光主体 3500 之后、深度测试开、深度写关，引擎按 ShaderDef 的 additive 配置绘制）。
    /// - 三个材质只在首次使用时创建，之后复用；每个都 `RingWorldVisualState.Register`。
    /// </summary>
    // 2026-11-17：有静态 Shader/Texture2D/Material 字段 ⇒ 标记静态构造（主线程）。材质仍是首次使用时创建。
    [Verse.StaticConstructorOnStartup]
    internal static class RingGlowMaterials
    {
        private const int GlowQueue = 3550;
        private const string GlowDotPath = "World/RingWorld/RingGlowDot";

        private static Shader? glowShader;
        private static bool shaderWarned;
        private static Texture2D? glowDot;

        private static Material? rim;
        private static Material? ribbon;
        private static Material? sprite;

        /// <summary>mode 0：沿真法线的菲涅尔边缘光（复用壳体全部四边形）。</summary>
        internal static Material? Rim => rim;

        /// <summary>mode 1：环带上下两条大气薄带（UV.y 做软边）。</summary>
        internal static Material? Ribbon => ribbon;

        /// <summary>mode 2：警示灯 camera-independent 方形光斑（顶点色 a 存确定性相位）。</summary>
        internal static Material? Sprite => sprite;

        /// <summary>
        /// 确保三个材质就绪。返回 false 表示当前拿不到 `PF_RingGlow` shader——调用方（绘制层）应整体跳过。
        /// 幂等：已创建的直接复用；Shader.Find 只告警一次，之后每次 Regenerate 可重试（shader 可能在 Defs 加载后才注册）。
        /// </summary>
        internal static bool TryEnsureMaterials()
        {
            if (rim != null && ribbon != null && sprite != null) return true;
            Shader? shader = GlowShader;
            if (shader == null) return false;

            // mode, intensity, falloff, pulse 的取值见 IMPL §5.1；2026-10-17 用户反馈"荧光更强"后整体上调约 1.5–1.7×。
            rim ??= Create(shader, mode: 0f, intensity: 1.9f, falloff: 2.2f, pulse: 0f,
                glowColor: new Color(.45f, .68f, 1f, 1f));
            ribbon ??= Create(shader, mode: 1f, intensity: 1.35f, falloff: 1.9f, pulse: .18f,
                glowColor: new Color(.50f, .76f, 1f, 1f));
            sprite ??= Create(shader, mode: 2f, intensity: 2.40f, falloff: 2.4f, pulse: 1.35f,
                glowColor: Color.white); // sprite 的橙色来自顶点色，_GlowColor 用白避免二次染色
            return rim != null && ribbon != null && sprite != null;
        }

        private static Shader? GlowShader
        {
            get
            {
                if (glowShader != null) return glowShader;
                glowShader = Shader.Find("PF_RingGlow");
                if (glowShader == null && !shaderWarned)
                {
                    shaderWarned = true;
                    Log.Warning("[PFL RingWorld] Shader 'PF_RingGlow' not found; WorldDrawLayer_RingGlow is skipped.");
                }
                return glowShader;
            }
        }

        private static Material Create(Shader shader, float mode, float intensity, float falloff, float pulse,
            Color glowColor)
        {
            Material material = MaterialPool.MatFrom(GlowDot, shader, glowColor, GlowQueue);
            material.SetColor("_GlowColor", glowColor);
            material.SetVector("_GlowParams", new Vector4(mode, intensity, falloff, pulse));
            material.SetTexture("_GlowTex", GlowDot);
            RingWorldVisualState.Register(material);
            return material;
        }

        private static Texture2D GlowDot => glowDot ??= LoadGlowDot();

        private static Texture2D LoadGlowDot()
        {
            Texture2D? texture = ContentFinder<Texture2D>.Get(GlowDotPath, reportFailure: false);
            if (texture == null)
            {
                Log.WarningOnce(
                    $"[PFL RingWorld] Texture '{GlowDotPath}' missing; using WhiteTex fallback for RingGlow sprites.",
                    GlowDotPath.GetHashCode());
                return BaseContent.WhiteTex;
            }
            // 光斑/薄带是屏幕向的软衰减，禁止 wrap 采样避免边缘接缝。
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }
    }
}
