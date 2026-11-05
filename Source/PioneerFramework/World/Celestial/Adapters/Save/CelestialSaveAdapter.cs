using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using Verse;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialSaveAdapter : IExposable
    {
        private const int MaximumHeaderCharacters = 4096;
        private const int MaximumChunkCharacters = CelestialSaveEnvelope.ChunkBytes * 2;
        private readonly CelestialRuntime? runtime;
        private int formatVersion;
        private string? envelopeHeader;
        private List<string>? chunks;
        public CelestialSaveDocument? Document { get; private set; }
        public CelestialSaveAdapter() { }
        public CelestialSaveAdapter(CelestialRuntime runtime) => this.runtime = runtime;
        public CelestialResult<CelestialSaveDocument> CaptureCheckpoint()
        {
            if (runtime == null) return Document == null ? CelestialResult.Reject<CelestialSaveDocument>(new CelestialError("RuntimeUnavailable")) : CelestialResult.Ok(Document);
            return runtime.WithPublication(() => {
                try
                {
                    if (runtime.Coordinator.HasPreparedStep || runtime.IsExecutingCallbacks || runtime.HostWork.IsBusy)
                        return CelestialResult.Reject<CelestialSaveDocument>(new CelestialError("HostWorkPending"));
                    var retained = runtime.RestoreDocument;
                    var physics = runtime.IsRestoring && retained?.PhysicsCheckpoint != null ? retained.PhysicsCheckpoint : runtime.Physics.CaptureCheckpoint();
                    if (!runtime.IsRestoring && physics.Stamp != runtime.CurrentStamp) throw new InvalidOperationException("CheckpointBoundaryMismatch");
                    if (!runtime.IsRestoring && runtime.Maps != null)
                    {
                        var nativeMaps = runtime.Maps.BindNativeMapsForSave();
                        if (!nativeMaps.Success) return CelestialResult.Reject<CelestialSaveDocument>(nativeMaps.Errors.ToArray());
                    }
                    var bodies = runtime.Creation.CaptureBodies();
                    Document = CelestialSaveDocument.CreateEmpty(runtime.WorldSessionId, runtime.WorldSeed) with {
                        Epochs = runtime.Operations.Epochs.Capture(), Extensions = runtime.Extensions.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal),
                        Stamp = physics.Stamp, CatalogRevision = runtime.Catalog.Revision, NextHandle = runtime.Catalog.NextHandle, RootSurfaceId = runtime.Creation.RootSurfaceId,
                        Bodies = bodies, PublishedBodies = runtime.Catalog.Capture().Bodies.Select(body => body.Id).ToArray(),
                        CommittedBodies = bodies.Where(body => body.CommittedMembership).Select(body => body.Description.Id).ToArray(),
                        Operations = runtime.Operations.Capture(), Creations = runtime.Creation.CaptureCreations(), Removals = runtime.Removal.Capture(), References = runtime.References.Capture(),
                        Commands = runtime.Commands.Capture(), SchedulerCursors = runtime.Scheduler.CaptureCursors(), Events = runtime.Scheduler.CaptureEvents(),
                        Surfaces = runtime.IsRestoring ? retained!.Surfaces : runtime.Surfaces != null ? runtime.Surfaces.CaptureHeaders().Select(header => new CelestialSavedSurface(header.OwnerId, header.LayerId,
                            header.PhysicalRadiusMeters, header.NativeRadius, header.Subdivisions, header.GenerationSeed, header.GenerationConfiguration.ToArray(), header.State)).ToArray() : retained?.Surfaces ?? Array.Empty<CelestialSavedSurface>(),
                        Maps = runtime.IsRestoring ? retained!.Maps : runtime.Maps?.CaptureSites() ?? Array.Empty<CelestialMapSiteRecord>(), PhysicsCheckpoint = physics };
                    var valid = CelestialSaveCodec.Validate(Document);
                    return valid.Success ? CelestialResult.Ok(Document) : CelestialResult.Reject<CelestialSaveDocument>(valid.Errors.ToArray());
                }
                catch (Exception exception) { return CelestialResult.Reject<CelestialSaveDocument>(new CelestialError("CheckpointCaptureFailed", Detail: exception.Message)); }
            });
        }
        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // Retained documents remain available to diagnostics, never as a substitute for a restored runtime.
                if (runtime == null) throw new InvalidOperationException("RuntimeUnavailable: a loaded adapter cannot write a new save.");
                if (runtime.IsRestoring) throw new InvalidOperationException("RestorePending: complete celestial restoration before saving.");
                var capture = CaptureCheckpoint();
                if (!capture.Success) throw new InvalidOperationException("Celestial save capture failed: " + string.Join(",", capture.Errors.Select(error => error.Code + ":" + error.Detail)));
                var encoded = CelestialSaveCodec.Encode(capture.Value);
                if (!encoded.Success) throw new InvalidOperationException(string.Join(",", encoded.Errors.Select(error => error.Code)));
                var envelope = JsonNode.Parse(encoded.Value.Span)!.AsObject();
                chunks = envelope["chunks"]!.AsArray().Select(chunk => chunk!.ToJsonString()).ToList();
                envelope.Remove("chunks");
                envelopeHeader = envelope.ToJsonString();
                formatVersion = 2;
            }
            Scribe_Values.Look(ref formatVersion, "celestialFormatVersion");
            Scribe_Values.Look(ref envelopeHeader, "celestialV2Header");
            Scribe_Collections.Look(ref chunks, "celestialV2Chunks", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (formatVersion != 2) throw new InvalidOperationException("UnsupportedSaveVersion: create a new test world.");
                if (envelopeHeader == null || envelopeHeader.Length > MaximumHeaderCharacters || chunks == null || chunks.Count > CelestialSaveEnvelope.MaximumChunkCount ||
                    chunks.Any(chunk => chunk == null || chunk.Length > MaximumChunkCharacters))
                    throw new InvalidOperationException("InvalidSaveDocument");
                var envelope = JsonNode.Parse(envelopeHeader)!.AsObject();
                if (envelope.ContainsKey("chunks")) throw new InvalidOperationException("InvalidSaveDocument");
                envelope["chunks"] = new JsonArray(chunks.Select(chunk => JsonNode.Parse(chunk)).ToArray());
                var decoded = CelestialSaveCodec.Decode(Encoding.UTF8.GetBytes(envelope.ToJsonString()));
                if (!decoded.Success) throw new InvalidOperationException(string.Join(",", decoded.Errors.Select(error => error.Code)));
                Document = decoded.Value;
            }
        }
    }
}
