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
    /// </summary>
    public class PFProjectile3D : Projectile
    {
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

        /// <summary>从入口 3D 点直线飞向落点（瞬爆：入口 = 落点 = 命中点，距离 0 → 1 tick 后 ImpactSomething）。</summary>
        public void Launch3D(Vector3 start, Vector3 end, IntVec3 landingCell)
        {
            origin = start;
            destination = end;
            float speed = def.projectile.SpeedTilesPerTick;
            ticksToImpact = Mathf.Max(1, Mathf.CeilToInt((end - start).magnitude / Mathf.Max(speed, 0.01f)));
            lifetime = ticksToImpact;
            usedTarget = new LocalTargetInfo(landingCell);
            intendedTarget = new LocalTargetInfo(landingCell);
            HitFlags = ProjectileHitFlags.All; // 命中建筑/pawn/落点（desiredHitFlags 为 private，走公共属性）
        }

        /// <summary>
        /// 碰撞 + 建筑伤害（原版语义）：命中建筑/落点建筑造成 damageDef 伤害；
        /// 落点爆炸视觉沿用 Mote（太空 Pocket Map 上 GenExplosion 会 NRE）。
        /// 注意：base.Impact 会 Destroy 射弹，伤害/视觉必须在它之前完成（先缓存 Map/Position）。
        /// </summary>
        protected override void Impact(Thing hitThing, bool blockedByShield = false)
        {
            Map map = Map;
            IntVec3 pos = Position;
            bool inBounds = map != null && pos.InBounds(map);

            Thing target = hitThing;
            if (target == null || !target.Spawned)
                target = inBounds ? pos.GetEdifice(map) : null;
            if (target != null && target.Spawned)
            {
                int dmg = def.projectile.GetDamageAmount(null, null);
                target.TakeDamage(new DamageInfo(def.projectile.damageDef, dmg));
            }

            // 落点爆炸视觉（1.6 Fleck 体系：ExplosionFlash；太空 Pocket Map 上 GenExplosion 会 NRE）
            if (inBounds)
                FleckMaker.Static(pos, map, FleckDefOf.ExplosionFlash, 2f);

            base.Impact(hitThing, blockedByShield); // 视觉残留 + Destroy（放最后）
        }
    }
}
