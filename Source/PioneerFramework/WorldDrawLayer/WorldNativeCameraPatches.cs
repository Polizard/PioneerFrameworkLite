using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework;

/// <summary>匹配真实 1.7 相机调用点；数量或公式变化时明确失败，避免悄悄失去交互修正。</summary>
internal static class WorldNativeCameraPatches
{
    internal static IEnumerable<CodeInstruction> OnGui(IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();
        var tileSize = AccessTools.Method(typeof(GenWorldUI), nameof(GenWorldUI.CurUITileSize));
        var layerZoom = AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.ZoomSwitchWorldLayer));
        int drag = 0, zoom = 0, maximum = 0;
        for (int i = 0; i < list.Count; i++)
        {
            string? hook = null;
            if (list[i].Calls(tileSize)) { hook = nameof(WorldNativeCamera.DragTileSize); drag++; }
            else if (list[i].Calls(layerZoom)) { hook = nameof(WorldNativeCamera.AllowLayerZoom); zoom++; }
            else if (PatchIlGuards.IsClampUpperBound(list[i], i + 1 < list.Count ? list[i + 1] : null))
            { hook = nameof(WorldNativeCamera.MaximumAltitude); maximum++; }
            if (hook == null) continue;
            list[i].opcode = OpCodes.Call;
            list[i].operand = AccessTools.Method(typeof(WorldNativeCamera), hook, Type.EmptyTypes);
        }
        if (drag != 1 || zoom != 1 || maximum != 1)
            throw new InvalidOperationException($"World camera GUI changed: drag={drag}, layerZoom={zoom}, maximum={maximum}.");
        return list;
    }

    internal static IEnumerable<CodeInstruction> Update(IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();
        var altitude = AccessTools.Field(typeof(WorldCameraDriver), nameof(WorldCameraDriver.altitude));
        var minimum = AccessTools.PropertyGetter(typeof(WorldCameraDriver), nameof(WorldCameraDriver.MinAltitude));
        var apply = AccessTools.Method(typeof(WorldCameraDriver), "ApplyPositionToGameObject");
        int speed = 0, present = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (i + 11 < list.Count && list[i].opcode == OpCodes.Ldarg_0 && list[i + 1].LoadsField(altitude) &&
                list[i + 2].Calls(minimum) && list[i + 3].opcode == OpCodes.Sub &&
                PatchIlGuards.IsMaxAltitudeDenominator(list[i + 4], list[i + 5]) &&
                list[i + 6].opcode == OpCodes.Sub && list[i + 7].opcode == OpCodes.Div &&
                list[i + 8].LoadsConstant(.85f) && list[i + 9].opcode == OpCodes.Mul &&
                list[i + 10].LoadsConstant(.15f) && list[i + 11].opcode == OpCodes.Add)
            {
                var first = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(list[i]).MoveBlocksFrom(list[i]);
                list.RemoveRange(i, 12);
                list.InsertRange(i, new[] { first, new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(WorldNativeCamera), nameof(WorldNativeCamera.KeyboardScale))) });
                speed++;
            }
            if (!list[i].Calls(apply)) continue;
            var duplicate = new CodeInstruction(OpCodes.Dup).MoveLabelsFrom(list[i]).MoveBlocksFrom(list[i]);
            list.InsertRange(i, new[] { duplicate, new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(WorldNativeCamera), nameof(WorldNativeCamera.PresentRotation))) });
            i += 2;
            present++;
        }
        if (speed != 1 || present != 1)
            throw new InvalidOperationException($"World camera Update changed: speed={speed}, presentation={present}.");
        return list;
    }
}
