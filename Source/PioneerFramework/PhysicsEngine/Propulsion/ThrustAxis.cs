// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 推进器轴向（船体局部系）。局部系约定：Map +X → 船 +X，Map +Z → 船 +Z，船 +Y = 世界 Y。
    /// XZ 四向随建筑 Rot4 映射；Up/Down 固定。
    /// </summary>
    public enum ThrustAxis
    {
        Forward,  // +Z（船头）
        Back,     // -Z（船尾，主推进默认）
        Left,     // -X
        Right,    // +X
        Up,       // +Y
        Down      // -Y
    }

    /// <summary>ThrustAxis 辅助：轴向 → 船体局部系单位向量；Rot4 朝向映射。</summary>
    public static class ThrustAxisHelper
    {
        private static readonly ThrustAxis[] XzAxes =
            [ThrustAxis.Forward, ThrustAxis.Back, ThrustAxis.Left, ThrustAxis.Right];

        public static DVector3 ShipVector(this ThrustAxis axis)
        
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              => axis switch
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              {
            ThrustAxis.Forward => DVector3.forward,
            ThrustAxis.Back => DVector3.back,
            ThrustAxis.Left => DVector3.left,
            ThrustAxis.Right => DVector3.right,
            ThrustAxis.Up => DVector3.up,
            ThrustAxis.Down => DVector3.down,
            _ => DVector3.forward,
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              };

        /// <summary>
        /// XZ 四向随建筑 Rot4 旋转映射；Up/Down 固定。
        /// RimWorld 地图坐标：Rot4.North 朝 -Z（屏幕上）、East +X、South +Z、West -X；
        /// 船体局部系 = 地图 XZ（Map +X=船+X, Map +Z=船+Z）。
        /// 朝向往东 = 船头(-Z)转成船右(+X)：绕 +Y 旋转 -90°·AsInt。
        /// </summary>
        public static ThrustAxis RotateByRot4(this ThrustAxis axis, Rot4 rot)
        {
            if (axis == ThrustAxis.Up || axis == ThrustAxis.Down) return axis;
            DVector3 v = axis.ShipVector();
            double rad = -rot.AsInt * 90f * Mathf.Deg2Rad;
            DVector3 r = DVector3.Rotate(v, DVector3.up, rad);
            foreach (var a in XzAxes)
            {
                if (DVector3.Dot(r, a.ShipVector()) > 0.999)
                    return a;
            }
            return axis;
        }
    }
}
