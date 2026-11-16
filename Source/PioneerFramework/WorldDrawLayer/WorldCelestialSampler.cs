using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using UnityEngine;

namespace PioneerFramework;

internal sealed record WorldCelestialMember(CelestialBodyDescription Description, CelestialInstanceId? ParentId,
    string Label, Color Color, CelestialVisualProfile? Profile, ExistingEphemerisParameters? Orbit, AtmosphereProfile Atmosphere)
{
    internal CelestialInstanceId Id => Description.Id;
}

internal sealed record WorldCelestialBody(WorldCelestialMember Member, CelestialMotionState Motion)
{
    internal CelestialInstanceId Id => Member.Id;
    internal CelestialInstanceId? ParentId => Member.ParentId;
    internal string Label => Member.Label;
    internal double Radius => Member.Description.RadiusMeters;
    internal DVector3 Position => Motion.Position;
}

internal sealed record WorldCelestialSnapshot(PhysicsSampleStamp Stamp, CelestialInstanceId StarId,
    IReadOnlyDictionary<CelestialInstanceId, WorldCelestialBody> Bodies);

/// <summary>原生世界地图在同一发布边界捕获目录和星历。</summary>
internal sealed class WorldCelestialSampler(CelestialRuntime runtime, CelestialInstanceId? entryId)
{
    private long catalogRevision = -1;
    private long topologyRevision = -1;
    private long faultRevision = -1;
    private CelestialInstanceId? starId;
    private WorldCelestialMember[] members = Array.Empty<WorldCelestialMember>();
    private CelestialInstanceId[] ids = Array.Empty<CelestialInstanceId>();

    internal CelestialResult<WorldCelestialSnapshot> Capture(double seconds) =>
        runtime.WithPublication(() => CapturePublished(seconds));

    private CelestialResult<WorldCelestialSnapshot> CapturePublished(double seconds)
    {
        var committed = runtime.CurrentStamp;
        if (runtime.IsRestoring) return Reject("RestorePending");
        var catalog = runtime.CaptureCatalog();
        if (catalog.Revision != catalogRevision || committed.TopologyRevision != topologyRevision ||
            runtime.Creation.FaultRevision != faultRevision)
            RebuildMembers(catalog, committed.TopologyRevision);
        if (starId is not { } star) return Reject("WorldCelestialNoStar");

        double time = double.IsFinite(seconds) ? Math.Min(seconds, committed.PhysicalSeconds) : committed.PhysicalSeconds;
        PhysicsSnapshotResult Sample(IReadOnlyList<CelestialInstanceId> requested) =>
            runtime.Sample(new(committed.WorldSessionId, committed.TopologyRevision, requested,
                time == committed.PhysicalSeconds ? PhysicsSamplePurpose.SimulationCommitted : PhysicsSamplePurpose.Presentation, time));

        var sample = Sample(ids);
        if (!sample.Success && time != committed.PhysicalSeconds && sample.Errors.Any(error => error.Code == "TimeUnavailable"))
        {
            time = committed.PhysicalSeconds;
            sample = Sample(ids);
        }
        if (!sample.Success && sample.Errors.Count != 0 &&
            sample.Errors.All(error => error.Code == "BodyMissing" && error.InstanceId.HasValue && error.InstanceId.Value != star))
        {
            var missing = sample.Errors.Select(error => error.InstanceId!.Value).ToHashSet();
            if (missing.Overlaps(ids)) sample = Sample(ids.Where(id => !missing.Contains(id)).ToArray());
        }
        if (!sample.Success)
            return sample.Errors.Count == 0 ? Reject("WorldCelestialSampleUnavailable") :
                CelestialResult.Reject<WorldCelestialSnapshot>(sample.Errors.ToArray());

        using var snapshot = sample.Snapshot!;
        if (!MatchesSample(snapshot.Stamp, committed, time)) return Reject("WorldCelestialSampleMismatch");
        var bodies = new Dictionary<CelestialInstanceId, WorldCelestialBody>(members.Length);
        foreach (var member in members)
        {
            var expected = member.Description;
            if (snapshot.TryGetBodyDescriptor(member.Id, out var descriptor) && descriptor.Id == member.Id &&
                descriptor.DescriptorRevision == expected.DescriptorRevision && descriptor.RadiusMeters == expected.RadiusMeters &&
                snapshot.TryGetBodyState(member.Id, out var state) && state.IsValid &&
                state.DescriptorRevision == expected.DescriptorRevision && state.Position.IsFinite())
                bodies.Add(member.Id, new(member, state));
        }

        return bodies.ContainsKey(star) ? CelestialResult.Ok(new WorldCelestialSnapshot(snapshot.Stamp, star, bodies)) :
            Reject("WorldCelestialStarSampleMissing");
    }

