using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    /// <summary>
    /// 装饰金属复用环壳材质；警示灯与垂直构件末端荧光分别使用普通光斑和周期呼吸光斑。
    /// 所有光斑保留深度测试，太阳、相机和时间统一由 RingWorldVisualState 刷新。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class RingOrnamentMaterials
    {
        private const int GlowQueue = 3550;
        internal const float TerminalPeriodSeconds = 5f;

        private static Material? sprite;
        private static Material? terminalGlow;
        private static Material? terminalCore;
        private static Shader? terminalShader;
        private static bool terminalShaderWarned;
        private static Shader? glowShader;
        private static bool glowShaderWarned;

        internal static Material Metal => RingWorldMaterials.HullMaterial;

        /// <summary>原有塔灯与漂浮信标，保持原有颜色和脉冲。</summary>
        internal static Material? Sprite => sprite ??= Create(GlowShader, new Vector4(2f, 2.4f, 2.6f, 1.40f), GlowQueue);

        /// <summary>信号柱及天线面板末端，五秒一轮的呼吸光，各构件由顶点 alpha 错开相位。</summary>
        internal static Material? TerminalGlow => terminalGlow ??= Create(GlowShader,
            new Vector4(3f, 3.8f, 1.1f, 2f * Mathf.PI / TerminalPeriodSeconds), GlowQueue);

        /// <summary>无光照衰减的实体发光灯头，写深度；与外围灯晕使用同一周期和相位。</summary>
        internal static Material? TerminalCore => terminalCore ??= Create(TerminalShader,
            new Vector4(4f, 3.8f, 1f, 2f * Mathf.PI / TerminalPeriodSeconds), 3500);

        private static Material? Create(Shader? shader, Vector4 parameters, int queue)
        {
            if (shader == null) return null;
            // 两种脉冲参数需要独立实例，避免 MaterialPool 相同 key 导致相互覆盖。
            Material material = new Material(shader);
            material.SetColor("_Color", Color.white);
            material.SetColor("_GlowColor", Color.white);
            material.SetVector("_GlowParams", parameters);
            material.SetTexture("_MainTex", RingWorldMaterials.GlowDot);
            material.SetTexture("_GlowTex", RingWorldMaterials.GlowDot);
            material.renderQueue = queue;
            RingWorldVisualState.Register(material);
            return material;
        }

        private static Shader? TerminalShader
        {
            get
            {
                if (terminalShader != null) return terminalShader;
                terminalShader = Shader.Find("PF_RingTerminal");
                if (terminalShader == null && !terminalShaderWarned)
                {
                    terminalShaderWarned = true;
                    Log.Warning("[PFL RingWorld] Shader 'PF_RingTerminal' not found; terminal cores fall back to hull metal.");
                }
                return terminalShader;
            }
        }

        private static Shader? GlowShader
        {
            get
            {
                if (glowShader != null) return glowShader;
                glowShader = Shader.Find("PF_RingGlow");
                if (glowShader == null && !glowShaderWarned)
                {
                    glowShaderWarned = true;
                    Log.Warning("[PFL RingWorld] Shader 'PF_RingGlow' not found; ring ornament lights/beacons are skipped.");
                }
                return glowShader;
            }
        }
    }
}
