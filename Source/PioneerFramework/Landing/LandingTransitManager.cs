using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.Landing
{
    public sealed class LandingTransitManager : GameComponent
    {
        public List<LandingTaskData> Tasks = new List<LandingTaskData>();
        private bool needsReconcile;
        public LandingTransitManager(Game game) { }
        public LandingTaskData? FindTask(string id) => Tasks.FirstOrDefault(t => t.TaskId == id);
        public LandingTaskData? FindForGroup(int id) => Tasks.FirstOrDefault(t => t.GroupId == id && t.Status != LandingTransitStatus.Completed);
        public void Register(LandingTaskData task)
        {
            if (FindTask(task.TaskId) != null || FindForGroup(task.GroupId) != null) throw new InvalidOperationException("Duplicate landing task.");
            Tasks.Add(task);
        }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref Tasks, "pflLandingTasks", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) { Tasks ??= new List<LandingTaskData>(); needsReconcile = true; }
        }
        public override void GameComponentTick()
        {
            if (needsReconcile) { needsReconcile = false; LandingTransitService.Reconcile(this); }
            foreach (var task in Tasks.ToArray())
            {
                if (task.Status == LandingTransitStatus.Completed) continue;
                try { LandingTransitService.EnsureMarker(task); LandingTransitService.Advance(task); LandingAcceptanceScenarios.Observe(task); }
                catch (Exception e) { LandingTransitService.Fail(task, LandingReport.Reject("PFL_LandingExecutionError", e.Message), task.Status); }
            }
        }
    }
}
