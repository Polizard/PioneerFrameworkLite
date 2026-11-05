using UnityEngine;
using Verse;

namespace PioneerFramework.ShipSkin
{
    /// <summary>
    /// 舰船蒙皮的运行时资源（2026-10-20）：一个 `PF_ShipSkin` 材质 + image2 工业装甲贴图。
    ///
    /// 设计约束（用户裁定）：
    ///   · **不改几何/不加 UV**：三平面投影在 shader 里完成，现有盒体/地图浮雕直接套；
    ///   · 只有**一个**材质实例（阵营差靠 `MaterialPropertyBlock._Color` 按批给）⇒ 不增加批次；
    ///   · Shader 取不到（旧版本/编译失败）⇒ `Available == false`，调用方回到 PFLit/Toon 旧路径，
    ///     不抛异常、不黑屏（与本库既有的 shader 回退纪律一致）。
    ///
    /// ⚠ 与 PFLit 同坑①：材质是独立实例，**收不到** `renderMat` 的逐帧 `_SunDir/_SunColor` 刷新，
    ///   所以 `ApplyLighting` 必须每帧由 PFView 调用（另带相机位置给 rim 用）。
    /// ⚠ 坑②（2026-10-19 实机事故）：`Material.mainTexture` 只绑 `_MainTex`，而本 shader 的采样器叫
    ///   `_SkinTex` ⇒ 首版贴图**从未被绑上**，采到引擎默认值（白）⇒ 无论怎么改 PNG 画面都不变。
    ///   必须用 `SetTexture("_SkinTex", …)` 显式绑定；并在调试窗显示"贴图是否加载"现场读数。
    /// </summary>
    internal static class ShipSkinAssets
    {
        /// <summary>一张贴图覆盖的物理尺寸（米）：16 m ⇒ 8 m 大板 / 4 m 次级板（300 m 机位可读）。</summary>
        internal const float PanelTileMeters = 16f;

        /// <summary>板面细节保真（1 = 保留全部板缝/倒角；0 = 压成纯色）。</summary>
        internal const float PlateDetail = 1f;

        /// <summary>轮廓 rim 强度（很克制，只做剪影分离）。</summary>
        internal const float RimStrength = 0.08f;

        /// <summary>浅板缝的着色高度（米）；只扰动法线，不改变几何。</summary>
        internal const float BumpHeightMeters = 0.025f;

        /// <summary>调试用高频板缝的贴图周期（米）：2 m 一块 ⇒ 贴图若生效会一眼看到密集板缝。</summary>
        private const float DebugHighFrequencyTileMeters = 2f;

        private const string TexturePath = "PFView/Ships/ShipSkinPlates";

        private static bool tried;
        private static Material? material;
        private static string? failure;
        private static bool textureLoaded;
        private static bool debugHighFrequency;

        /// <summary>蒙皮材质是否可用（false ⇒ 调用方回退旧材质）。</summary>
        internal static bool Available => material != null;

        /// <summary>共享材质（null = 不可用）。调用方**不要销毁**它（生命周期在本类）。</summary>
        internal static Material? Material => material;

        /// <summary>上一次失败原因（日志用；成功时为空）。</summary>
        internal static string? Failure => failure;

        /// <summary>调试窗现场读数：当前蒙皮资源状态（一眼判断"是没加载还是没生效"）。</summary>
        internal static string StatusText
        {
            get
            {
                if (material == null)
                    return "回退旧材质（" + (failure ?? "未初始化") + "）";
                return "PF_ShipSkin｜贴图=" + (textureLoaded ? "OK" : "缺失→白图")
                    + "｜板格=" + (debugHighFrequency ? DebugHighFrequencyTileMeters : PanelTileMeters) + "m";
            }
        }

        /// <summary>贴图是否真的加载到（调试窗/日志用）。</summary>
        internal static bool TextureLoaded => textureLoaded;

