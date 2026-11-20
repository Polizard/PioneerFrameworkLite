using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PioneerFramework.Celestial;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework;

[StaticConstructorOnStartup]
internal static class WorldCelestialHost
{
    internal static WorldCelestialNavigation? Navigation { get; private set; }
    internal static bool Active => Navigation != null && CanUseWorld();
    internal static bool PlanetViewLocked => PioneerFrameworkLiteMod.Settings?.lockWorldPlanetView == true;
    private static bool installed;
    private static int capturedButtons;

    static WorldCelestialHost()
    {
        var harmony = new Harmony("PioneerFramework.WorldCelestialNavigation");
        try
        {
            Patch(typeof(WorldCameraDriver), nameof(WorldCameraDriver.Update), nameof(CameraUpdate));
            Patch(typeof(WorldCameraDriver), nameof(WorldCameraDriver.WorldCameraDriverOnGUI), nameof(CameraGui));
            Patch(typeof(WorldInterface), nameof(WorldInterface.WorldInterfaceOnGUI), nameof(BeforeWorldGui), nameof(AfterWorldGui));
            Patch(typeof(WorldSelector), nameof(WorldSelector.WorldSelectorOnGUI), nameof(SelectorInput));
            Patch(typeof(WorldDrawLayerBase), nameof(WorldDrawLayerBase.Render), nameof(BeforeLayerDraw), transpiler: nameof(ReplaceTerrainDraw));
            Patch(typeof(WorldDynamicDrawManager), nameof(WorldDynamicDrawManager.DrawDynamicWorldObjects), nameof(NativeOverlays));
            Patch(typeof(WorldFeatures), nameof(WorldFeatures.UpdateFeatures), transpiler: nameof(FilterWorldFeatureLabels));
            Patch(typeof(WorldSelectionDrawer), nameof(WorldSelectionDrawer.DrawSelectionOverlays), nameof(NativeOverlays));
            installed = true;
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(harmony.Id);
            Log.Error("[PFL World Celestial] " + ex);
        }

        void Patch(Type owner, string method, string? prefix = null, string? postfix = null, string? transpiler = null) =>
            harmony.Patch(AccessTools.Method(owner, method), Hook(prefix), Hook(postfix), Hook(transpiler));
        static HarmonyMethod? Hook(string? name) =>
            name == null ? null : new HarmonyMethod(typeof(WorldCelestialHost), name);
    }

    private static bool CanUseWorld() => Current.ProgramState == ProgramState.Playing &&
        Find.World?.renderer != null && WorldRendererUtility.WorldSelected &&
        Find.WorldInterface is { } ui && !ui.routePlanner.Active && !ui.tilePicker.Active && !ui.targeter.IsTargeting &&
        Find.MainTabsRoot.OpenTab == null;

    internal static void Update()
    {
        if (Navigation == null || LongEventHandler.ShouldWaitForEvent) return;
        if (!TryHost(out _, out var runtime, out var layer, out var camera, out _) ||
            !Navigation.ValidFor(runtime, camera, layer))
        {
            Close(false);
            return;
        }
        if (!Navigation.Update()) Close(true);
    }

    private static bool TryHost(out CelestialBodyManager manager, out CelestialRuntime runtime,
        out PlanetLayer layer, out Camera camera, out WorldCameraDriver driver)
    {
        manager = null!;
        runtime = null!;
        layer = null!;
        camera = null!;
        driver = null!;
        if (!installed || !CanUseWorld() || LongEventHandler.ShouldWaitForEvent) return false;
        layer = Find.WorldSelector.SelectedLayer;
        camera = Find.WorldCamera;
        driver = Find.WorldCameraDriver;
        manager = Current.Game.GetComponent<CelestialBodyManager>();
        return layer != null && camera != null && driver != null && manager != null &&
            !Megastructures.ArtificialSurfaceLayers.TryDescribe(layer, out _) &&
            manager.TryGetCelestialRuntime(out runtime) && !runtime.IsRestoring;
    }

    private static bool Start(bool stellar)
    {
        if (!TryHost(out var manager, out var runtime, out var layer, out var camera, out var driver)) return false;
        Navigation = WorldCelestialNavigation.TryCreate(manager, runtime, layer, camera, driver);
        if (Navigation == null) return false;
        Find.WorldSelector.dragBox.active = false;
        if (stellar) Navigation.ShowSystem();
        return true;
    }

    internal static void Close(bool complete)
    {
        var previous = Navigation;
        Navigation = null;
        previous?.Restore(complete);
    }

    private static bool CameraUpdate(WorldCameraDriver __instance)
    {
        if (Active)
        {
            WorldNativeCamera.Reset();
            return false;
        }
        WorldNativeCamera.BeginUpdate(__instance);
        return true;
    }
    private static bool NativeOverlays() => !Active;

    // Feature names are persistent text objects. Keep the native update running so it can hide and restore them.
    private static bool ShowWorldFeatureLabels(PlaySettings settings) => settings.showWorldFeatures && !Active;

