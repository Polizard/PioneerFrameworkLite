using System;
using PioneerFramework.Celestial;
using PioneerFramework.Launch;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using HarmonyLib;

namespace PioneerFramework;

/// <summary>世界相机的交互参数。距离为所选 layer 球心的距离，不依赖世界原点或默认半径。</summary>
internal static class WorldCameraTuning
{
    internal static float DragDegreesPerPixel(double radius, double distance, float fov, float uiHeight)
    {
        double height = Math.Max(1, uiHeight);
        double ratio = Math.Max(0, distance / Math.Max(.001, radius) - 1);
        double projection = 2 * Math.Tan(Math.Clamp(fov, 1, 170) * Math.PI / 360) * 180 / Math.PI / height;
        return (float)Math.Clamp(2.5 * ratio * projection, .24 * 720 / height, .48 * 720 / height);
    }

    internal static float KeyboardScale(double radius, double distance) =>
        (float)(.45 + .55 * Math.Clamp((distance / Math.Max(.001, radius) - 1) / 9, 0, 1));

    // 约 105 ms 跟上 90% 的目标转角；输入目标始终保留，平滑不会吞掉鼠标位移。
    internal static float RotationBlend(float seconds) =>
        (float)(1 - Math.Exp(-22 * Math.Clamp(seconds, 0, .1f)));

    internal static DVector3 SurfaceCameraForward(DQuaternion viewFrame, DQuaternion bodyOrientation,
        DVector3 terrainCenter) => DVector3.Rotate(-terrainCenter, viewFrame * bodyOrientation);

    internal static float MaximumAltitude(double layerRadius, float layerOffset, float minimum,
        double bodyRadius, double periapsis, double? gm, double? parentGm) =>
        (float)Math.Max(minimum, WorldCelestialFrame.LockedDistanceLimit(bodyRadius, periapsis, gm, parentGm) *
            layerRadius / bodyRadius - layerOffset);
}

/// <summary>
/// 保留原生 PlanetLayer 相机、地表网格和输入处理，仅修正灵敏度、锁定边界与显示朝向。
/// 原生 Update 在目标朝向上累计输入，在唯一一次 ApplyPosition 之前提交平滑后的显示朝向。
/// </summary>
internal static class WorldNativeCamera
{
    private static readonly GameComponentRef<CelestialBodyManager> managerRef = new();
    private static readonly AccessTools.FieldRef<WorldCameraDriver, float> DesiredAltitude =
        AccessTools.FieldRefAccess<WorldCameraDriver, float>("desiredAltitude");
    private static readonly AccessTools.FieldRef<WorldCameraDriver, float> RotationAnimation =
        AccessTools.FieldRefAccess<WorldCameraDriver, float>("rotationAnimation_lerpFactor");
    private static WorldCameraDriver? trackedDriver;
    private static World? trackedWorld;
    private static PlanetLayer? trackedLayer;
    private static Quaternion target;
    private static Quaternion presented;
    private static bool updating;

    private static bool NativeSelected => Current.ProgramState == ProgramState.Playing &&
        Find.World?.renderer != null && WorldRendererUtility.WorldSelected && !WorldCelestialHost.Active;

    internal static void Reset()
    {
        trackedDriver = null;
        trackedWorld = null;
        trackedLayer = null;
        updating = false;
    }

    internal static void BeginUpdate(WorldCameraDriver driver)
    {
        if (!NativeSelected || LongEventHandler.ShouldWaitForEvent || !Find.WorldInterface.everReset ||
            Find.WindowStack.WindowsPreventCameraMotion || RotationAnimation(driver) < 1)
        {
            Reset();
            return;
        }

        // 跳转、跟随、换层、换世界都可能在原生 Update 以外改变朝向，立即接受这些外部变更。
        if (!ReferenceEquals(trackedDriver, driver) || !ReferenceEquals(trackedWorld, Find.World) ||
            !ReferenceEquals(trackedLayer, PlanetLayer.Selected) || !driver.sphereRotation.Equals(presented))
        {
            presented = target = driver.sphereRotation;
            trackedDriver = driver;
            trackedWorld = Find.World;
            trackedLayer = PlanetLayer.Selected;
        }
        driver.sphereRotation = target;
        updating = true;
    }

    internal static void PresentRotation(WorldCameraDriver driver)
    {
        if (!NativeSelected) return;
        if (WorldCelestialHost.PlanetViewLocked)
        {
            float maximum = MaximumAltitude();
            driver.altitude = Mathf.Clamp(driver.altitude, WorldCameraDriver.MinAltitude, maximum);
            DesiredAltitude(driver) = Mathf.Clamp(DesiredAltitude(driver), WorldCameraDriver.MinAltitude, maximum);
            if (Find.WorldCamera is { } camera && PlanetLayer.Selected is { } layer)
                camera.farClipPlane = Mathf.Max(camera.farClipPlane, driver.TrueAltitude + layer.Radius * 2 + 100);
        }
        if (!updating || !ReferenceEquals(trackedDriver, driver)) return;
        target = driver.sphereRotation;
        presented = Quaternion.Slerp(presented, target, WorldCameraTuning.RotationBlend(Time.unscaledDeltaTime));
        driver.sphereRotation = presented;
        updating = false;
    }

    // 原生输入继续使用 UnityGUIBugsFixer 的真实鼠标位移和玩家 MapDragSensitivity。
    internal static float DragTileSize()
    {
        if (!NativeSelected || PlanetLayer.Selected is not { } layer) return GenWorldUI.CurUITileSize();
        var camera = Find.WorldCamera;
        float gain = WorldCameraTuning.DragDegreesPerPixel(layer.Radius,
            (camera.transform.position - layer.Origin).magnitude, camera.fieldOfView, UI.screenHeight);
        return .273f / gain;
    }

    internal static float KeyboardScale(WorldCameraDriver driver) => NativeSelected && PlanetLayer.Selected is { } layer
        ? WorldCameraTuning.KeyboardScale(layer.Radius, driver.TrueAltitude)
        : Mathf.Clamp01((driver.altitude - WorldCameraDriver.MinAltitude) / (1100 - WorldCameraDriver.MinAltitude)) * .85f + .15f;

    internal static bool AllowLayerZoom() => Prefs.ZoomSwitchWorldLayer &&
        !(NativeSelected && WorldCelestialHost.PlanetViewLocked);

    internal static float MaximumAltitude() => NativeSelected && WorldCelestialHost.PlanetViewLocked &&
        PlanetLayer.Selected is { } layer ? MaximumAltitude(layer) : 50000f;

    internal static float MaximumAltitude(PlanetLayer layer)
    {
        var manager = managerRef.Get();
        var body = manager?.GetBodyById(GetSurfaceState.GetBodyForLayer(manager, layer));
        if (body == null || body.radius <= 0 || layer.Radius <= 0) return 50000f;
        var parent = manager!.GetBodyById(body.ParentBodyId);
        return WorldCameraTuning.MaximumAltitude(layer.Radius, layer.ExtraCameraAltitude, WorldCameraDriver.MinAltitude,
            body.radius, body.OrbitPeriapsis, body.gravitationalParameter, parent?.gravitationalParameter);
    }
}