        /// <summary>
        /// 初始化（幂等）。板面周期采用物理米；每次绘制显式提供舰体的米→视图换算。
        /// 返回 true = 蒙皮可用。
        /// </summary>
        internal static bool EnsureInitialized()
        {
            if (tried) return material != null;
            tried = true;

            Shader? shader = Shader.Find("PF_ShipSkin");
            if (shader == null)
            {
                failure = "Shader.Find(\"PF_ShipSkin\") = null";
                Log.Warning("[PFView·蒙皮] " + failure + " ⇒ 回退 PFLit/Toon 旧材质");
                return false;
            }

            Texture2D? texture = ContentFinder<Texture2D>.Get(TexturePath, reportFailure: false);
            textureLoaded = texture != null;
            if (texture != null)
            {
                // 引擎映射为 MirroredRepeat：保留原始 UV 导数与跨边界过滤，避免接缝和远景闪动。
                texture.wrapMode = TextureWrapMode.Mirror;
                texture.filterMode = FilterMode.Trilinear;
                texture.anisoLevel = 4;
            }
            else
            {
                failure = "贴图缺失：" + TexturePath;
                Log.Warning("[PFView·蒙皮] " + failure + " ⇒ 回退旧材质");
                return false;
            }

            var mat = new Material(shader) { color = Color.white };
            // ⚠ 必须显式绑到 shader 的采样器名 `_SkinTex`；`mainTexture` 只对应 `_MainTex`（首版事故根因）。
            mat.SetTexture("_SkinTex", texture);
            // 兼容某些引擎按 mainTexture 绑定第一个 sampler 的路径。
            mat.mainTexture = texture;
            material = mat;
            RefreshSkinParams();

            Log.Message("[PFView·蒙皮] 就绪：" + StatusText
                + "｜板缝高度=" + BumpHeightMeters + "m｜rim=" + RimStrength);
            return true;
        }

        /// <summary>把 `_SkinParams` 写进材质（初始化与调试开关共用）。</summary>
        private static void RefreshSkinParams()
        {
            Material? mat = material;
            if (mat == null) return;
            float tile = debugHighFrequency ? DebugHighFrequencyTileMeters : PanelTileMeters;
            float repeatsPerMeter = 1f / tile;
            mat.SetVector("_SkinParams", new Vector4(repeatsPerMeter, PlateDetail, RimStrength, BumpHeightMeters));
        }

        /// <summary>调试开关当前值（调试窗复选框用）。</summary>
        internal static bool DebugHighFrequency => debugHighFrequency;

        /// <summary>调试开关：把板格压到 2 m（贴图生效时一眼可见密集板缝；不生效则毫无变化）。</summary>
        internal static void SetDebugHighFrequency(bool value)
        {
            if (debugHighFrequency == value) return;
            debugHighFrequency = value;
            RefreshSkinParams();
            Log.Message("[PFView·蒙皮] 调试高频板缝 = " + value + "｜" + StatusText);
        }

        /// <summary>每帧喂太阳方向/太阳色/相机位置（视图系，与网格同系）。</summary>
        internal static void ApplyLighting(Vector3 toSun, Color sunColor, Vector3 cameraViewPos)
        {
            Material? mat = material;
            if (mat == null) return;
            mat.SetVector("_SunDir", new Vector4(toSun.x, toSun.y, toSun.z, 0f));
            mat.SetVector("_SunColor", new Vector4(sunColor.r, sunColor.g, sunColor.b, 1f));
            mat.SetVector("_CamPos", new Vector4(cameraViewPos.x, cameraViewPos.y, cameraViewPos.z, 1f));
        }

        /// <summary>窗口关闭时释放材质（贴图由 ContentFinder 持有，不归本类销毁）。</summary>
        internal static void Destroy()
        {
            if (material != null)
            {
                Object.Destroy(material);
                material = null;
            }
            tried = false;
            failure = null;
            textureLoaded = false;
            debugHighFrequency = false;
        }
    }
}