    private static IEnumerable<CodeInstruction> FilterWorldFeatureLabels(IEnumerable<CodeInstruction> instructions)
    {
        var preference = AccessTools.Field(typeof(PlaySettings), nameof(PlaySettings.showWorldFeatures));
        var visibility = AccessTools.Method(typeof(WorldCelestialHost), nameof(ShowWorldFeatureLabels));
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.LoadsField(preference))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = visibility;
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("WorldFeatures.UpdateFeatures visibility check changed.");
    }

    private static bool CameraGui()
    {
        if (Active) return false;
        var e = Event.current;
        if (PlanetViewLocked || e.type != EventType.ScrollWheel || e.delta.y <= 0 ||
            Find.WindowStack.WindowsPreventCameraMotion ||
            Find.WindowStack.GetWindowAt(UI.MousePositionOnUIInverted) != null ||
            !TryHost(out _, out _, out var layer, out var camera, out _) ||
            !WorldCelestialNavigation.CanEnterWithWheel(layer, camera) || !Start(false))
            return true;
        Navigation!.HandleInput(e);
        return false;
    }

    private static bool BeforeWorldGui(WorldInterface __instance)
    {
        var e = Event.current;
        releasedButtons = 0;
        if (e.type == EventType.MouseDown && e.button is >= 0 and <= 2)
        {
            int mask = 1 << e.button;
            capturedButtons &= ~mask;
            if (Active && !OverMainButtonBar() && Find.WindowStack.GetWindowAt(UI.MousePositionOnUIInverted) == null)
                capturedButtons |= mask;
        }
        if (e.rawType == EventType.MouseUp && e.button is >= 0 and <= 2)
        {
            int mask = 1 << e.button;
            bool captured = (capturedButtons & mask) != 0;
            capturedButtons &= ~mask;
            if (captured)
            {
                releasedButtons |= mask;
                if (!Active && e.type != EventType.Used) e.Use();
            }
        }
        if (!Active) return true;
        Find.WindowStack.TryRemove(__instance.inspectPane, doCloseSound: false);
        if (!Find.UIRoot.screenshotMode.FiltersCurrentEvent)
        {
            Find.ColonistBar.ColonistBarOnGUI();
            __instance.globalControls.WorldGlobalControlsOnGUI();
        }
        return false;
    }

    // The native main buttons run after WorldInterfaceOnGUI and occupy the bottom 35 UI units.
    internal static bool OverMainButtonBar() => UI.MousePositionOnUIInverted.y >= UI.screenHeight - 35f;

    private static void AfterWorldGui()
    {
        if (!TryHost(out _, out _, out _, out _, out _) || Find.UIRoot.screenshotMode.FiltersCurrentEvent) return;
        if (Navigation is { } view)
        {
            view.DrawGui();
            if (!ReferenceEquals(Navigation, view)) return;
            SpaceGroupVisualManager.WorldInterfaceOnGUIBody(view);
            view.HandleInput(Event.current);
        }
        else
        {
            if (Widgets.ButtonText(new Rect(12, 112, 126, 28), "PF_WorldCelestial_System".Translate()))
                Start(true);
            DrawPlanetViewLock(new Rect(146, 112, 180, 28));
        }
    }

    internal static void DrawPlanetViewLock(Rect rect)
    {
        if (PioneerFrameworkLiteMod.Settings is not { } settings) return;
        bool locked = settings.lockWorldPlanetView && Navigation == null;
        var label = locked ? "PF_WorldCelestial_UnlockPlanet" : "PF_WorldCelestial_LockPlanet";
        if (Widgets.ButtonText(rect, label.Translate()))
        {
            settings.lockWorldPlanetView = !locked;
            if (settings.lockWorldPlanetView && Navigation is { } view)
            {
                view.PrepareNativeLock();
                Close(true);
            }
            settings.Write();
        }
        TooltipHandler.TipRegion(rect, "PF_WorldCelestial_LockPlanetTip".Translate());
    }

    private static bool SelectorInput()
    {
        if (Active) return false;
        // 原版会在 Event.Use 后继续处理 rawType == MouseUp。
        var e = Event.current;
        return !(e.rawType == EventType.MouseUp && e.button is >= 0 and <= 2 && (releasedButtons & (1 << e.button)) != 0);
    }

    private static int releasedButtons;

    private static readonly AccessTools.FieldRef<WorldCameraDriver, float> DesiredAltitude =
        AccessTools.FieldRefAccess<WorldCameraDriver, float>("desiredAltitude");
    private static readonly AccessTools.FieldRef<WorldCameraDriver, float> LayerAltitude =
        AccessTools.FieldRefAccess<WorldCameraDriver, float>("layerAltitudeOffset");
    private static readonly AccessTools.FieldRef<WorldCameraDriver, Vector3> LayerOrigin =
        AccessTools.FieldRefAccess<WorldCameraDriver, Vector3>("layerOriginOffset");
    private static readonly AccessTools.FieldRef<WorldCameraDriver, float> AltitudeDecay =
        AccessTools.FieldRefAccess<WorldCameraDriver, float>("altitudeDecay");
    private static readonly AccessTools.FieldRef<WorldCameraDriver, Vector2> RotationVelocity =
        AccessTools.FieldRefAccess<WorldCameraDriver, Vector2>("rotationVelocity");
    private static readonly AccessTools.FieldRef<WorldCameraDriver, Vector2> DesiredRotation =
        AccessTools.FieldRefAccess<WorldCameraDriver, Vector2>("desiredRotation");
    private static readonly AccessTools.FieldRef<WorldCameraDriver, Vector2> DesiredRotationRaw =
        AccessTools.FieldRefAccess<WorldCameraDriver, Vector2>("desiredRotationRaw");
    private static readonly AccessTools.FieldRef<WorldCameraDriver, float> RotationAnimation =
        AccessTools.FieldRefAccess<WorldCameraDriver, float>("rotationAnimation_lerpFactor");
    private static readonly AccessTools.FieldRef<WorldCameraDriver, float> LayerAltitudeVelocity =
        AccessTools.FieldRefAccess<WorldCameraDriver, float>("layerOffsetVel");
    private static readonly AccessTools.FieldRef<WorldCameraDriver, Vector3> LayerOriginVelocity =
        AccessTools.FieldRefAccess<WorldCameraDriver, Vector3>("layerOriginOffsetVel");
    private static readonly AccessTools.FieldRef<WorldCameraDriver, List<CameraDriver.DragTimeStamp>> DragStamps =
        AccessTools.FieldRefAccess<WorldCameraDriver, List<CameraDriver.DragTimeStamp>>("dragTimeStamps");

    internal static void PlaceNative(WorldCameraDriver driver, PlanetLayer layer, Quaternion orientation, float altitude)
    {
        if (PlanetLayer.Selected != layer) Find.WorldSelector.ClearSelection();
        PlanetLayer.Selected = layer;
        driver.sphereRotation = Quaternion.Inverse(orientation);
        driver.altitude = Mathf.Clamp(altitude, WorldCameraDriver.MinAltitude,
            PlanetViewLocked ? WorldNativeCamera.MaximumAltitude(layer) : 50000f);
        DesiredAltitude(driver) = driver.altitude;
        LayerAltitude(driver) = layer.ExtraCameraAltitude;
        LayerOrigin(driver) = layer.Origin;
        LayerAltitudeVelocity(driver) = 0;
        LayerOriginVelocity(driver) = Vector3.zero;
        AltitudeDecay(driver) = 0;
        RotationVelocity(driver) = Vector2.zero;
        DesiredRotation(driver) = Vector2.zero;
        DesiredRotationRaw(driver) = Vector2.zero;
        RotationAnimation(driver) = 1;
        DragStamps(driver).Clear();
        var camera = Find.WorldCamera;
        camera.transform.rotation = orientation;
        camera.transform.position = layer.Origin - camera.transform.forward * driver.TrueAltitude;
        WorldNativeCamera.Reset();
    }

    private static bool BeforeLayerDraw(WorldDrawLayerBase __instance) => !Active ||
        (__instance is not WorldDrawLayer || Navigation!.DrawNativeLayer(__instance));

    private static readonly MethodInfo PlainDraw = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh),
        new[] { typeof(Mesh), typeof(Vector3), typeof(Quaternion), typeof(Material), typeof(int) });
    private static readonly MethodInfo BlockDraw = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh),
        new[] { typeof(Mesh), typeof(Vector3), typeof(Quaternion), typeof(Material), typeof(int),
            typeof(Camera), typeof(int), typeof(MaterialPropertyBlock) });

    private static IEnumerable<CodeInstruction> ReplaceTerrainDraw(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            string? method = instruction.Calls(PlainDraw) ? nameof(DrawPlain) :
                instruction.Calls(BlockDraw) ? nameof(DrawWithBlock) : null;
            if (method == null)
            {
                yield return instruction;
                continue;
            }
            yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(WorldCelestialHost), method));
            replaced++;
        }
        if (replaced != 2) throw new InvalidOperationException("WorldDrawLayerBase.Render mesh calls changed.");
    }

    private static void DrawPlain(Mesh mesh, Vector3 pos, Quaternion rotation, Material material, int layer,
        WorldDrawLayerBase source)
    {
        if (Active && source is WorldDrawLayer terrain && Navigation!.TryTerrainMatrix(terrain.planetLayer, out var matrix))
            Graphics.DrawMesh(mesh, matrix * Matrix4x4.TRS(pos, rotation, Vector3.one), material, layer);
        else
            Graphics.DrawMesh(mesh, pos, rotation, material, layer);
    }

    private static void DrawWithBlock(Mesh mesh, Vector3 pos, Quaternion rotation, Material material, int layer,
        Camera camera, int submesh, MaterialPropertyBlock block, WorldDrawLayerBase source)
    {
        if (Active && source is WorldDrawLayer terrain && Navigation!.TryTerrainMatrix(terrain.planetLayer, out var matrix))
            Graphics.DrawMesh(mesh, matrix * Matrix4x4.TRS(pos, rotation, Vector3.one), material, layer, camera, submesh, block);
        else
            Graphics.DrawMesh(mesh, pos, rotation, material, layer, camera, submesh, block);
    }
}
