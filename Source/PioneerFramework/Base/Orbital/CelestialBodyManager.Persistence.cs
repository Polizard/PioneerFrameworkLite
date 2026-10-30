using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using Verse;

namespace PioneerFramework
{
    public partial class CelestialBodyManager
    {
        private CelestialSaveAdapter? celestialSaveData;
        private CelestialRestoreCoordinator? celestialRestore;
        private bool restoreAttempted;
        private bool restoreNeedsProvider;
        private bool restoreNoticePending;
        private string lastRestoreFailure = "";
        public IReadOnlyList<CelestialError> CelestialRestoreErrors { get; private set; } = Array.Empty<CelestialError>();
        public bool HasCelestialState => celestialRuntime != null || celestialSaveData?.Document != null;
        public bool CelestialRestorePending => restoreAttempted && (celestialRuntime == null || celestialRuntime.IsRestoring);
        private void ExposeCelestialData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (CelestialRestorePending) throw new InvalidOperationException("RestorePending: complete celestial restoration before saving.");
                if (celestialRuntime != null) celestialSaveData = new CelestialSaveAdapter(celestialRuntime);
                celestialSaveData?.ExposeData();
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                restoreAttempted = true;
                celestialSaveData = new CelestialSaveAdapter();
                celestialSaveData.ExposeData();
                StartCelestialRestore(celestialSaveData.Document!);
            }
        }
        private void StartCelestialRestore(CelestialSaveDocument document)
        {
            if (celestialRuntime != null)
            {
                celestialRuntime.Surfaces?.StopObservingNativeChanges();
                DetachCelestialRuntime(celestialRuntime);
            }
            Guid session = Guid.NewGuid();
            var errors = new List<CelestialError>();
            ICelestialPhysicsBridge? bridge = null;
            if (document.PhysicsCheckpoint != null)
            {
                var request = new CelestialPhysicsWorldRequest(session, document.WorldSeed,
                    new CelestialSpawnManifest("restore", document.WorldSeed, document.RootSurfaceId, Array.Empty<CelestialSpawnItem>()), document.Stamp.PhysicalSeconds);
                var connection = CelestialPhysicsProviderRegistry.Global.Connect(document.PhysicsCheckpoint.ProviderId, request);
                if (connection.Success) bridge = connection.Value.Bridge;
                else errors.AddRange(connection.Errors);
            }
            else errors.Add(new CelestialError("ProviderUnavailable"));
            restoreNeedsProvider = bridge == null;
            celestialRestore = new CelestialRestoreCoordinator(session, bridge, CelestialBuiltinCompRegistry.Global, name => {
                var definition = DefDatabase<CelestialBodyDef>.GetNamedSilentFail(name);
                return definition?.ToTemplate() ?? CelestialResult.Reject<CelestialBodyTemplate>(new CelestialError("DefinitionMissing", Detail: name)); });
            var restored = celestialRestore.RestoreIdentities(document);
            if (restored.Success)
            {
                var attached = AttachCelestialRuntime(celestialRestore.Runtime);
                if (!attached.Success) errors.AddRange(attached.Errors);
            }
            else errors.AddRange(restored.Errors);
            CelestialRestoreErrors = errors.AsReadOnly();
        }
        public override void LoadedGame()
        {
            base.LoadedGame();
            if (!CelestialRestorePending) return;
            CompleteCelestialRestore();
        }
        private CelestialResult<bool> CompleteCelestialRestore()
        {
            if (celestialRestore == null || celestialRuntime == null)
                return RestoreFailure(CelestialRestoreErrors.Count == 0 ? new[] { new CelestialError("IdentityRestoreFailed") } : CelestialRestoreErrors);
            var bound = celestialRestore.BindNativeObjects();
            if (!bound.Success) return RestoreFailure(bound.Errors);
            var completed = celestialRestore.CompleteRestore();
            if (!completed.Success) return RestoreFailure(completed.Errors);
            CelestialRestoreErrors = Array.Empty<CelestialError>();
            restoreNoticePending = false;
            lastRestoreFailure = "";
            initialized = false;
            return CelestialResult.Ok(true);
        }
        /// <summary>Retry the retained restoration only; never starts a replacement system or advances simulation time.</summary>
        public CelestialResult<bool> RetryCelestialRestore()
        {
            if (!ReferenceEquals(Current.Game, hostGame)) return CelestialResult.Reject<bool>(new CelestialError("WorldChanged"));
            if (!restoreAttempted) return CelestialResult.Reject<bool>(new CelestialError("RestoreNotStarted"));
            if (!CelestialRestorePending) return CelestialResult.Ok(true);
            if (celestialSaveData?.Document == null) return RestoreFailure(new[] { new CelestialError("InvalidSaveDocument") });
            if (restoreNeedsProvider || celestialRuntime == null) StartCelestialRestore(celestialSaveData.Document);
            return CompleteCelestialRestore();
        }
        private CelestialResult<bool> RestoreFailure(IReadOnlyList<CelestialError> errors)
        {
            CelestialRestoreErrors = Array.AsReadOnly(errors.ToArray());
            string failure = string.Join("; ", errors.Select(error => error.Code + ": " + error.Detail));
            if (failure != lastRestoreFailure)
            {
                Log.Error("[PFL Celestial] Restore suspended; space simulation and saving are blocked: " + failure);
                lastRestoreFailure = failure;
                restoreNoticePending = true;
            }
            return CelestialResult.Reject<bool>(errors.ToArray());
        }
        private void ShowRestoreNotice()
        {
            if (!restoreNoticePending || Current.ProgramState != ProgramState.Playing || Find.WindowStack == null) return;
            restoreNoticePending = false;
            Find.WindowStack.Add(new Dialog_MessageBox("PF_Celestial_RestoreFailed".Translate(string.Join("\n", CelestialRestoreErrors.Select(error => error.Code + ": " + error.Detail))),
                "PF_Celestial_RetryRestore".Translate(), () => {
                    var result = RetryCelestialRestore();
                    if (!result.Success && ReferenceEquals(Current.Game, hostGame))
                        Messages.Message("PF_Celestial_RestoreStillBlocked".Translate(string.Join(", ", result.Errors.Select(error => error.Code))), RimWorld.MessageTypeDefOf.RejectInput, false);
                }, "Close".Translate()));
        }
        internal void NotifyCelestialSaveRejected(string code)
        {
            if (CelestialRestorePending) restoreNoticePending = true;
            if (Current.ProgramState == ProgramState.Playing)
                Messages.Message("PF_Celestial_SaveBlocked".Translate(code), RimWorld.MessageTypeDefOf.RejectInput, false);
        }
        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            if (ReferenceEquals(Current.Game, hostGame)) ShowRestoreNotice();
            if (!ReferenceEquals(Current.Game, hostGame) || celestialRuntime == null || celestialRuntime.IsRestoring) return;
            celestialRuntime.HostWork.BindHostThread();
            celestialRuntime.ProcessHostWork();
        }
    }
}
