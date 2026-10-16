using System;

namespace PioneerFramework.Landing
{
    public static class LandingGroupLifecycle
    {
        public static LandingSuspensionData SuspendState(SpaceEntityGroup group, string taskId)
        {
            if (!string.IsNullOrEmpty(group.LandingTaskId)) throw new InvalidOperationException("Group already landing.");
            var snapshot = new LandingSuspensionData { IsActive = group.IsActive, IsMovable = group.IsMovable, IsCollidable = group.IsCollidable };
            group.LandingTaskId = taskId; group.IsActive = group.IsMovable = group.IsCollidable = false;
            return snapshot;
        }
        public static bool TrySuspend(SpaceGameComp sg, SpaceEntityGroup group, string taskId, out LandingSuspensionData snapshot, out LandingReport report)
        {
            snapshot = new LandingSuspensionData();
            try { snapshot = SuspendState(group, taskId); sg.RemoveLandingGroupFromIndexes(group, snapshot); report = LandingReport.Ok(); return true; }
            catch (Exception e) { report = LandingReport.Reject("PFL_LandingExecutionError", e.Message); return false; }
        }
        public static LandingReport Restore(SpaceGameComp sg, LandingTaskData task)
        {
            var group = sg.GetGroupById(task.GroupId);
            if (group == null) return LandingReport.Reject("PFL_LandingMissingGroup");
            group.LandingTaskId = "";
            group.IsActive = task.Suspension.IsActive; group.IsMovable = task.Suspension.IsMovable; group.IsCollidable = task.Suspension.IsCollidable;
            sg.SyncGroupToCoarseGrid(group.GroupId, group.CurrentOrbit.positionSystem.v);
            sg.RestoreLandingTracker(group.GroupId, task.Suspension);
            group.GetLinkedMapComp()?.SetThrustersBurning(group.HasActiveBurn);
            return LandingReport.Ok();
        }
        public static LandingReport Finish(SpaceGameComp sg, LandingTaskData task)
        {
            try { if (!task.Cleanup.SpaceRemoved) sg.FinishLandingGroup(task); return LandingReport.Ok(); }
            catch (Exception e) { return LandingReport.Reject("PFL_LandingExecutionError", e.Message); }
        }
    }
}
