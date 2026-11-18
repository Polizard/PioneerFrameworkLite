using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    /// <summary>
    /// 环世界材质（2026-10-17 电影感视觉升级重写）。
    ///
    /// 与旧版的区别：旧版全部用 `WorldOverlayTransparent`（unlit）+ 把固定光向量量化成
    /// 4 档 brightness 烘进 material color；现在改为**受光**材质 `PF_RingLit`（真实法线 +
    /// 与行星同源的昼夜混色 + 克制的 rim/高光 + 自发光通道）。见 IMPL §2/§3。
    ///
    /// 纪律：
    /// - 贴图缺失时回退 WhiteTex（沿用旧版 `Load` 的防御模式），保证不黑屏/不抛异常。
    /// - 新 shader 缺失时回退 `WorldOverlayTransparent`（旧观感），只告警一次。
    /// - 每个材质创建后都登记到 `RingWorldVisualState`，由各绘制层每帧刷新 uniform。
    /// </summary>
    // 2026-11-17：有静态 Shader/Texture2D 字段 ⇒ 标记静态构造（主线程）。材质仍是首次使用时创建。
    [Verse.StaticConstructorOnStartup]
    internal static class RingWorldMaterials
    {
        private const string TextureRoot = "World/RingWorld/";
        private const int RingQueue = 3500;   // 与旧版环同一层区间；不透明写深度，不透明排序由深度决定

        private static Texture2D? hullTexture;
        private static Texture2D? hullEmissive;
        private static Texture2D? innerTexture;
        private static Texture2D? innerEmissive;
        private static Texture2D? glowDot;
        private static Shader? litShader;
        private static bool shaderWarned;

        private static Material? habitat;
        private static Material? hull;
        private static Material? frame;
        private static Material? pipe;
        private static Material? warningLight;

        /// <summary>居住面（内侧，工业装甲 + 夜窗自发光）。</summary>
        internal static Material Habitat => habitat ??= Create(
            InnerTexture, InnerEmissive, Color.white, rimStrength: .42f, emissionStrength: 2.6f,
            emissionColor: new Color(.55f, .78f, 1f, 1f));

        /// <summary>环壳与装饰金属共用同一个材质实例，统一贴图、昼夜光照和掩膜自发光。</summary>
        internal static Material HullMaterial => hull ??= Create(Hull, HullEmissive, Color.white,
            rimStrength: .75f, emissionStrength: 2.0f, emissionColor: new Color(.5f, .78f, 1f, 1f));

        internal static Material Structure(RingVisualQuad quad) => quad.Material switch
        {
            RingVisualMaterial.Hull => HullMaterial,
            RingVisualMaterial.Frame => frame ??= Create(Hull, HullEmissive,
                new Color(.66f, .71f, .78f), rimStrength: .9f, emissionStrength: .8f,
                emissionColor: new Color(.45f, .72f, 1f, 1f)),
            RingVisualMaterial.Pipe => pipe ??= Create(Hull, HullEmissive,
                new Color(.34f, .39f, .45f), rimStrength: .55f, emissionStrength: 0f,
                emissionColor: Color.black),
            _ => warningLight ??= Create(BaseContent.WhiteTex, null, Color.black,
                rimStrength: 0f, emissionStrength: 5.0f, emissionColor: new Color(1f, .32f, .045f, 1f)),
        };

        /// <summary>加性光晕/大气带用的径向点图（其他绘制层可复用）。</summary>
        internal static Texture2D GlowDot => glowDot ??= Load("RingGlowDot", TextureWrapMode.Clamp);

        /// <summary>外壳贴图（装饰物层等外部绘制层可复用）。</summary>
        internal static Texture2D Hull => hullTexture ??= Load("RingHull", TextureWrapMode.Repeat);

        internal static Shader? LitShader
        {
            get
            {
                if (litShader != null) return litShader;
                litShader = Shader.Find("PF_RingLit");
                if (litShader != null) return litShader;
                if (!shaderWarned)
                {
                    shaderWarned = true;
                    Log.Warning("[PFL RingWorld] Shader 'PF_RingLit' not found; ring falls back to unlit WorldOverlayTransparent.");
                }
                return litShader = ShaderDatabase.WorldOverlayTransparent;
            }
        }

        private static Material Create(Texture2D texture, Texture2D? emissive, Color color,
            float rimStrength, float emissionStrength, Color emissionColor)
        {
            Shader shader = LitShader!;
            Material material = MaterialPool.MatFrom(texture, shader, color, RingQueue);
            material.SetVector("_RingParams", new Vector4(rimStrength, 2.5f, emissionStrength, .12f));
            material.SetColor("_EmissionColor", emissionColor);
            material.SetColor("_RimColor", new Color(.45f, .62f, .95f, 1f));
            // 必须显式绑定 _EmissionTex：未绑定 sampler 在 Waylith 下不保证是白图。
            // 无自发光图的材质（如警示灯）用白底，让 _EmissionColor × 强度 单独生效。
            material.SetTexture("_EmissionTex", emissive ?? BaseContent.WhiteTex);
            RingWorldVisualState.Register(material);
            return material;
        }

        private static Texture2D InnerTexture => innerTexture ??= Load("RingInnerHull", TextureWrapMode.Repeat);

        // 自发光图与 albedo 共用同一套环向 UV（u 会大于 1 很多），必须 Repeat，
        // 否则 clamp 会把边缘像素拉成整圈光带（2026-10-17 审查修正）。
        private static Texture2D InnerEmissive => innerEmissive ??= Load("RingInnerEmissive", TextureWrapMode.Repeat);

        private static Texture2D HullEmissive => hullEmissive ??= Load("RingHullEmissive", TextureWrapMode.Repeat);

        private static Texture2D Load(string name, TextureWrapMode wrap)
        {
            Texture2D? texture = ContentFinder<Texture2D>.Get(TextureRoot + name, reportFailure: false);
            if (texture == null)
            {
                Log.WarningOnce($"[PFL RingWorld] Texture '{TextureRoot + name}' missing; using WhiteTex fallback.", name.GetHashCode());
                return BaseContent.WhiteTex;
            }
            texture.wrapMode = wrap;
            return texture;
        }
    }
}
