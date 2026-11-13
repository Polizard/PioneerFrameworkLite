using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PioneerFramework.Celestial;

namespace PioneerFramework
{
    /// <summary>
    /// 行星种类人工验收场景。只覆盖新实例的演示轨道，原始 Def 和默认地月系保持不变。
    /// 2026-11-09（用户授权，后端变更）起，测试天体绕**主光源恒星**（默认地月系 = 太阳）布置在分离的
    /// 开普勒圆轨上；旧版把它们挂到根天体（地球）下，绝对状态 = Earth(t) + 局部轨道，Earth 绕日的
    /// 12,910.7 m/s 是公共项而局部速度只有 686–1,721 m/s ⇒ PFView 里六颗看起来全部以地球角速度绕恒星转。
    /// 证据 / 判据 / 回退见 DesignNotes/后端变更授权-2026-11-09-行星种类测试天体绕恒星.md。
    /// 标记进入正常描述/存档；清理仍经框架引用检查，不能绕过地图、飞船或主地表保护。
    /// </summary>
    internal static class PlanetKindDebugScenario
    {
        internal const string TestTag = "PFL.Debug.PlanetKinds";
        private static readonly int[] orbitNormal = { 0, 1, 0 };
        internal static IReadOnlyList<string> DefinitionIds { get; } = Array.AsReadOnly(new[] {
            "PF_BarrenPlanet", "PF_TemperatePlanet", "PF_GreenhousePlanet", "PF_FrozenPlanet", "PF_GasGiant", "PF_IceGiant" });

        internal static bool IsTestBody(CelestialBodyDescription body)
            => body.Tags.Contains(TestTag, StringComparer.Ordinal);

        internal static IReadOnlyList<string> Variants(string definition) => definition switch {
            "PF_BarrenPlanet" => new[] { "Default", "ThinAir", "Synchronous" },
            "PF_TemperatePlanet" => new[] { "Default", "Ocean", "Arid" },
            "PF_FrozenPlanet" => new[] { "Default", "Atmospheric" }, _ => new[] { "Default" } };

        internal static CelestialResult<CelestialSpawnManifest> Build(IReadOnlyList<CelestialBodyDef> definitions, ulong seed,
            string operation, CelestialInstanceId parent, CelestialInstanceId light, double parentGm, double outerEdge)
            => BuildCore(definitions, seed, operation, parent, light, parentGm, outerEdge, "Default");

        internal static CelestialResult<CelestialSpawnManifest> BuildVariant(CelestialBodyDef definition, string variant, ulong seed,
            string operation, CelestialInstanceId parent, CelestialInstanceId light, double parentGm, double outerEdge)
            => !Variants(definition.defName).Contains(variant) ? Reject("UnknownPlanetKindVariant") :
                BuildCore(new[] { definition }, seed, operation, parent, light, parentGm, outerEdge, variant);

