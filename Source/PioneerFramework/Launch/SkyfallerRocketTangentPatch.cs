using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 在 Skyfaller 最终 drawLoc 确定后，用与上一 game tick 的位移重写 extraRotation（切线朝向）。
    /// 使用 TargetMethod() 在运行时解析方法，避免 Harmony 依赖编译期 Assembly-CSharp 的 metadata token（版本不一致时会 InvalidOperationException）。
    /// </summary>
    [HarmonyPatch]
    internal static class SkyfallerRocketTangentPatch
    {
        private static MethodInfo TargetMethod()
        {
            return AccessTools.Method(
                typeof(Skyfaller),
                "GetDrawPositionAndRotation",
                [typeof(Vector3).MakeByRefType(), typeof(float).MakeByRefType()]);
        }

        private static void Postfix(Skyfaller __instance, ref Vector3 drawLoc, ref float extraRotation)
        {
            CompRocketTangentGraphic comp = __instance.GetComp<CompRocketTangentGraphic>();
            if (comp == null)
            {
                return;
            }

            comp.ApplyTangentRotation(ref extraRotation, drawLoc, __instance);
        }
    }
}
