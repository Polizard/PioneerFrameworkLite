using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;

namespace PioneerFramework
{
    /// <summary>
    /// Transpiler 守卫谓词（2026-10-15，补丁核对批）：把"该不该替换这条 `ldc.r4 1100`"
    /// 的形状判据抽成**纯函数**。两个动机：
    ///   ① **单一出处**——transpiler 与回归测试（`PatchGuardRegressionTests`）共用同一份判据；
    ///   ② **可 headless 测试**——`SpaceGroupVisualManager` 的静态构造要 Unity（建球网格），
    ///      测试进程一碰它的静态成员就 `TypeInitializationException` ⇒ 守卫逻辑必须能脱离它跑。
    ///
    /// 背景缺陷（实测）：`Transpiler_MaxAltitude_Update` 的旧守卫只认 `ldc.r4 5f`
    /// （1.6 的 `MinAltitude` 是常量）；1.7 把它改成静态属性（`call get_MinAltitude`）
    /// ⇒ 守卫永不匹配、替换**静默失效**（症状：拉远到高高度时世界拖拽转速被放大 ~43×）。
    /// </summary>
    public static class PatchIlGuards
    {
        /// <summary>
        /// `1100f`（WorldCameraDriver 的 MaxAltitude）后接的是 MinAltitude 的加载
        /// ⇒ 它是旋转速度归一化分母 `(1100 − MinAltitude)` 里的那个字面量。
        /// 1.7 形态 = 静态属性 getter（`call get_MinAltitude`）；1.6 旧形态 = 常量 `ldc.r4 5f`。
        /// </summary>
        public static bool IsMaxAltitudeDenominator(CodeInstruction current, CodeInstruction? next)
        {
            if (current.opcode != OpCodes.Ldc_R4 || (float)current.operand != 1100f)
                return false;

            if (next == null)
                return false;

            if (next.opcode == OpCodes.Ldc_R4 && next.operand is float legacyConst && legacyConst == 5f)
                return true;

            return next.opcode == OpCodes.Call
                && next.operand is MethodInfo mi
                && mi.Name == "get_MinAltitude"
                && mi.DeclaringType == typeof(WorldCameraDriver);
        }

        /// <summary>
        /// `1100f` 后接 `Mathf.Clamp` 调用 ⇒ 它是 `Mathf.Clamp(num2, MinAltitude, 1100f)` 的上界
        /// （放宽拉远）。**不**匹配行星层切换判断（`num2 &gt; 1100f` 后接分支）——那个有意保持原版。
        /// </summary>
        public static bool IsClampUpperBound(CodeInstruction current, CodeInstruction? next)
        {
            if (current.opcode != OpCodes.Ldc_R4 || (float)current.operand != 1100f)
                return false;

            return next != null
                && next.opcode == OpCodes.Call
                && next.operand is MethodInfo mi
                && mi.Name == "Clamp"
                && mi.DeclaringType == typeof(Mathf);
        }
    }
}
