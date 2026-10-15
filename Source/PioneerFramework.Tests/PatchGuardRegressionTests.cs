using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 补丁守卫回归（2026-10-15，补丁核对批）：**Transpiler 的守卫形状必须跟住目标 IL**。
    ///
    /// 背景（核对中实测出的真缺陷）：`Transpiler_MaxAltitude_Update` 的旧守卫要求 `ldc.r4 1100`
    /// 后接 `ldc.r4 5f`——那是 1.6 的形态（当年 `MinAltitude` 是常量 5f）；而 1.7 把它改成了
    /// **静态属性**（`call get_MinAltitude`，桌面 = 125f）⇒ 守卫永不匹配、替换静默失效
    /// （症状：拉远到高高度后世界拖拽转速被放大到 ~43×；不抛错、无日志）。
    ///
    /// ⚠ 这里测的是抽出来的**纯谓词**（`PatchIlGuards`，transpiler 的单一出处）而不是 transpiler
    /// 本身：`SpaceGroupVisualManager` 的静态构造要 Unity（建球网格），测试进程一碰它的静态成员
    /// 就 `TypeInitializationException`——这正是"闸门盲区：没人碰过的类型"那条教训的实例。
    ///
    /// 钉住两件事：① 1.7 形态（与 1.6 旧形态）**必**被认；② 无关形态**必不**被认
    /// （守卫的牙还在——防"修宽了变成乱改字面量"）。
    /// </summary>
    public class PatchGuardRegressionTests
    {
        private static System.Reflection.MethodInfo MinAltitudeGetter() =>
            AccessTools.PropertyGetter(typeof(WorldCameraDriver), nameof(WorldCameraDriver.MinAltitude))!;

        private static CodeInstruction Ldc1100() => new(OpCodes.Ldc_R4, 1100f);

        private static CodeInstruction Ldc(float v) => new(OpCodes.Ldc_R4, v);

        // ── Update：旋转速度归一化 (altitude − MinAltitude) / (1100 − MinAltitude) ──

        [Fact]
        public void MaxAltitudeUpdate_Recognizes_17Shape()
        {
            var getMin = MinAltitudeGetter();
            Assert.NotNull(getMin);
            Assert.True(PatchIlGuards.IsMaxAltitudeDenominator(
                Ldc1100(), new CodeInstruction(OpCodes.Call, getMin)));
        }

        [Fact]
        public void MaxAltitudeUpdate_Recognizes_LegacyShape()
        {
            Assert.True(PatchIlGuards.IsMaxAltitudeDenominator(Ldc1100(), Ldc(5f)));
        }

        [Fact]
        public void MaxAltitudeUpdate_Rejects_UnrelatedLiteral()
        {
            Assert.False(PatchIlGuards.IsMaxAltitudeDenominator(Ldc1100(), Ldc(3f)));
            Assert.False(PatchIlGuards.IsMaxAltitudeDenominator(Ldc1100(), new CodeInstruction(OpCodes.Sub)));
            Assert.False(PatchIlGuards.IsMaxAltitudeDenominator(Ldc1100(), null));
            Assert.False(PatchIlGuards.IsMaxAltitudeDenominator(Ldc(1101f), Ldc(5f)));
        }

        // ── OnGUI：Mathf.Clamp(num2, MinAltitude, 1100f) 的上界 ──

        [Fact]
        public void MaxAltitudeOnGui_Recognizes_ClampArgument()
        {
            var clamp = AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp),
                new[] { typeof(float), typeof(float), typeof(float) });
            Assert.NotNull(clamp);
            Assert.True(PatchIlGuards.IsClampUpperBound(Ldc1100(), new CodeInstruction(OpCodes.Call, clamp)));
        }

        [Fact]
        public void MaxAltitudeOnGui_Rejects_NonClampFollower()
        {
            // 行星层切换判断（`num2 > 1100f`）后接的是分支/运算而不是 Clamp ⇒ 有意保持原版
            Assert.False(PatchIlGuards.IsClampUpperBound(Ldc1100(), new CodeInstruction(OpCodes.Sub)));
            Assert.False(PatchIlGuards.IsClampUpperBound(Ldc1100(), Ldc(5f)));
        }
    }
}
