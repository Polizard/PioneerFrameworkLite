using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework.Combat
{
    /// <summary>
    /// Map 内三维贯穿弹（M4-F2/F3）：替代纯视觉 Mote，具备原版射弹的建筑/pawn 碰撞。
    /// 覆盖 ExactPosition 做完整 3D 直线插值（原版默认 Yto0 只飞 xz 平面 + 固定高度）。
    /// 由 CombatManager.SpawnPenetrationMote 生成：从入口（地图边缘，高度按命中规则）
    /// 直线飞向落点（命中点映射格，y=0），命中建筑自动 Impact 造成伤害。
    /// visualOnly 模式供 F2 出膛视觉弹使用：不碰撞己方建筑、落点无伤害（纯叙事）。
    /// </summary>
    public class PFProjectile3D : Projectile
    {
        /// <summary>纯视觉模式：跳过碰撞（HitFlags=None）且落点不造成伤害。</summary>
        public bool visualOnly;

        /// <summary>3D 直线插值（含 y 高度），替代原版 xz 平面 + 固定 Altitude。</summary>
        public override Vector3 ExactPosition
        {
            get
            {
                float t = DistanceCoveredFraction;
                return Vector3.Lerp(origin, destination, t);
            }
        }

        /// <summary>朝向防御：垂直贯穿（xz 相同）时避免 LookRotation 零向量警告。</summary>
        public override Quaternion ExactRotation
        {
            get
            {
                Vector3 dir = (destination - origin).Yto0();
                if (dir.sqrMagnitude < 0.001f)
                    return Quaternion.identity;
                return Quaternion.LookRotation(dir);
            }
        }

        /// <summary>
        /// 从入口 3D 点直线飞向落点（F3 贯穿/瞬爆）。
        /// visualOnly = 纯视觉（不碰撞、无伤害，F2 出膛视觉弹用）；
        /// speedOverride &gt; 0 时覆盖弹速（时间放大表现）。
        /// </summary>
        public void Launch3D(Vector3 start, Vector3 end, IntVec3 landingCell, bool visualOnly = false, float speedOverride = 0f)
        {
            origin = start;
            destination = end;
            this.visualOnly = visualOnly;
            float speed = speedOverride > 0f ? speedOverride : def.projectile.SpeedTilesPerTick;
            ticksToImpact = Mathf.Max(1, Mathf.CeilToInt((end - start).magnitude / Mathf.Max(speed, 0.01f)));
            lifetime = ticksToImpact;
            usedTarget = new LocalTargetInfo(landingCell);
            intendedTarget = new LocalTargetInfo(landingCell);
            // 纯视觉弹跳过碰撞检测：不伤害己方建筑/人员，飞完整段到落点
            HitFlags = visualOnly ? ProjectileHitFlags.None : ProjectileHitFlags.All;
        }

        /// <summary>
        /// 碰撞 + 建筑伤害（原版语义）：命中建筑/落点建筑造成 damageDef 伤害；
        /// 落点爆炸视觉沿用 Mote（太空 Pocket Map 上 GenExplosion 会 NRE）。
        /// 注意：base.Impact 会 Destroy 射弹，伤害/视觉必须在它之前完成（先缓存 Map/Position）。
        /// visualOnly 模式跳过伤害结算（出膛视觉弹不误伤己方建筑）。
        /// </summary>
        protected override void Impact(Thing hitThing, bool blockedByShield = false)
        {
            Map map = Map;
            IntVec3 pos = Position;
            bool inBounds = map != null && pos.InBounds(map);

            if (!visualOnly)
            {
                Thing target = hitThing;
                if (target == null || !target.Spawned)
                    target = inBounds ? pos.GetEdifice(map) : null;
                if (target != null && target.Spawned)
                {
                    int dmg = def.projectile.GetDamageAmount(null, null);
                    target.TakeDamage(new DamageInfo(def.projectile.damageDef, dmg));
                }
            }

            // 落点爆炸视觉（1.6 Fleck 体系：ExplosionFlash；太空 Pocket Map 上 GenExplosion 会 NRE）
            if (inBounds)
                FleckMaker.Static(pos, map, FleckDefOf.ExplosionFlash, 2f);

            base.Impact(hitThing, blockedByShield); // 视觉残留 + Destroy（放最后）
        }
    }
}