        private static CelestialResult<CelestialSpawnManifest> BuildCore(IReadOnlyList<CelestialBodyDef> definitions, ulong seed,
            string operation, CelestialInstanceId parent, CelestialInstanceId light, double parentGm, double outerEdge, string variant)
        {
            if (definitions.Count == 0 || definitions.Any(d => d == null || !DefinitionIds.Contains(d.defName, StringComparer.Ordinal)) ||
                definitions.Select(d => d.defName).Distinct(StringComparer.Ordinal).Count() != definitions.Count ||
                string.IsNullOrWhiteSpace(operation) || !parent.IsValid || !light.IsValid || !double.IsFinite(parentGm) || parentGm <= 0 ||
                !double.IsFinite(outerEdge) || outerEdge <= 0)
                return Reject("InvalidPlanetKindTestRequest");
            var items = new List<CelestialSpawnItem>();
            foreach (var definition in definitions.OrderBy(d => DefinitionIds.IndexOf(d.defName)))
            {
                string path = operation + "/" + definition.defName;
                var overrides = variant is "Ocean" or "Arid" ? new[] {
                    new CelestialParameterOverride("biomes", "terrain.oceanCoverage", variant == "Ocean" ? .8 : .02),
                    new CelestialParameterOverride("environment", "climate.rainfallMillimeters", variant == "Ocean" ? 1500 : 100) } : Array.Empty<CelestialParameterOverride>();
                var context = new CelestialGenerationContext(seed, path, parameterOverrides: overrides);
                var original = definition.ToTemplate();
                if (!original.Success) return CelestialResult.Reject<CelestialSpawnManifest>(original.Errors.ToArray());
                if (variant is "ThinAir" or "Atmospheric")
                {
                    var biome = Verse.DefDatabase<RimWorld.BiomeDef>.GetNamedSilentFail(variant == "ThinAir" ? "Desert" : "IceSheet");
                    if (biome == null) return Reject("PlanetKindVariantBiomeUnavailable");
                    var replacements = new CelestialCompProperties[] {
                        new CompProperties_Atmosphere { key = "air", pressurePascals = new() { value = variant == "ThinAir" ? 600 : 150000 },
                            temperatureKelvin = new() { value = variant == "ThinAir" ? 230 : 100 }, topAltitude = new() { value = 100000 },
                            composition = new() { new() { gas = "Nitrogen", moleFraction = 1 } } },
                        new CompProperties_ExistingEnvironment { key = "environment", profile = new() { defName = "debug/" + variant,
                            climate = new() { meanTemperatureCelsius = variant == "ThinAir" ? -30 : -160, rainfallMillimeters = 0, humidity = 0 },
                            exposure = new() { gravityStats = true } } },
                        new CompProperties_BiomeGeneration { key = "biomes", useEnvironmentClimate = true, profile = new() {
                            defName = "debug/" + variant, mode = CelestialBiomeGenerationMode.Parameterized,
                            terrain = new() { oceanCoverage = new() { value = 0 }, biomeRules = new() { new() { key = "fallback", biome = biome, fallback = true } } } } } };
                    var keys = replacements.Select(c => c.key).ToHashSet(StringComparer.Ordinal);
                    original = CelestialBodyTemplate.Create(original.Value.DefinitionId, original.Value.Label, original.Value.Category, original.Value.Tags,
                        original.Value.Components.Where(c => !keys.Contains(c.Descriptor.Key)).Concat(replacements.Select(c => c.ToDefinition())));
                    if (!original.Success) return CelestialResult.Reject<CelestialSpawnManifest>(original.Errors.ToArray());
                }
                var resolved = original.Value.Resolve(context);
                if (!resolved.Success) return CelestialResult.Reject<CelestialSpawnManifest>(resolved.Errors.ToArray());
                var geometry = resolved.Value.Select(c => c.Properties).OfType<ICelestialGeometryProperties>().Single();
                var binding = resolved.Value.SingleOrDefault(c => c.Properties is ICelestialPhysicsBindingProperties);
                if (binding?.Properties is not ICelestialPhysicsBindingProperties physics) return Reject("PhysicsBindingMissing");
                var parameters = ExistingEphemerisParameters.Parse(physics.Initialization);
                if (!parameters.Success) return CelestialResult.Reject<CelestialSpawnManifest>(parameters.Errors.ToArray());
                double radius = outerEdge + geometry.RadiusMeters + 60000000;
                double period = 2 * Math.PI * Math.Sqrt(radius * radius * radius / parentGm);
                if (!double.IsFinite(radius) || !double.IsFinite(period) || period <= 0) return Reject("InvalidPlanetKindTestOrbit");
                var initialization = new PhysicsInitialization(ExistingCelestialEphemerisBridge.Id, "existing.circular.v1", 1,
                    JsonSerializer.SerializeToUtf8Bytes(new {
                        orbitRadius = radius, orbitPeriod = period, initialPhase = items.Count * Math.PI / 3,
                        orbitNormal = PlanetKindDebugScenario.orbitNormal, dayLengthTicks = parameters.Value.DayLengthTicks,
                        bodyType = "Planet", frameOriginReference = "frameOrigin" }));
                if (parameters.Value.UsesAuthoritativeRotation)
                {
                    var p = parameters.Value;
                    initialization = new CelestialMotionXml {
                        orbitRadiusMeters = radius, orbitPeriodSeconds = period, orbitPhaseRadians = items.Count * Math.PI / 3,
                        bodyType = "Planet", frameOriginReference = "frameOrigin", rotation = new() {
                            axisX = p.RotationAxis.x, axisY = p.RotationAxis.y, axisZ = p.RotationAxis.z,
                            phaseRadians = p.RotationPhaseRadians, retrograde = p.Retrograde,
                            periodSeconds = variant != "Synchronous" && p.SynchronousTargetReference.Length == 0 ? p.DayLengthTicks : null,
                            synchronousTargetReference = variant == "Synchronous" ? "synchronousTarget" : p.SynchronousTargetReference } }.Compile();
                }
                var replacement = new ExternalPhysicsResolvedProperties(physics.Key, physics.GravitationalParameter, initialization);
                var motion = new CelestialCompDefinition(binding.Descriptor, binding.Factory,
                    _ => CelestialResult.Ok<ICelestialResolvedProperties>(replacement));
                var template = CelestialBodyTemplate.Create(original.Value.DefinitionId, original.Value.Label, original.Value.Category,
                    original.Value.Tags.Append(TestTag), original.Value.Components.Select(c => c.Descriptor.Key == physics.Key ? motion : c));
                if (!template.Success) return CelestialResult.Reject<CelestialSpawnManifest>(template.Errors.ToArray());
                var components = template.Value.Resolve(context);
                if (!components.Success) return CelestialResult.Reject<CelestialSpawnManifest>(components.Errors.ToArray());
                var references = new Dictionary<string, CelestialInstanceId> { ["frameOrigin"] = parent, ["primaryLight"] = light };
                string target = ExistingEphemerisParameters.Parse(initialization).Value.SynchronousTargetReference;
                if (target.Length > 0) references[target] = parent;
                items.Add(new(CelestialInstanceId.Parse("planet-kind-test/" + path), definition.defName, 0, path,
                    template.Value, components.Value, parent, references));
                outerEdge = radius + geometry.RadiusMeters;
            }
            return CelestialResult.Ok(new CelestialSpawnManifest("PFL.Debug.PlanetKinds", seed, null, items));
        }

