using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PioneerFramework.Combat
{
    public class TargetingSystem
    {
        private CombatManager combatManager;
        private int lastLockLogTick = -120;   // 锁定日志限频（每 60 tick 一条）

        // 性能第 5 条：索敌每物理步一次 GetComponent 是组件表线性扫描，按 Game 缓存
        private readonly GameComponentRef<SpaceGameComp> spaceGameRef = new GameComponentRef<SpaceGameComp>();

        public TargetingSystem(CombatManager manager)
        {
            combatManager = manager;
        }

        public void Update(int currentTick)
        {
            var spaceGame = spaceGameRef.Get();
            if (spaceGame == null) return;

            foreach (var group in spaceGame.GetAllGroups())
            {
                if (!group.IsActive) continue;

                SpaceEntityGroup target = FindBestTarget(group, spaceGame);
                if (target != null)
                {
                    if (!combatManager.IsBattleActive)
                    {
                        combatManager.EngageGroups(group.GroupId, target.GroupId);
                    }
                    EngageTarget(group, target);
                }
            }
        }

        private SpaceEntityGroup FindBestTarget(SpaceEntityGroup attacker, SpaceGameComp spaceGame)
        {
            SpaceEntityGroup bestTarget = null;
            float bestScore = float.MinValue;

            DVector3 attackerPos = combatManager.GetGroupPos(attacker);

            // O(n²) → 空间网格预过滤：只遍历 CellSizeMeters 半径内的候选（粗网格 GetGroupsInRange），
            // 避免每 tick 对全部 group 双重遍历。候选数从 N 降到近邻 k。
            foreach (var group in spaceGame.GetGroupsInRange(attackerPos, SpaceConstants.CellSizeMeters))
            {
                if (!group.IsActive) continue;
                if (group.GroupId == attacker.GroupId) continue;
                if (group.FactionId == attacker.FactionId) continue;

                float score = EvaluateTarget(attacker, group);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = group;
                }
            }

            return bestTarget;
        }

        private float EvaluateTarget(SpaceEntityGroup attacker, SpaceEntityGroup target)
        {
            DVector3 attackerPos = combatManager.GetGroupPos(attacker);
            DVector3 targetPos = combatManager.GetGroupPos(target);
            double distance = (attackerPos - targetPos).magnitude;

            float score = 0f;

            double maxEngagementRange = SpaceConstants.CellSizeMeters;
            if (distance > maxEngagementRange)
                return float.MinValue;

            score += (1 - (float)(distance / maxEngagementRange)) * 100f;
            score += 50f;

            return score;
        }

        private void EngageTarget(SpaceEntityGroup attacker, SpaceEntityGroup target)
        {
            // 目标当前位置/速度（统一走 CombatManager：圆轨取解析位置与速度）
            DVector3 targetPos = combatManager.GetGroupPos(target);
            DVector3 targetVel = combatManager.GetGroupVelocity(target);

            // 复用 CombatManager 的 group 武器缓存（#2：不再每 tick new List 重复收集）
            var weapons = combatManager.GetGroupWeapons(attacker.GroupId);

            for (int i = 0; i < weapons.Count; i++)
            {
                if (weapons[i].CanFire())
                    combatManager.FireWeapon(attacker.GroupId, weapons[i], targetPos, null, targetVel, target.GroupId);
            }

            // ── 调试日志（限频 + 门控）：目标锁定状态，验证 A2 解析位置与 A1 提前量输入 ──
            if (CombatManager.DebugDetailedCombat && Find.TickManager.TicksGame - lastLockLogTick >= 60)
            {
                lastLockLogTick = Find.TickManager.TicksGame;
                DVector3 attackerPos = combatManager.GetGroupPos(attacker);
                double dist = (targetPos - attackerPos).magnitude;
                double vRelMag = (targetVel - combatManager.GetGroupVelocity(attacker)).magnitude;
                Log.Message(string.Format(
                    "[锁定] G{0}→G{1} 距离={2:F0}m 目标解析位置=({3:F0},{4:F0},{5:F0}) 目标速度=({6:F0},{7:F0},{8:F0}) |vRel|={9:F0}m/s 目标圆轨={10}",
                    attacker.GroupId, target.GroupId, dist,
                    targetPos.x, targetPos.y, targetPos.z,
                    targetVel.x, targetVel.y, targetVel.z, vRelMag,
                    target.IsCircularOrbit ? "是" : "否"));
            }
        }
    }
}
