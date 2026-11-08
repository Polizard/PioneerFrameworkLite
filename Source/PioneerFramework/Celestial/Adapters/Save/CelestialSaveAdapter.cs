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
        /// <summary>保存预检捕获的文档；只在 SaveGame 前缀成功通过后由 ExposeData 消费一次。</summary>
        private CelestialSaveDocument? prepared;
        public CelestialSaveDocument? Document { get; private set; }
        internal CelestialRuntime? Runtime => runtime;
        public CelestialSaveAdapter() { }
        public CelestialSaveAdapter(CelestialRuntime runtime) => this.runtime = runtime;
        /// <summary>已预检但尚未写入的文档；仅用于测试与诊断，不对外承诺生命周期。</summary>
        internal CelestialSaveDocument? PreparedDocument => prepared;
        /// <summary>
        /// 在 SafeSaver 接触目标文件前完成一次捕获：运行期/恢复态校验 → CaptureCheckpoint → 保留结果。
        /// 失败时不保留半成品（prepared 清空），失败码沿用 CaptureCheckpoint 的结构化 Code（HostWorkPending 等）。
        /// </summary>
        internal CelestialResult<bool> PrepareSave()
        {
            // 每次保存尝试都从空开始：上一次被其它组件中止的 SaveGame 可能留下 prepared，
            // 绝不能让它跨到下一次保存（或非 SaveGame 的 ExposeData 路径）被误消费。
            prepared = null;
            if (runtime == null) return CelestialResult.Reject<bool>(new CelestialError("RuntimeUnavailable"));
            if (runtime.IsRestoring) return CelestialResult.Reject<bool>(new CelestialError("RestorePending"));
            var capture = CaptureCheckpoint();
            if (!capture.Success) { prepared = null; return CelestialResult.Reject<bool>(capture.Errors.ToArray()); }
            prepared = capture.Value;
            return CelestialResult.Ok(true);
        }
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
                    // 只在 codec 校验通过后才提交 Document：Validate 失败时不得让半成品文档
                    // 通过 HasCelestialState/诊断入口看起来像一次成功捕获。
                    var document = CelestialSaveDocument.CreateEmpty(runtime.WorldSessionId, runtime.WorldSeed) with {
                        Epochs = runtime.Operations.Epochs.Capture(), Extensions = runtime.Extensions.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal),
                        Stamp = physics.Stamp, CatalogRevision = runtime.Catalog.Revision, NextHandle = runtime.Catalog.NextHandle, RootSurfaceId = runtime.Creation.RootSurfaceId,
                        Bodies = bodies, PublishedBodies = runtime.Catalog.Capture().Bodies.Select(body => body.Id).ToArray(),
                        CommittedBodies = bodies.Where(body => body.CommittedMembership).Select(body => body.Description.Id).ToArray(),
                        Operations = runtime.Operations.Capture(), Creations = runtime.Creation.CaptureCreations(), Removals = runtime.Removal.Capture(), References = runtime.References.Capture(),
                        Commands = runtime.Commands.Capture(), SchedulerCursors = runtime.Scheduler.CaptureCursors(), Events = runtime.Scheduler.CaptureEvents(),
                        Surfaces = runtime.IsRestoring ? retained!.Surfaces : runtime.Surfaces != null ? runtime.Surfaces.CaptureHeaders().Select(header => new CelestialSavedSurface(header.OwnerId, header.LayerId,
                            header.PhysicalRadiusMeters, header.NativeRadius, header.Subdivisions, header.GenerationSeed, header.GenerationConfiguration.ToArray(), header.State,
                            header.TerrainAlgorithmVersion)).ToArray() : retained?.Surfaces ?? Array.Empty<CelestialSavedSurface>(),
                        Maps = runtime.IsRestoring ? retained!.Maps : runtime.Maps?.CaptureSites() ?? Array.Empty<CelestialMapSiteRecord>(), PhysicsCheckpoint = physics };
                    var valid = CelestialSaveCodec.Validate(document);
                    if (!valid.Success) return CelestialResult.Reject<CelestialSaveDocument>(valid.Errors.ToArray());
                    Document = document;
                    return CelestialResult.Ok(document);
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
                // 优先消费 SaveGame 前缀（CelestialSaveGuard.BeforeSave）捕获的文档；用完立即清空，
                // 避免失败/重试时把上一次的旧文档写成新档。没有预检时回退到现场捕获（非 SaveGame 路径仍可用）。
                CelestialResult<CelestialSaveDocument> capture;
                var pending = prepared;
                prepared = null;
                if (pending != null) capture = CelestialResult.Ok(pending);
                else capture = CaptureCheckpoint();
                if (!capture.Success) throw new InvalidOperationException("Celestial save capture failed: " + string.Join(",", capture.Errors.Select(error => error.Code + ":" + error.Detail)));
                var encoded = CelestialSaveCodec.Encode(capture.Value);
                if (!encoded.Success) throw new InvalidOperationException(string.Join(",", encoded.Errors.Select(error => error.Code)));
                var envelope = JsonNode.Parse(encoded.Value.Span)!.AsObject();
                chunks = envelope["chunks"]!.AsArray().Select(chunk => chunk!.ToJsonString()).ToList();
                envelope.Remove("chunks");
                envelopeHeader = envelope.ToJsonString();
                formatVersion = CelestialSaveDocument.CurrentVersion;
            }
            Scribe_Values.Look(ref formatVersion, "celestialFormatVersion");
            // Scribe keys keep their historical names: renaming them would turn an old save into a
            // silently empty celestial section instead of the explicit UnsupportedSaveVersion below.
            Scribe_Values.Look(ref envelopeHeader, "celestialV2Header");
            Scribe_Collections.Look(ref chunks, "celestialV2Chunks", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (formatVersion != CelestialSaveDocument.CurrentVersion) throw new InvalidOperationException("UnsupportedSaveVersion: create a new test world.");
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