        private static int IndexOf(this IReadOnlyList<string> values, string value)
        {
            for (int i = 0; i < values.Count; i++) if (values[i] == value) return i;
            return int.MaxValue;
        }
        private static CelestialResult<CelestialSpawnManifest> Reject(string code)
            => CelestialResult.Reject<CelestialSpawnManifest>(new CelestialError(code));
    }

    /// <summary>同一 runtime 的测试窗口共享请求状态；关闭窗口不会遗失 pending，也不会重放旧动作。</summary>
    internal sealed class PlanetKindDebugSession
    {
        private static readonly ConditionalWeakTable<CelestialRuntime, PlanetKindDebugSession> sessions = new();
        private const string OperationPrefix = "planet-kind-test/";
        private readonly CelestialRuntime runtime;
        private readonly HashSet<string> pending = new(StringComparer.Ordinal);
        private readonly HashSet<long> ownedEpochs = new();
        private long? epoch;
        internal CelestialOperation? LastOperation { get; private set; }
        internal IReadOnlyList<CelestialError> Errors { get; private set; } = Array.Empty<CelestialError>();
        internal int PendingCount => pending.Count;
        internal static PlanetKindDebugSession For(CelestialRuntime runtime) => sessions.GetValue(runtime, value => new(value));
        private PlanetKindDebugSession(CelestialRuntime runtime)
        {
            this.runtime = runtime;
            var records = runtime.WithPublication(() => runtime.Operations.Capture());
            var open = runtime.CaptureEpochUsage().Select(usage => usage.Epoch).ToHashSet();
            foreach (var group in records.GroupBy(record => CelestialOperationEpochs.TryGetEpoch(record.Operation.OperationId, out long value) ? value : -1))
            {
                // An epoch containing another module's action is never ours to retire.
                if (group.Key <= 0 || !open.Contains(group.Key) || group.Any(record => !OwnsOperation(record.Operation.OperationId))) continue;
                ownedEpochs.Add(group.Key);
                foreach (var record in group) Track(record.Operation);
            }
        }

