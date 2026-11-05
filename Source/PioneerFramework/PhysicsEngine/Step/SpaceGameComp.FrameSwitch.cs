using System;
using System.Collections.Generic;
using PioneerFramework.Celestial;
using Verse;

namespace PioneerFramework
{
    public partial class SpaceGameComp
    {
        private const double SoiInScale = 0.99;
        private const double SoiOutScale = 1.01;

        /// <summary>盒 SOI 进/出检查（2026-11-04）：只对自由飞行组；圆轨/落地/燃烧中跳过。</summary>
        private void CheckGroupSoi(SpaceEntityGroup group, CelestialBodyManager mgr, double physicsTick)
        {
            if (group.IsCircularOrbit || group.landed || group.HasActiveBurn) return;
            int central = group.CelestialBodyId;
            if (central < 0) return;
            var centralBody = mgr.GetBodyById(central);
            if (centralBody == null) return;

            // 帧根系：positionSystem 相对帧根；出 SOI 用 positionBody（相对中心）。
            int frameRoot = mgr.GetReferenceFrameId(central);
            if (frameRoot < 0) return;
            DVector3 posFrame = group.CurrentOrbit.positionSystem.v;

            DVector3 OffsetOf(int bodyId)
            {
                if (mgr.TryGetSoiOffset(frameRoot, bodyId, physicsTick, out var cached))
                    return cached.Position;
                return mgr.GetFrameOffset(frameRoot, bodyId, physicsTick).Position;
            }

            // 出：相对当前中心的 SOI（盒，1.01R）；中心是恒星（无父）时不出。
            double soi = SoiRadius(mgr, centralBody);
            if (soi > 0.0)
            {
                var rel = posFrame + OffsetOf(central); // frameRoot 相对 central
                if (!SoiCheck.IsInside(rel.x, rel.y, rel.z, soi * SoiOutScale))
                {
                    int parentId = centralBody.ParentBodyId;
                    if (parentId >= 0)
                    {
                        int next = mgr.GetReferenceFrameId(parentId);
                        if (next >= 0) SwitchGroupFrame(group, next, mgr, physicsTick);
                    }
                    return;
                }
            }

            // 进：只在中心 == 帧根时检查（恒星帧进行星、行星帧进卫星），O(天体数)。
            if (central != frameRoot) return;
            var members = mgr.GetSystemMembers(frameRoot);
            for (int i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (member.bodyId == central) continue;
                double memberSoi = SoiRadius(mgr, member);
                if (memberSoi <= 0.0) continue;
                var rel = posFrame + OffsetOf(member.bodyId);
                if (SoiCheck.IsInside(rel.x, rel.y, rel.z, memberSoi * SoiInScale))
                {
                    SwitchGroupFrame(group, member.bodyId, mgr, physicsTick);
                    return;
                }
            }
        }

        /// <summary>换系：改参考天体 + 把状态平移到新帧根系（进 SOI 后必须用行星系小坐标）。</summary>
        private void SwitchGroupFrame(SpaceEntityGroup group, int targetBodyId, CelestialBodyManager mgr, double physicsTick)
        {
            if (group.CelestialBodyId == targetBodyId) return;
            int oldFrame = mgr.GetReferenceFrameId(group.CelestialBodyId);
            int newFrame = mgr.GetReferenceFrameId(targetBodyId);

            // positionSystem/velocitySystem 是**相对帧根**的坐标 ⇒ 换系必须平移到新帧根系。
            // 留在恒星系大坐标会让行星系内的引力/碰撞/渲染全部吃 1e10 量级的精度损失。
            if (oldFrame >= 0 && newFrame >= 0 && oldFrame != newFrame)
            {
                var off = mgr.GetFrameOffset(oldFrame, newFrame, physicsTick); // 旧帧根相对新帧根
                group.CurrentOrbit.positionSystem = (SystemVec3)(group.CurrentOrbit.positionSystem.v + off.Position);
                group.CurrentOrbit.velocitySystem = (SystemVec3)(group.CurrentOrbit.velocitySystem.v + off.Velocity);
            }

            if (oldFrame >= 0 && planetGrids.TryGetValue(oldFrame, out var oldGrid)
                && groupLastCoarsePos.TryGetValue(group.GroupId, out var last))
            {
                oldGrid.Remove(group);
                groupLastCoarsePos.Remove(group.GroupId);
            }

            int oldBody = group.CelestialBodyId;
            group.CelestialBodyId = targetBodyId;
            Log.Message($"[换系] G{group.GroupId} 中心 {oldBody} → {targetBodyId}" +
                $"（帧根 {oldFrame} → {newFrame}）" +
                $"｜|pos|={group.CurrentOrbit.positionSystem.v.magnitude:E4} m" +
                $"｜|vel|={group.CurrentOrbit.velocitySystem.v.magnitude:F1} m/s｜tick={physicsTick:F0}");
            group.physicsRenderPreviousPosition = group.CurrentOrbit.positionSystem.v;
            group.physicsRenderPreviousTick = physicsTick;
            group.OrbitDirty = true;
            group.IsSpatialDirty = true;
            SyncGroupToCoarseGrid(group.GroupId, group.CurrentOrbit.positionSystem.v);
        }

        /// <summary>外部（命令/测试）换系入口。</summary>
        public void SwitchGroupFrame(int groupId, int targetBodyId)
        {
            var mgr = celestialRef.Get();
            if (mgr == null) return;
            if (!groups.TryGetValue(groupId, out var group)) return;
            SwitchGroupFrame(group, targetBodyId, mgr, Find.TickManager.TicksGame);
        }

        private static double SoiRadius(CelestialBodyManager mgr, CelestialBodyInfo body)
        {
            if (body.orbitRadius <= 0.0 || body.gravitationalParameter <= 0.0) return 0.0;
            double r = OrbitAdapter.SoiRadiusApprox(body, mgr);
            return double.IsFinite(r) && r > 0.0 ? r : 0.0;
        }
    }
}
