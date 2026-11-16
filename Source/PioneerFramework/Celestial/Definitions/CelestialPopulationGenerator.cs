using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace PioneerFramework.Celestial
{
    internal sealed record CelestialPopulationExpansion(CelestialSpawnManifest Manifest, CelestialPopulationDiagnostics Diagnostics);

    internal sealed class CelestialPopulationDiagnostics
    {
        private readonly SortedDictionary<string, int> rejections = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, string> skippedSlots = new(StringComparer.Ordinal);
        internal int RequestedPlanets { get; init; }
        internal int RequestedDwarfs { get; init; }
        internal int RequestedMoons { get; set; }
        internal int AcceptedPlanets { get; set; }
        internal int AcceptedDwarfs { get; set; }
        internal int AcceptedMoons { get; set; }
        internal int Attempts { get; set; }
        internal IReadOnlyDictionary<string, int> Rejections => new ReadOnlyDictionary<string, int>(rejections);
        internal IReadOnlyDictionary<string, string> SkippedSlots => new ReadOnlyDictionary<string, string>(skippedSlots);
        internal void Reject(string path, string reason)
        {
            rejections[reason] = rejections.GetValueOrDefault(reason) + 1;
            skippedSlots[path] = reason;
        }
        internal void Accept(string path) => skippedSlots.Remove(path);

        public override string ToString() => FormattableString.Invariant(
            $"planets {AcceptedPlanets}/{RequestedPlanets}, dwarf planets {AcceptedDwarfs}/{RequestedDwarfs}, moons {AcceptedMoons}/{RequestedMoons}, attempts {Attempts}") +
            (rejections.Count == 0 ? "" : ", rejected " + string.Join(", ", rejections.Select(pair => FormattableString.Invariant($"{pair.Key}={pair.Value}")))) +
            (skippedSlots.Count == 0 ? "" : ", skipped " + string.Join(", ", skippedSlots.Select(pair => pair.Key + "=" + pair.Value)));
    }

    /// <summary>Builds only resolved spawn records. The caller submits the complete manifest as one bootstrap batch.</summary>
    internal static class CelestialPopulationGenerator
    {
        private const string Domain = "$population/v1";

        internal static CelestialResult<CelestialPopulationExpansion> Append(CelestialSpawnManifest authored, int maxInstances,
            CelestialPopulationPolicyDef policy)
        {
            var rules = policy.ResolveRules();
            if (!rules.Success) return CelestialResult.Reject<CelestialPopulationExpansion>(rules.Errors.ToArray());
            var byId = authored.Items.ToDictionary(item => item.Id);
            var reference = byId[authored.RootSurfaceId!.Value];
            CelestialSpawnItem? star = null;
            while (reference.ParentId is { } parentId)
            {
                var parent = byId[parentId];
                if (parent.Template.HasCapability("light.source")) { star = parent; break; }
                reference = parent;
            }
            if (star == null || ReadPhysical(star) is not { Gm: > 0 } stellar || ReadPhysical(reference) is not { Orbit.HasOrbit: true } home)
                return Reject("PopulationStellarReferenceUnavailable");

            // The authored Earth–Moon orbit uses a different scale from the solar system. Reserve its actual subtree reach.
            var children = authored.Items.Where(item => item.ParentId.HasValue).ToLookup(item => item.ParentId!.Value);
            var subtree = new Stack<CelestialSpawnItem>(children[star.Id]);
            var physical = new List<(CelestialSpawnItem Item, Physical Body)>();
            while (subtree.TryPop(out var member))
            {
                if (ReadPhysical(member) is not { } body) return Reject("PopulationAuthoredOrbitUnavailable", member.Id.Value);
                physical.Add((member, body));
                foreach (var child in children[member.Id]) subtree.Push(child);
            }
            var extents = new Dictionary<CelestialInstanceId, double>();
            var occupied = new List<CelestialRadialEnvelope>();
            foreach (var (item, body) in physical.AsEnumerable().Reverse())
            {
                double extent = Math.Max(body.Radius, extents.GetValueOrDefault(item.Id));
                if (item.ParentId == star.Id)
                    occupied.Add(new(body.Orbit.OrbitRadius, body.Orbit.PeriapsisRadius - extent, body.Orbit.ApoapsisRadius + extent));
                else
                {
                    var parentId = item.ParentId!.Value;
                    extents[parentId] = Math.Max(extents.GetValueOrDefault(parentId), body.Orbit.ApoapsisRadius + extent);
                }
            }

            string basePath = star.GenerationPath + "/" + Domain;
            var counts = new CelestialGenerationContext(authored.WorldSeed, basePath).ForComp("counts");
            var diagnostics = new CelestialPopulationDiagnostics {
                RequestedPlanets = counts.SampleInt("planets", IntParameterSpec.Uniform(policy.minimumPlanets, policy.maximumPlanets)),
                RequestedDwarfs = counts.SampleInt("dwarfPlanets", IntParameterSpec.Uniform(policy.minimumDwarfs, policy.maximumDwarfs))
            };
            var bands = policy.distanceBands.OrderBy(band => band.minimum).ToArray();
            double irradianceScale = Math.Sqrt(policy.referenceIrradiance);
            var stellarParent = new OrbitalParent(star, stellar.Gm, stellar.Radius, home.Orbit.OrbitNormal);
            var items = authored.Items.ToList();
            var moons = new List<(Candidate Candidate, CelestialSpawnItem Item)>();
            AddPlanets(CelestialPopulationRole.Planet, diagnostics.RequestedPlanets);
            AddPlanets(CelestialPopulationRole.DwarfPlanet, diagnostics.RequestedDwarfs);
            foreach (var (candidate, item) in moons) AddMoons(candidate, item);
            return CelestialResult.Ok(new CelestialPopulationExpansion(
                new(authored.SystemDefinitionId, authored.WorldSeed, authored.RootSurfaceId, items), diagnostics));

            void AddPlanets(CelestialPopulationRole role, int count)
            {
                string key = role == CelestialPopulationRole.Planet ? "planet" : "dwarf";
                var weights = bands.Select(band => role == CelestialPopulationRole.Planet ? band.planetWeight : band.dwarfWeight).ToArray();
                for (int slot = 0; slot < count; slot++)
                {
                    string path = SlotPath(basePath, key, slot);
                    for (int attempt = 0; attempt < policy.attemptsPerSlot; attempt++)
                    {
                        if (items.Count >= maxInstances) { diagnostics.Reject(path, "InstanceLimit"); break; }
                        diagnostics.Attempts++;
                        var random = CandidateContext(authored.WorldSeed, path, attempt);
                        int bandIndex = PlanetBand(role, slot, weights, random);
                        var band = bands[bandIndex];
                        double distance = LogRange(random, "stellarDistance", band.minimum, band.maximum);
                        double irradianceDistance = distance / irradianceScale;
                        var rule = ChooseRule(rules.Value, role, irradianceDistance, random);
                        if (rule == null) { diagnostics.Reject(path, "NoBodyRuleInBand"); continue; }
                        var candidate = CreateCandidate(rule, irradianceDistance, random);
                        candidate.SemiMajor = home.Orbit.OrbitRadius * distance / Math.Pow(1 - candidate.Eccentricity * candidate.Eccentricity, .25);
                        double reach = candidate.Radius;
                        if (rule.Settings.moonCountWeights.Skip(1).Any(weight => weight > 0))
                            reach = Math.Max(reach, policy.hillFraction * HillRadius(candidate, stellar.Gm));
                        var envelope = CelestialRadialEnvelope.Around(candidate.SemiMajor, candidate.Eccentricity, reach);
                        if (!double.IsFinite(envelope.Outer) || envelope.Inner <= stellar.Radius)
                        { diagnostics.Reject(path, "StellarClearance"); continue; }
                        if (occupied.Any(other => envelope.Overlaps(other, policy.minimumOrbitSeparation * Math.Min(envelope.SemiMajor, other.SemiMajor))))
                        { diagnostics.Reject(path, "StellarOrbitOverlap"); continue; }
                        int displayIndex = role == CelestialPopulationRole.Planet ? slot + 1 : slot + 1 + policy.maximumPlanets;
                        var built = Build(authored, candidate, key, slot, path, stellarParent, star.Id, displayIndex, random);
                        if (!built.Success) { diagnostics.Reject(path, "Resolution/" + built.Errors[0].Code); continue; }
                        items.Add(built.Value);
                        occupied.Add(envelope);
                        moons.Add((candidate, built.Value));
                        if (role == CelestialPopulationRole.Planet) diagnostics.AcceptedPlanets++;
                        else diagnostics.AcceptedDwarfs++;
                        diagnostics.Accept(path);
                        break;
                    }
                }
            }

            void AddMoons(Candidate planet, CelestialSpawnItem parent)
            {
                var weights = planet.Rule.Settings.moonCountWeights;
                if (weights.Count == 0) return;
                var countRandom = new CelestialGenerationContext(authored.WorldSeed, parent.GenerationPath).ForComp("moonCount");
                int count = WeightedIndex(weights, countRandom, "target");
                diagnostics.RequestedMoons += count;
                double hillLimit = policy.hillFraction * HillRadius(planet, stellar.Gm);
                double budget = planet.Gm * policy.satelliteMassFraction;
                var parentOrbit = ReadPhysical(parent)!.Orbit;
                var orbitalParent = new OrbitalParent(parent, planet.Gm, planet.Radius, parentOrbit.OrbitNormal);
                var siblings = new List<(CelestialRadialEnvelope Orbit, double Gm)>();
                for (int slot = 0; slot < count; slot++)
                {
                    string path = SlotPath(parent.GenerationPath, "moon", slot);
                    for (int attempt = 0; attempt < policy.attemptsPerSlot; attempt++)
                    {
                        if (items.Count >= maxInstances) { diagnostics.Reject(path, "InstanceLimit"); break; }
                        diagnostics.Attempts++;
                        var random = CandidateContext(authored.WorldSeed, path, attempt);
                        var rule = ChooseRule(rules.Value, CelestialPopulationRole.Moon, planet.Distance, random);
                        if (rule == null) { diagnostics.Reject(path, "NoMoonRuleInBand"); continue; }
                        var candidate = CreateCandidate(rule, planet.Distance, random);
                        if (candidate.Radius > .3 * planet.Radius || candidate.Gm >= budget)
                        { diagnostics.Reject(path, "SatelliteMassOrSize"); continue; }
                        double roche = CelestialPopulationOrbits.RocheRadius(planet.Radius, planet.Density, candidate.Density);
                        double minimum = (1.15 * Math.Max(planet.Radius, roche) + candidate.Radius) / (1 - candidate.Eccentricity);
                        double maximum = (hillLimit - candidate.Radius) / (1 + candidate.Eccentricity);
                        if (maximum <= minimum) { diagnostics.Reject(path, "SatelliteRocheOrHill"); continue; }
                        candidate.SemiMajor = LogRange(random, "satelliteDistance", minimum, maximum);
                        var envelope = CelestialRadialEnvelope.Around(candidate.SemiMajor, candidate.Eccentricity, candidate.Radius);
                        if (siblings.Any(other => envelope.Overlaps(other.Orbit, CelestialPopulationOrbits.MutualHillClearance(
                            envelope.SemiMajor, other.Orbit.SemiMajor, candidate.Gm + other.Gm, planet.Gm))))
                        { diagnostics.Reject(path, "SatelliteOrbitOverlap"); continue; }
                        var built = Build(authored, candidate, "moon", slot, path, orbitalParent, star.Id, slot + 1, random);
                        if (!built.Success) { diagnostics.Reject(path, "Resolution/" + built.Errors[0].Code); continue; }
                        siblings.Add((envelope, candidate.Gm));
                        budget -= candidate.Gm;
                        items.Add(built.Value);
                        diagnostics.AcceptedMoons++;
                        diagnostics.Accept(path);
                        break;
                    }
                }
            }
        }

        private static Physical? ReadPhysical(CelestialSpawnItem item)
        {
            var geometry = item.Components.Select(component => component.Properties).OfType<ICelestialGeometryProperties>().SingleOrDefault();
            var binding = item.Components.Select(component => component.Properties).OfType<ICelestialPhysicsBindingProperties>().SingleOrDefault();
            if (geometry == null || binding?.GravitationalParameter is not { } gm || gm < 0) return null;
            var orbit = ExistingEphemerisParameters.Parse(binding.Initialization);
            return orbit.Success ? new Physical(geometry.RadiusMeters, gm, orbit.Value) : null;
        }

        private static int PlanetBand(CelestialPopulationRole role, int slot, double[] weights, CelestialGenerationContext random)
        {
            // The first three planets preferentially sample the inner, middle, and giant zones respectively.
            int preferred = 2 * slot;
            if (role == CelestialPopulationRole.Planet && slot < 3 && preferred + 1 < weights.Length)
            {
                var pair = new[] { weights[preferred], weights[preferred + 1] };
                if (pair[0] + pair[1] > 0) return preferred + WeightedIndex(pair, random, "stellarBand");
            }
            return WeightedIndex(weights, random, "stellarBand");
        }

        private static CelestialPopulationRule? ChooseRule(IReadOnlyList<CelestialPopulationRule> rules, CelestialPopulationRole role,
            double distance, CelestialGenerationContext random)
        {
            var eligible = rules.Where(rule => rule.Settings.role == role && distance >= rule.Settings.minimumDistance &&
                distance <= rule.Settings.maximumDistance).ToArray();
            return eligible.Length == 0 ? null : eligible[WeightedIndex(eligible.Select(rule => rule.Settings.weight).ToArray(), random, "bodyRule")];
        }

        private static Candidate CreateCandidate(CelestialPopulationRule rule, double distance, CelestialGenerationContext random)
        {
            var settings = rule.Settings;
            double size = Uniform(random, "size", 0, 1);
            double radius = settings.minimumRadiusMeters + size * (settings.maximumRadiusMeters - settings.minimumRadiusMeters);
            double densityFraction = Math.Clamp(.2 + .6 * size + Uniform(random, "densityScatter", -.2, .2), 0, 1);
            double density = settings.minimumDensity + densityFraction * (settings.maximumDensity - settings.minimumDensity);
            double gm = CelestialPopulationOrbits.GravitationalParameter(radius, density);
            double eccentricity = Uniform(random, "eccentricity", .005, settings.role switch {
                CelestialPopulationRole.Moon => .18, CelestialPopulationRole.DwarfPlanet => .28, _ => .16
            });
            return new(rule, distance, radius, density, gm, eccentricity);
        }

        private static CelestialResult<CelestialSpawnItem> Build(CelestialSpawnManifest authored, Candidate candidate, string key, int slot,
            string path, OrbitalParent parent, CelestialInstanceId starId, int displayIndex, CelestialGenerationContext random)
        {
            var rule = candidate.Rule;
            var role = rule.Settings.role;
            bool moon = role == CelestialPopulationRole.Moon;
            double period = CelestialPopulationOrbits.PeriodSeconds(candidate.SemiMajor, parent.Gm + candidate.Gm);
            if (!double.IsFinite(period) || period <= 0 || !double.IsFinite(candidate.Gm) || candidate.Gm <= 0)
                return CelestialResult.Reject<CelestialSpawnItem>(new CelestialError("InvalidGeneratedMotion"));
            string bodyType = role.ToString();
            var normal = Tilt(parent.Normal.normalized, Uniform(random, "inclination", 0, moon ? .30 : .16), Angle(random, "ascendingNode"));
            var periapsis = PlaneDirection(normal, Angle(random, "periapsis"));
            var spinAxis = Tilt(normal, Uniform(random, "axialTilt", 0, .6), Angle(random, "rotationNode"));
            bool locked = moon && Uniform(random, "lock", 0, 1) < (candidate.SemiMajor < parent.Radius * 30 ? .9 : .4);
            if (locked) spinAxis = normal;
            var motion = new CelestialMotionXml {
                orbitRadiusMeters = candidate.SemiMajor, orbitPeriodSeconds = period, orbitPhaseRadians = Angle(random, "meanAnomaly"),
                orbitNormalX = normal.x, orbitNormalY = normal.y, orbitNormalZ = normal.z, bodyType = bodyType, frameOriginReference = "frameOrigin",
                rotation = new() {
                    axisX = spinAxis.x, axisY = spinAxis.y, axisZ = spinAxis.z, phaseRadians = locked ? 0 : Angle(random, "rotationPhase"),
                    periodSeconds = locked ? null : LogRange(random, "spinSeconds", rule.Settings.minimumSpinSeconds, rule.Settings.maximumSpinSeconds),
                    retrograde = !locked && Uniform(random, "spinDirection", 0, 1) < .12,
                    synchronousTargetReference = locked ? "frameOrigin" : ""
                }
            };
            var initialization = ExistingEphemerisParameters.CreateEllipticalInitialization(motion, candidate.Eccentricity, 0, periapsis,
                moon && Uniform(random, "orbitDirection", 0, 1) < .06);
            var source = rule.Template.Components.Single(component => component.Descriptor.Key == rule.MotionKey);
            var binding = new CelestialCompDefinition(source.Descriptor, source.Factory, context => CelestialResult.Ok<ICelestialResolvedProperties>(
                new ExternalPhysicsResolvedProperties(rule.MotionKey, context.SampleDouble("gravitationalParameter", DoubleParameterSpec.Fixed(candidate.Gm)), initialization)),
                source.ConfigurationVersion);
            string ordinal = displayIndex.ToString(CultureInfo.InvariantCulture);
            string label = moon ? parent.Item.Template.Label + " / " + ordinal : rule.Template.Label + " " + ordinal;
            var template = CelestialBodyTemplate.Create(rule.Template.DefinitionId, label, rule.Template.Category,
                rule.Template.Tags.Concat(new[] { "ProceduralPopulation", bodyType }).Distinct(StringComparer.Ordinal),
                rule.Template.Components.Select(component => component == source ? binding : component));
            if (!template.Success) return CelestialResult.Reject<CelestialSpawnItem>(template.Errors.ToArray());

            var overrides = new List<CelestialParameterOverride> {
                new(rule.GeometryKey, "radiusMeters", Value: candidate.Radius),
                new(rule.MotionKey, "gravitationalParameter", Value: candidate.Gm)
            };
            double change = rule.Settings.temperatureResponseKelvin * (1 / Math.Sqrt(candidate.Distance) - 1);
            if (rule.Climate is { } climate) overrides.Add(new(climate.CompKey, climate.ParameterKey, Value: Math.Clamp(climate.BaselineKelvin + change, 1, 10273.15) - 273.15));
            if (rule.Atmosphere is { } atmosphere) overrides.Add(new(atmosphere.CompKey, atmosphere.ParameterKey, Value: Math.Clamp(atmosphere.BaselineKelvin + change, 1, 10273.15)));
            var resolved = template.Value.Resolve(new(authored.WorldSeed, path, parameterOverrides: overrides));
            if (!resolved.Success) return CelestialResult.Reject<CelestialSpawnItem>(resolved.Errors.ToArray());
            var id = CelestialInstanceId.Parse(Uri.EscapeDataString(authored.SystemDefinitionId) + ":" + path);
            var references = new Dictionary<string, CelestialInstanceId>(StringComparer.Ordinal) {
                ["frameOrigin"] = moon ? parent.Item.Id : id, ["primaryLight"] = starId
            };
            if (rule.LightReference is { } light) references[light] = starId;
            return CelestialResult.Ok(new CelestialSpawnItem(id, key, slot, path, template.Value, resolved.Value, parent.Item.Id, references));
        }

        private static double HillRadius(Candidate planet, double stellarGm)
            => CelestialPopulationOrbits.HillRadius(planet.SemiMajor * (1 - planet.Eccentricity), planet.Gm, stellarGm);

        private static DVector3 PlaneDirection(DVector3 axis, double angle)
        {
            var basis = DVector3.Cross(axis, Math.Abs(axis.y) > .9 ? DVector3.forward : DVector3.up).normalized;
            return (Math.Cos(angle) * basis + Math.Sin(angle) * DVector3.Cross(axis, basis)).normalized;
        }
        private static DVector3 Tilt(DVector3 axis, double tilt, double node)
            => (Math.Cos(tilt) * axis + Math.Sin(tilt) * PlaneDirection(axis, node)).normalized;

        private static string SlotPath(string parent, string key, int slot)
            => parent + "/" + key + "[" + slot.ToString(CultureInfo.InvariantCulture) + "]";
        private static CelestialGenerationContext CandidateContext(ulong seed, string path, int attempt)
            => new(seed, path, "candidate/" + attempt.ToString(CultureInfo.InvariantCulture));
        private static double Uniform(CelestialGenerationContext random, string key, double min, double max)
            => random.SampleDouble(key, DoubleParameterSpec.Uniform(min, max));
        private static double LogRange(CelestialGenerationContext random, string key, double min, double max)
            => Math.Exp(Uniform(random, key, Math.Log(min), Math.Log(max)));
        private static double Angle(CelestialGenerationContext random, string key) => Uniform(random, key, 0, 2 * Math.PI);

        private static int WeightedIndex(IReadOnlyList<double> weights, CelestialGenerationContext random, string key)
        {
            double draw = Uniform(random, key, 0, weights.Sum());
            int last = 0;
            for (int i = 0; i < weights.Count; i++)
            {
                if (weights[i] <= 0) continue;
                draw -= weights[i];
                if (draw < 0) return i;
                last = i;
            }
            return last;
        }

        private static CelestialResult<CelestialPopulationExpansion> Reject(string code, string detail = "")
            => CelestialResult.Reject<CelestialPopulationExpansion>(new CelestialError(code, Detail: detail));

        private sealed record Physical(double Radius, double Gm, ExistingEphemerisParameters Orbit);
        private sealed record OrbitalParent(CelestialSpawnItem Item, double Gm, double Radius, DVector3 Normal);
        private sealed record Candidate(CelestialPopulationRule Rule, double Distance, double Radius, double Density, double Gm, double Eccentricity)
        {
            internal double SemiMajor { get; set; }
        }
    }
}