        private static bool OwnsOperation(string id)
        {
            if (!id.StartsWith("epoch/", StringComparison.Ordinal)) return false;
            int separator = id.IndexOf('/', 6);
            if (separator < 0) return false;
            string name = Uri.UnescapeDataString(id[(separator + 1)..]);
            return name.StartsWith(OperationPrefix, StringComparison.Ordinal) && Guid.TryParseExact(name[OperationPrefix.Length..], "N", out _);
        }

        internal void Refresh()
        {
            if (runtime.IsRestoring) return;
            foreach (string id in pending.ToArray())
            {
                var operation = runtime.GetOperation(id);
                if (LastOperation?.OperationId == id) LastOperation = operation;
                Errors = Errors.Concat(operation.Errors).Distinct().ToArray();
                if (operation.Status != CelestialOperationStatus.Pending) pending.Remove(id);
            }
            if (pending.Count == 0 && ownedEpochs.Count > 0)
            {
                var records = runtime.WithPublication(() => runtime.Operations.Capture());
                var open = runtime.CaptureEpochUsage().Select(usage => usage.Epoch).ToHashSet();
                foreach (long owner in ownedEpochs.ToArray())
                {
                    bool shared = records.Any(record => CelestialOperationEpochs.TryGetEpoch(record.Operation.OperationId, out long value) &&
                        value == owner && !OwnsOperation(record.Operation.OperationId));
                    if (shared || !open.Contains(owner))
                    {
                        ownedEpochs.Remove(owner);
                        if (epoch == owner) epoch = null;
                        if (shared) Errors = Errors.Append(new CelestialError("PlanetKindTestSharedEpoch")).Distinct().ToArray();
                        continue;
                    }
                    var closed = runtime.CloseEpoch(runtime.WorldSessionId, owner);
                    if (closed.Success) { ownedEpochs.Remove(owner); if (epoch == owner) epoch = null; }
                    else Errors = Errors.Concat(closed.Errors).Distinct().ToArray();
                }
            }
        }

        internal CelestialResult<CelestialOperationContext> Context()
        {
            if (runtime.IsRestoring) return CelestialResult.Reject<CelestialOperationContext>(new CelestialError("RestorePending"));
            Refresh();
            if (pending.Count > 0) return CelestialResult.Reject<CelestialOperationContext>(new CelestialError("PlanetKindTestsPending"));
            if (ownedEpochs.Count > 0) return CelestialResult.Reject<CelestialOperationContext>(new CelestialError("HostWorkPending"));
            if (!epoch.HasValue)
            {
                var opened = runtime.OpenEpoch(runtime.WorldSessionId);
                if (!opened.Success) return CelestialResult.Reject<CelestialOperationContext>(opened.Errors.ToArray());
                epoch = opened.Value;
                ownedEpochs.Add(opened.Value);
            }
            Errors = Array.Empty<CelestialError>();
            return CelestialResult.Ok(NewContext());
        }

        private CelestialOperationContext NewContext() => new(CelestialOperationEpochs.Qualify(epoch!.Value,
            OperationPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)), runtime.WorldSessionId, runtime.CaptureCatalog().Revision);

        internal void Track(CelestialOperation operation)
        {
            LastOperation = operation;
            Errors = Errors.Concat(operation.Errors).Distinct().ToArray();
            if (operation.Status == CelestialOperationStatus.Pending) pending.Add(operation.OperationId);
        }

