using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 火箭重建任务：前往返回舱并将其转换为目标火箭 Frame。
    /// 后续搬运与施工完全由原版 Frame 建造流程处理。
    /// </summary>
    public class JobDriver_RebuildRocket : JobDriver
    {
        private const int ConvertWaitTicks = 300;

        private const TargetIndex ReturnedRocketInd = TargetIndex.A;
        private const TargetIndex LaunchpadInd = TargetIndex.B;

        private Building ReturnedRocket => (job.GetTarget(ReturnedRocketInd).Thing as Building)!;
        private Building Launchpad => (job.GetTarget(LaunchpadInd).Thing as Building)!;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (!pawn.Reserve(ReturnedRocket, job, 1, -1, null, errorOnFailed))
                return false;
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(ReturnedRocketInd);
            this.FailOnDestroyedOrNull(ReturnedRocketInd);
            this.FailOn(() => ReturnedRocket == null || Map.designationManager.DesignationOn(ReturnedRocket, PFLaunchDefOf.PF_DesignateRebuildRocket) == null);
            this.FailOn(() => Launchpad == null || !LaunchPadUtility.IsAssemblyPad(Launchpad.def));
            this.FailOn(() => !LaunchPadUtility.IsWithinBuildableArea(Launchpad, ReturnedRocket.Position, ReturnedRocket.def, ReturnedRocket.Rotation));

            yield return Toils_Goto.GotoThing(ReturnedRocketInd, PathEndMode.Touch);
            yield return Toils_General.Wait(ConvertWaitTicks).WithProgressBarToilDelay(ReturnedRocketInd).FailOnCannotTouch(ReturnedRocketInd, PathEndMode.Touch);

            Toil convert = ToilMaker.MakeToil("ConvertReturnedRocketToFrame");
            convert.initAction = delegate
            {
                Building target = ReturnedRocket;
                if (target == null || target.Destroyed || target.Map == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                Designation rebuildDesignation = target.Map.designationManager.DesignationOn(target, PFLaunchDefOf.PF_DesignateRebuildRocket);
                if (rebuildDesignation != null)
                {
                    target.Map.designationManager.RemoveDesignation(rebuildDesignation);
                }

                if (!RocketRebuildFrameUtility.TryConvertReturnedToFrame(target, pawn, out string failReason))
                {
                    if (!failReason.NullOrEmpty())
                        Messages.Message(failReason, MessageTypeDefOf.RejectInput);
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                Messages.Message("火箭框架已就绪。", MessageTypeDefOf.PositiveEvent);
            };
            convert.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return convert;
        }
    }
}
