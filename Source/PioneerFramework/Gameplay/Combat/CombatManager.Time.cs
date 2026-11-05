namespace PioneerFramework.Combat
{
    public partial class CombatManager
    {
        private void PrepareProjectileBirth(ProjectileInstance projectile, SpaceEntityGroup source,
            DVector3 targetPosition, DVector3? targetVelocity, double muzzleSpeed)
        {
            var clock = GetTimeScaleManager();
            if (clock == null || clock.IsPhysicsBatchActive || clock.GetPhysicsTick() <= clock.CommittedPhysicsTick) return;
            clock.MarkPhysicsEvent();
            if (source.IsCircularOrbit) return;
            projectile.awaitingSourceState = true;
            projectile.birthOffsetShip = DVector3.Rotate(projectile.spawnPosition.v - GetGroupPos(source),
                DQuaternion.Inverse(source.CurrentOrbit.rotation));
            projectile.birthTargetPosition = targetPosition;
            projectile.birthTargetVelocity = targetVelocity;
            projectile.birthMuzzleSpeed = muzzleSpeed;
        }

        private bool ResolveProjectileBirth(ProjectileInstance projectile, double physicsTick)
        {
            if (!projectile.awaitingSourceState || physicsTick < projectile.spawnPhysicsTick) return true;
            var source = GetSpaceGameComp()?.GetGroupById(projectile.sourceGroupId);
            if (source == null || !source.IsActive) return false;
            projectile.awaitingSourceState = false;
            var position = GetGroupPos(source) + DVector3.Rotate(projectile.birthOffsetShip, source.CurrentOrbit.rotation);
            var sourceVelocity = GetGroupVelocity(source);
            var aim = projectile.birthTargetVelocity.HasValue
                ? ComputeLeadAimPoint(position, sourceVelocity, projectile.birthTargetPosition,
                    projectile.birthTargetVelocity.Value, projectile.birthMuzzleSpeed)
                : projectile.birthTargetPosition;
            var direction = aim - position;
            if (direction.sqrMagnitude < 1.0) return false;
            var velocity = sourceVelocity + direction.normalized * projectile.birthMuzzleSpeed;
            projectile.spawnPosition = projectile.prevPosition = projectile.position = (SystemVec3)position;
            projectile.spawnVelocity = projectile.velocity = (SystemVec3)velocity;
            return true;
        }
    }
}