        internal CelestialOperation Create(IReadOnlyList<CelestialBodyDef> definitions, string variant = "Default")
        {
            var context = Context();
            if (!context.Success) return Rejected(context.Errors);
            var rootId = runtime.Creation.RootSurfaceId;
            if (!rootId.HasValue || !runtime.Query(rootId.Value).Success ||
                !runtime.TryGetInitializationReference(rootId.Value, "primaryLight", out var light) || !runtime.Query(light).Success ||
                runtime.GetCapabilities<ICelestialLightSourceCapability>(light).Count != 1 ||
                runtime.Physics is not ExistingCelestialEphemerisBridge bridge)
                return Rejected(new[] { new CelestialError("PlanetKindTestContextUnavailable") }, context.Value.OperationId);
            var root = runtime.Query(rootId.Value).Value;
            // 2026-11-09（用户授权）：测试天体改绕主光源（默认地月系 = 太阳）公转。旧版 parent = 根天体（地球），
            // 六颗变成地球卫星；绝对状态 = Earth(t) + 局部圆轨，Earth 绕日的 12,910.7 m/s 主导 ⇒ PFView 里
            // 看起来全部以地球角速度绕恒星转。父链只沿 ParentInstanceId 累加，所以只改 frameOrigin 无效。
            var parent = light;
            var parentBody = runtime.Query(parent).Value;
            double parentGm = parentBody.Description.GravitationalParameter ?? 0;
            double edge;
            if (parent == rootId.Value) edge = root.Description.RadiusMeters;   // 根天体就是恒星：沿用旧起点
            else if (root.ParentId == parent && bridge.TryGetParameters(rootId.Value, out var rootOrbit) && rootOrbit.HasOrbit)
                edge = rootOrbit.OrbitRadius + root.Description.RadiusMeters;   // 从根行星轨道外缘之外布带（默认：地球轨道之外）
            else edge = Math.Max(parentBody.Description.RadiusMeters, root.Description.RadiusMeters);
            // 同一父体下已有测试天体继续向外扩展；旧版挂在根天体下的实例不参与新带的起点计算。
            foreach (var body in runtime.CaptureCatalog().Bodies.Select(d => runtime.Query(d.Id).Value).Where(b => PlanetKindDebugScenario.IsTestBody(b.Description)))
                if (body.ParentId == parent && bridge.TryGetParameters(body.Id, out var parameters))
                    edge = Math.Max(edge, parameters.OrbitRadius + body.Description.RadiusMeters);
            var manifest = variant == "Default" ? PlanetKindDebugScenario.Build(definitions, runtime.WorldSeed, context.Value.OperationId, parent,
                light, parentGm, edge) : definitions.Count == 1 ?
                PlanetKindDebugScenario.BuildVariant(definitions[0], variant, runtime.WorldSeed, context.Value.OperationId, parent,
                    light, parentGm, edge) : CelestialResult.Reject<CelestialSpawnManifest>(new CelestialError("InvalidVariantBatch"));
            var operation = manifest.Success ? runtime.CreateBatch(new(context.Value, manifest.Value)) :
                new CelestialOperation(context.Value.OperationId, CelestialOperationStatus.Rejected, errors: manifest.Errors);
            Track(operation);
            return operation;
        }

        internal IReadOnlyList<CelestialOperation> RemoveAll()
        {
            var context = Context();
            if (!context.Success) return new[] { Rejected(context.Errors) };
            var operations = new List<CelestialOperation>();
            foreach (var body in runtime.CaptureCatalog().Bodies.Select(d => runtime.Query(d.Id).Value).Where(b => PlanetKindDebugScenario.IsTestBody(b.Description)))
            {
                var check = runtime.CanRemove(body.Id);
                var next = NewContext();
                var operation = check.Allowed ? runtime.Remove(new(next, body.Id)) :
                    new CelestialOperation(next.OperationId, CelestialOperationStatus.Rejected, new[] { body.Id }, check.Blockers);
                Track(operation);
                operations.Add(operation);
            }
            return operations;
        }

        private CelestialOperation Rejected(IEnumerable<CelestialError> errors, string id = "planet-kind-test/rejected")
        {
            var operation = new CelestialOperation(id, CelestialOperationStatus.Rejected, errors: errors);
            Track(operation);
            return operation;
        }
    }
}
