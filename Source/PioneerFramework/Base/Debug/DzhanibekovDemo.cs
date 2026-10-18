using System;
using System.Collections.Generic;
using PioneerFramework.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **贾尼别科夫效应演示**（2026-10-17，用户点名：必须用我们自己的多part转动自然演化，不能手写姿态）。
    ///
    /// 做法：
    /// · 建一艘**多part** 复杂船（7 件，质量/位置不对称，惯量张量一般非对称）；
    /// · 组上打开 `torqueFreePhysics`（批 2 物理档前哨，默认 false）；
    /// · 初始世界角速度沿**惯量张量的中间主轴** + 极小扰动；
    /// · 之后每个物理步由 `SpaceGameComp.ComputeGroupPhysics` → `TorqueFreeRotation`
    ///   用这艘船**真实的多part惯量**按无外力矩 Euler 方程演化 ω 和姿态。
    ///   本类**只布置与读数，不写 rotation / 不写 ω**。
    ///
    /// 因此长轴翻转是方程的自然结果（网球拍定理），不是动画。
    /// </summary>
    public static class DzhanibekovDemo
    {
        private const double DemoOmega = 0.0200;   // 中间轴自旋（rad/s，模拟时间）
        private const int LogEveryTicks = 120;     // 2s（1×）一次读数

        private static int groupId = -1;
        private static int lastSign;
        private static int flipCount;
        private static int lastLogTick;

        public static bool IsRunning => groupId >= 0;

        public static void Spawn()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null)
            {
                Log.Warning("[贾尼别科夫] 不在太空场景（缺 SpaceGameComp）");
                return;
            }
            Stop();

            sg.DesiredTimeScale = TimeScaleLevel.Normal;
            Current.Game?.GetComponent<TimeScaleManager>()?.SetNormalMode();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;

            DVector3 pos = new(1.0e9, 3.0e8, 0);
            var group = CreateMultipartShip(sg, pos);
            groupId = group.GroupId;
            group.torqueFreePhysics = true;

            var r = group.InertiaResult;
            if (!r.HasData || !(r.p1 > 0.0) || !(r.p2 > 0.0) || !(r.p3 > 0.0))
            {
                Log.Warning("[贾尼别科夫] 多part 惯量无数据，演示中止");
                Stop();
                return;
            }

            // 中间主轴 = 主轴列 1（p2）；沿它自旋 + 沿 p1/p3 的极小扰动。
            // 这是“初始条件”，之后完全交给物理步；本类不再写 rotation/ω。
            DVector3 principal = new(0.0004, DemoOmega, 0.0006);
            DVector3 bodyOmega = BodyFromPrincipal(r, principal);
            group.angularVelocity = DVector3.Rotate(bodyOmega, group.CurrentOrbit.rotation);

            lastSign = Math.Sign(principal.y);
            flipCount = 0;
            lastLogTick = Find.TickManager?.TicksGame ?? 0;

            Log.Message($"[贾尼别科夫] 已布置 G{groupId}（多part 9 件，总质量 {ShipMass(sg, group):F0} kg）："
                + $"I1={r.p1:E3} I2={r.p2:E3} I3={r.p3:E3} kg·m²；"
                + $"中间主轴(体轴)=({r.r01:F3},{r.r11:F3},{r.r21:F3})；初始 Ω_world={group.angularVelocity.magnitude:F5} rad/s。"
                + "physics 已开 torqueFreePhysics：ω/姿态由多part惯量按 Euler 方程自然演化（无手写旋转）。");
            Messages.Message("贾尼别科夫演示：多part 自由转动已布置；看长轴周期性翻面。", MessageTypeDefOf.TaskCompletion, false);
        }

        public static void Stop()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (groupId >= 0 && sg != null && sg.GetGroupById(groupId) != null)
            {
                sg.DestroyGroup(groupId);
                Log.Message($"[贾尼别科夫] 已停止并销毁演示船 G{groupId}（中间轴翻转共 {flipCount} 次）");
            }
            groupId = -1;
        }

        /// <summary>每游戏 tick 只**读**：把物理演化后的世界 ω 转回主轴系，记符号翻转。</summary>
        public static void Tick()
        {
            if (groupId < 0) return;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            var group = sg?.GetGroupById(groupId);
            if (group == null) { groupId = -1; return; }

            var r = group.InertiaResult;
            if (!r.HasData || !(r.p1 > 0.0) || !(r.p2 > 0.0) || !(r.p3 > 0.0)) return;

            DQuaternion q = group.CurrentOrbit.rotation;
            DVector3 bodyOmega = DVector3.Rotate(group.angularVelocity, DQuaternion.Inverse(q));
            DVector3 principal = PrincipalFromBody(r, bodyOmega);

            int sign = Math.Sign(principal.y);
            if (lastSign != 0 && sign != 0 && sign != lastSign)
            {
                flipCount++;
                Log.Message($"[贾尼别科夫] 中间轴分量翻转 #{flipCount}（物理演化）："
                    + $"Ω_principal=({principal.x:F5},{principal.y:F5},{principal.z:F5})");
            }
            lastSign = sign;

            int now = Find.TickManager?.TicksGame ?? 0;
            if (now - lastLogTick >= LogEveryTicks)
            {
                lastLogTick = now;
                Log.Message($"[贾尼别科夫] Ω_principal=({principal.x:F5},{principal.y:F5},{principal.z:F5}) "
                    + $"|Ω_world|={group.angularVelocity.magnitude:F5} rad/s 翻转#{flipCount}"
                    + "｜看多part船的长轴（Z/50m 段）是否翻面");
            }
        }

        // ── 多part 演示船（7 件、非对称质量/位置；全部是普适 SpacePart）────────────

        private static SpaceEntityGroup CreateMultipartShip(SpaceGameComp sg, DVector3 pos)
        {
            int groupIdNew = sg.AllocGroupId();
            int entityId = sg.AllocEntityId();
            int faction = Faction.OfPlayer?.loadID ?? 0;

            var group = new SpaceEntityGroup(groupIdNew, faction)
            {
                IsMovable = true,
                IsCollidable = false,
                CelestialBodyId = 1
            };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)pos,
                velocitySystem = (SystemVec3)DVector3.zero,
                rotation = DQuaternion.identity
            };
            sg.RegisterGroup(group);

            var entity = new SpaceEntity(entityId, faction)
            {
                HasMap = false,
                IsMapArchived = false,
                IsCollidable = false,
                IsMovable = true,
                CanInteract = false,
                IsAffectedByGravity = false
            };
            entity.GroupId = groupIdNew;
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);

            var parts = new List<SpacePart>();
            // 质量/位置对原点对称 ⇒ COM=0（自由刚体绕质心转；不会出现"绕群原点转"的假象），
            // 但尺寸/分布非球形 ⇒ p1>p2>p3 严格分开，中间轴仍不稳定。
            AddPart(parts, new DVector3(0, 0, 0), new DVector3(8, 10, 20), 2000f);    // 主舱
            AddPart(parts, new DVector3(0, 0, 11), new DVector3(3, 4, 5), 300f);     // 头
            AddPart(parts, new DVector3(0, 0, -11), new DVector3(3, 4, 5), 300f);    // 尾
            AddPart(parts, new DVector3(6, 0, 0), new DVector3(3, 4, 10), 500f);     // +X 侧舱
            AddPart(parts, new DVector3(-6, 0, 0), new DVector3(3, 4, 10), 500f);    // -X 侧舱
            AddPart(parts, new DVector3(0, 5, 0), new DVector3(1, 3, 8), 100f);      // +Y 鳍
            AddPart(parts, new DVector3(0, -5, 0), new DVector3(1, 3, 8), 100f);     // -Y 鳍
            AddPart(parts, new DVector3(4, 0, 4), new DVector3(1, 4, 3), 80f);       // +XZ 鳍
            AddPart(parts, new DVector3(-4, 0, -4), new DVector3(1, 4, 3), 80f);     // -XZ 鳍
            entity.SetParts(parts);

            sg.RegisterEntity(entity);
            group.AddEntity(entityId);
            return group;
        }

        private static void AddPart(List<SpacePart> parts, DVector3 pos, DVector3 size, float mass)
        {
            var part = new SpacePart
            {
                partId = parts.Count + 1,
                defName = "PF_ShipPart_Cube",
                positionShip = pos,
                boundsSizeShip = size,
                hp = 100000f,
                isIndestructible = true
            };
            part.AddComponent(new HullMassComponent { mass = mass });
            parts.Add(part);
        }

        private static double ShipMass(SpaceGameComp sg, SpaceEntityGroup g)
        {
            double m = 0.0;
            foreach (int eid in g.GetEntityIds())
            {
                var e = sg.GetEntityById(eid);
                if (e == null) continue;
                for (int i = 0; i < e.Parts.Count; i++)
                    if (e.Parts[i] != null) m += e.Parts[i].Mass;
            }
            return m;
        }

        private static DVector3 PrincipalFromBody(in InertiaTensor.InertiaResult r, DVector3 b)
            => new(
                r.r00 * b.x + r.r10 * b.y + r.r20 * b.z,
                r.r01 * b.x + r.r11 * b.y + r.r21 * b.z,
                r.r02 * b.x + r.r12 * b.y + r.r22 * b.z);

        private static DVector3 BodyFromPrincipal(in InertiaTensor.InertiaResult r, DVector3 p)
            => new(
                r.r00 * p.x + r.r01 * p.y + r.r02 * p.z,
                r.r10 * p.x + r.r11 * p.y + r.r12 * p.z,
                r.r20 * p.x + r.r21 * p.y + r.r22 * p.z);
    }
}
