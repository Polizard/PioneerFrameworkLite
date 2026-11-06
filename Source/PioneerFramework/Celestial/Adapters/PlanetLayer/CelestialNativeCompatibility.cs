using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Celestial
{
    public sealed record CelestialNativeContractEntry(string Name, bool Available, string Detail);
    /// <summary>All required private/native signatures are inspected once, before constructing framework-owned worlds.</summary>
    public static class CelestialNativeCompatibility
    {
        private static readonly Lazy<Contracts> contracts = new(Inspect);
        public static IReadOnlyList<CelestialNativeContractEntry> Report => contracts.Value.Report;
        internal sealed record PatchBinding(string Name, MethodInfo? Target, MethodInfo? Handler, string Kind);
        internal static IReadOnlyList<PatchBinding> ExpectedPatches()
        {
            var result = new List<PatchBinding>();
            void Add(string key, Type hook, string name, string kind)
            { result.Add(new(key + "." + kind + "." + name, contracts.Value.Methods.GetValueOrDefault(key), AccessTools.Method(hook, name), kind)); }
            var bootstrap = typeof(CelestialWorldBootstrapHooks); var lifecycle = typeof(CelestialNativeMapLifecycle);
            Add("WorldGrid.RegisterPlanetLayer", bootstrap, "BeginConstruction", "prefix");
            Add("WorldGrid.RegisterPlanetLayer", bootstrap, "EndConstruction", "finalizer");
            Add("WorldGrid.RegisterPlanetLayer.Settings", bootstrap, "BeginRootConstruction", "prefix");
            Add("WorldGrid.RegisterPlanetLayer.Settings", bootstrap, "EndConstruction", "finalizer");
            Add("WorldGrid.GenerateFreshWorld", bootstrap, "BeginFreshWorld", "prefix");
            Add("WorldGenerator.GeneratePlanetLayer", bootstrap, "PlanetLayerGenerated", "postfix");
            Add("WorldGenerator.GenerateWorld", bootstrap, "BeginInvocation", "prefix");
            Add("WorldGenerator.GenerateWorld", bootstrap, "FinalizeWorld", "finalizer");
            Add("Game.DeinitAndRemoveMap", lifecycle, "CaptureMapRemoval", "prefix");
            Add("Game.DeinitAndRemoveMap", lifecycle, "MapRemoved", "postfix");
            Add("WorldObjectsHolder.Remove", lifecycle, "CaptureParentRemoval", "prefix");
            Add("WorldObjectsHolder.Remove", lifecycle, "ParentRemoved", "postfix");
            Add("GameDataSaveLoader.SaveGame", typeof(CelestialSaveGuard), "BeforeSave", "prefix");
            var local = typeof(PioneerFramework.MapGeneration.NativeLocalMapHooks);
            Add("MapGenerator.GenerateMap", local, "Begin", "prefix");
            Add("MapGenerator.GenerateMap", local, "AdaptLifecycle", "transpiler");
            Add("MapGenerator.GenerateMap", local, "End", "finalizer");
            Add("MapGenerator.GenerateContentsIntoMap", local, "Contents", "prefix");
            Add("MapGenerator.MapGeneratorPostInit", local, "PostInitialize", "prefix");
            Add("WorldGrid.CreateRequiredLayers", typeof(CelestialBodyLayerManager), "CreateRequiredLayers_Postfix", "postfix");
            Add("WorldGrid.GetGizmos", typeof(CelestialBodyLayerManager), "GetGizmos_Postfix", "postfix");
            return result.AsReadOnly();
        }
        public static IReadOnlyList<CelestialNativeContractEntry> CaptureInstalledHooks() => ExpectedPatches().Select(binding => {
            var info = binding.Target == null ? null : Harmony.GetPatchInfo(binding.Target);
            var patches = binding.Kind switch { "prefix" => info?.Prefixes, "postfix" => info?.Postfixes, "transpiler" => info?.Transpilers, _ => info?.Finalizers };
            return new CelestialNativeContractEntry(binding.Name, binding.Handler != null && patches?.Any(patch => patch.PatchMethod == binding.Handler) == true,
                binding.Target?.DeclaringType?.FullName + "." + binding.Target?.Name);
        }).ToArray();
        public static CelestialResult<bool> ValidateInstalledHooks(params string[] required)
        {
            var report = CaptureInstalledHooks();
            var errors = report.Where(entry => (required.Length == 0 || required.Contains(entry.Name, StringComparer.Ordinal)) && !entry.Available)
                .Select(entry => new CelestialError("NativePatchMissing", Detail: entry.Name)).Concat(required.Where(name => !report.Any(entry => entry.Name == name))
                    .Select(name => new CelestialError("NativePatchMissing", Detail: name))).ToArray();
            return errors.Length == 0 ? CelestialResult.Ok(true) : CelestialResult.Reject<bool>(errors);
        }
        internal static void RequireInstalledHooks(params string[] required)
        {
            var result = ValidateInstalledHooks(required);
            if (!result.Success) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Code + ": " + error.Detail)));
        }
        internal static MethodInfo RequireMethod(string key) => contracts.Value.Methods.TryGetValue(key, out var method) ? method :
            throw new InvalidOperationException("NativeContractMissing: " + key);
        public static CelestialResult<bool> ValidateNativeContracts()
        {
            var errors = Report.Where(entry => !entry.Available).Select(entry => new CelestialError("NativeContractMissing", Detail: entry.Name + ": " + entry.Detail)).ToArray();
            return errors.Length == 0 ? CelestialResult.Ok(true) : CelestialResult.Reject<bool>(errors);
        }
        public static CelestialResult<PlanetLayerDef> ResolveRootDefinition(Scenario? scenario)
        {
            var part = ResolveRootPart(scenario);
            return part.Success ? CelestialResult.Ok(part.Value.layer) : CelestialResult.Reject<PlanetLayerDef>(part.Errors.ToArray());
        }
        internal static CelestialResult<ScenPart_PlanetLayer> ResolveRootPart(Scenario? scenario)
        {
            if (contracts.Value.SurfaceLayer == null) return CelestialResult.Reject<ScenPart_PlanetLayer>(new CelestialError("NativeContractMissing", Detail: "Scenario.surfaceLayer"));
            if (scenario == null || contracts.Value.SurfaceLayer.GetValue(scenario) is not ScenPart_PlanetLayer part || part.layer == null)
                return CelestialResult.Reject<ScenPart_PlanetLayer>(new CelestialError("RootLayerAdapterMissing"));
            return CelestialResult.Ok(part);
        }
        private static Contracts Inspect()
        {
            var methods = new Dictionary<string, MethodInfo>(StringComparer.Ordinal); var report = new List<CelestialNativeContractEntry>();
            void Method(string key, Type type, string name, Type returns, bool isStatic, params Type[] parameters)
            {
                var method = AccessTools.Method(type, name, parameters);
                bool valid = method != null && method.ReturnType == returns && method.IsStatic == isStatic;
                report.Add(new(key, valid, type.FullName + "." + name + "(" + string.Join(",", parameters.Select(parameter => parameter.Name)) + ")"));
                if (valid) methods.Add(key, method!);
            }
            var field = AccessTools.Field(typeof(Scenario), "surfaceLayer");
            bool validField = field != null && !field.IsStatic && field.FieldType == typeof(ScenPart_PlanetLayer);
            report.Add(new("Scenario.surfaceLayer", validField, "instance ScenPart_PlanetLayer"));
            Method("WorldGrid.GenerateFreshWorld", typeof(WorldGrid), nameof(WorldGrid.GenerateFreshWorld), typeof(void), false);
            Method("WorldGrid.CreateRequiredLayers", typeof(WorldGrid), "CreateRequiredLayers", typeof(void), false);
            Method("WorldGrid.RegisterPlanetLayer", typeof(WorldGrid), nameof(WorldGrid.RegisterPlanetLayer), typeof(PlanetLayer), false,
                typeof(PlanetLayerDef), typeof(Vector3?), typeof(float), typeof(float), typeof(float), typeof(int), typeof(float), typeof(float), typeof(Vector3?));
            Method("WorldGrid.RegisterPlanetLayer.Settings", typeof(WorldGrid), nameof(WorldGrid.RegisterPlanetLayer), typeof(PlanetLayer), false,
                typeof(PlanetLayerDef), typeof(PlanetLayerSettings), typeof(float?), typeof(Vector3?), typeof(Vector3?), typeof(float?));
            Method("WorldGrid.GetGizmos", typeof(WorldGrid), nameof(WorldGrid.GetGizmos), typeof(IEnumerable<Gizmo>), false);
            Method("WorldGenerator.GeneratePlanetLayer", typeof(WorldGenerator), nameof(WorldGenerator.GeneratePlanetLayer), typeof(void), true, typeof(PlanetLayer), typeof(string), typeof(int));
            Method("WorldGenerator.GenerateWorld", typeof(WorldGenerator), nameof(WorldGenerator.GenerateWorld), typeof(World), true, typeof(float), typeof(string),
                typeof(OverallRainfall), typeof(OverallTemperature), typeof(OverallPopulation), typeof(LandmarkDensity), typeof(List<FactionDef>), typeof(float));
            Method("Game.DeinitAndRemoveMap", typeof(Game), nameof(Game.DeinitAndRemoveMap), typeof(void), false, typeof(Map), typeof(bool));
            Method("WorldObjectsHolder.Remove", typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Remove), typeof(void), false, typeof(WorldObject));
            Method("GameDataSaveLoader.SaveGame", typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame), typeof(void), true, typeof(string));
            Method("MapGenerator.GenerateContentsIntoMap", typeof(MapGenerator), "GenerateContentsIntoMap", typeof(void), true,
                typeof(IEnumerable<GenStepWithParams>), typeof(Map), typeof(int), typeof(bool));
            Method("MapGenerator.GenerateMap", typeof(MapGenerator), "GenerateMap", typeof(Map), true,
                typeof(IntVec3), typeof(MapParent), typeof(MapGeneratorDef), typeof(IEnumerable<GenStepWithParams>), typeof(Action<Map>), typeof(bool), typeof(bool));
            Method("MapGenerator.MapGeneratorPostInit", typeof(MapGenerator), "MapGeneratorPostInit", typeof(void), true,
                typeof(IEnumerable<GenStepWithParams>), typeof(Map));
            Method("PlanetLayer.InitializeLayer", typeof(PlanetLayer), "InitializeLayer", typeof(void), false);
            return new(validField ? field : null, methods, report.AsReadOnly());
        }
        private sealed record Contracts(FieldInfo? SurfaceLayer, Dictionary<string, MethodInfo> Methods, IReadOnlyList<CelestialNativeContractEntry> Report);
    }
}