    internal static bool MatchesSample(PhysicsSampleStamp sample, PhysicsSampleStamp committed, double time) =>
        time == committed.PhysicalSeconds ? sample == committed :
        time < committed.PhysicalSeconds && sample.SampleKind == PhysicsSampleKind.PresentationInterpolation &&
        sample.WorldSessionId == committed.WorldSessionId && sample.ClockRevision == committed.ClockRevision &&
        sample.StateRevision <= committed.StateRevision && sample.TopologyRevision == committed.TopologyRevision &&
        sample.ReferenceFrameId == committed.ReferenceFrameId && sample.PhysicalSeconds == time;

    private void RebuildMembers(CelestialCatalogSnapshot catalog, long topology)
    {
        catalogRevision = catalog.Revision;
        topologyRevision = topology;
        faultRevision = runtime.Creation.FaultRevision;
        var byId = new Dictionary<CelestialInstanceId, CelestialBodySnapshot>();
        var children = new Dictionary<CelestialInstanceId, List<CelestialInstanceId>>();
        foreach (var description in catalog.Bodies)
        {
            var query = runtime.Query(description.Id);
            if (!query.Success || !query.Value.CommittedMembership ||
                query.Value.Lifecycle is not (CelestialLifecycle.Ready or CelestialLifecycle.Removing))
                continue;

            var body = query.Value;
            byId.Add(body.Id, body);
            if (body.ParentId is not { } parent) continue;
            if (!children.TryGetValue(parent, out var siblings))
            {
                siblings = new();
                children.Add(parent, siblings);
            }
            siblings.Add(body.Id);
        }

        CelestialInstanceId? FindStar(CelestialInstanceId? seed)
        {
            var seen = new HashSet<CelestialInstanceId>();
            while (seed is { } id && seen.Add(id) && byId.TryGetValue(id, out var body))
            {
                if (runtime.GetCapabilities<ICelestialLightSourceCapability>(id).Count == 1) return id;
                seed = body.ParentId;
            }
            return null;
        }

        starId = FindStar(entryId) ?? FindStar(runtime.Creation.RootSurfaceId);
        if (starId is not { } star)
        {
            members = Array.Empty<WorldCelestialMember>();
            ids = Array.Empty<CelestialInstanceId>();
            return;
        }

        var included = new HashSet<CelestialInstanceId> { star };
        var pending = new Queue<CelestialInstanceId>();
        pending.Enqueue(star);
        while (pending.TryDequeue(out var id))
            if (children.TryGetValue(id, out var descendants))
                foreach (var child in descendants)
                    if (included.Add(child)) pending.Enqueue(child);

        members = catalog.Bodies.Where(body => included.Contains(body.Id)).Select(body => CreateMember(byId[body.Id], star)).ToArray();
        ids = members.Select(member => member.Id).ToArray();
    }

    private WorldCelestialMember CreateMember(CelestialBodySnapshot body, CelestialInstanceId star)
    {
        ExistingEphemerisParameters? orbit = null;
        if (runtime.Physics is ExistingCelestialEphemerisBridge bridge && bridge.TryGetParameters(body.Id, out var parameters))
            orbit = parameters;

        var visual = runtime.GetCapabilities<ICelestialVisualCapability>(body.Id)
            .Select(capability => capability.GetVisualDescription())
            .FirstOrDefault(description => description.Kind == (body.Id == star ? CelestialVisualKind.Stellar : CelestialVisualKind.Surface));
        var color = visual == null ? default : CelestialRenderProjection.ColorOf(visual.Profile);
        if (!float.IsFinite(color.r) || !float.IsFinite(color.g) || !float.IsFinite(color.b) ||
            Mathf.Max(color.r, Mathf.Max(color.g, color.b)) < .1f)
            color = body.Id == star ? new Color(1f, .73f, .32f) : new Color(.46f, .66f, .84f);
        color.a = 1f;
        var atmosphere = runtime.GetCapabilities<ICelestialAtmosphereCapability>(body.Id).SingleOrDefault()?.GetProfile() ?? default;
        return new(body.Description, body.ParentId, runtime.Creation.GetLabel(body.Id), color, visual?.Profile, orbit, atmosphere);
    }

    private static CelestialResult<WorldCelestialSnapshot> Reject(string code) =>
        CelestialResult.Reject<WorldCelestialSnapshot>(new CelestialError(code));
}
