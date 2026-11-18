using UnityEngine;
using Verse;

namespace PioneerFramework.Combat
{
    /// <summary>
    /// 精确炮击标记（测试用）：在被精确瞄准的敌舰格子上绘制一个半透明红色方块。
    /// 生命周期由 CompMapDesignator 管理：设置精确炮击点后生成，清除/目标失效/定位机拆除时销毁。
    /// </summary>
    // 2026-11-17（用户授权）：有静态 Material 字段（MarkerMat 内联初始化）⇒ 标记静态构造，
    // 让材质在启动时的主线程创建，消掉 "needs a StaticConstructorOnStartup attribute" 警告。
    [Verse.StaticConstructorOnStartup]
    public class Mote_PreciseStrikeMarker : Mote
    {
        /// <summary>标记边长（格）。</summary>
        private const float MarkerSize = 1.5f;

        /// <summary>半透明红色材质（Transparent 着色器支持 alpha 混合）。</summary>
        private static readonly Material MarkerMat =
            SolidColorMaterials.NewSolidColorMaterial(new Color(1f, 0.2f, 0.2f, 0.32f), ShaderDatabase.Transparent);

        /// <summary>Mote 的绘制入口是 DrawAt（Thing.Draw → DrawAt），自定义标记不调用 base（def 无贴图）。</summary>
        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            if (Find.UIRoot.HideMotes) return;
            Vector3 pos = drawLoc;
            pos.y += 0.05f; // 微抬避免与地面深度闪烁
            // plane10 是 10 格平面，缩放到标记尺寸
            float scale = MarkerSize / 10f;
            Matrix4x4 trs = Matrix4x4.TRS(pos, Quaternion.identity, new Vector3(scale, 1f, scale));
            Graphics.DrawMesh(MeshPool.plane10, trs, MarkerMat, 0);
        }
    }
}
