using UnityEngine;
using Verse;
using System.Collections.Generic;

namespace PioneerFramework.Combat
{
    public class WeaponDef : Def
    {
        public WeaponType weaponType;

        public ShotDefReference? defaultShot;
        public List<ShotDefReference> allowedShots = new();
        public float muzzleVelocityFactor = 1f;
        public bool UsesShotComponents => defaultShot != null || allowedShots is { Count: > 0 };
        public bool AllowsShot(ShotDef shot) => allowedShots != null && allowedShots.Exists(reference => reference?.Resolve() == shot);

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (!UsesShotComponents) yield break;
            var defaultDefinition = defaultShot?.Resolve();
            if (defaultDefinition == null || !AllowsShot(defaultDefinition))
                yield return "defaultShot must be a member of allowedShots";
            if (allowedShots != null && allowedShots.Exists(s => s?.Resolve() == null)) yield return "allowedShots contains a missing reference";
            if (!ShotMath.Positive(muzzleVelocityFactor)) yield return "muzzleVelocityFactor must be finite and positive";
            if (weaponType == WeaponType.Missile || weaponType == WeaponType.Drone)
                yield return "ShotComp weapons must use Projectile or Ray weaponType";
        }

        public float damagePerShot = 15f;
        public float armorPenetration = 5f;
        public float fireRate = 0.5f;
        public float maxRange = 5000f;
        public float minRange = 50f;

        /// <summary>轨道交战射程（米）：Map 炮塔索敌用。maxRange 保留给 Ray 射线长度等原语义。</summary>
        public float orbitalEngageRange = 100000f;

        public float accuracyRadius = 2f;
        public float accuracyFalloff = 0.001f;

        public int cooldownTicks = 60;
        public int overheatThreshold = 10;
        public int overheatCooldownTicks = 300;

        public Vector3 mountSize = new(2, 1, 2);
        public bool turret ;
        public float turretRotationSpeed = 30f;
        public float trackingArc = 360f;

        public float projectileSpeed = 300000f;
        public float projectileMass = 0.5f;
        public bool affectedByGravity ;
        public int projectileLifetime = 600;

        public float referenceDistance = 100f;
        public bool inverseSquareFalloff = true;
        public float rayMaxLength = 3000f;
        public Color beamVisualColor = new(1f, 0.3f, 0.1f);

        public int missileCount = 1;
        /// <summary>出膛速度增量（m/s，相对母船）：2026-09-06 从 300 降到 150——发射观感慢速脱离，接近由制导加速接管。</summary>
        public float missileSpeed = 150f;
        public float terminalSpeed = 800f;
        public float missileFuel = 60f;
        public float engageDistance = 50f;
        public bool requiresDirectPath ;

        /// <summary>导弹武器 → 弹体/弹头定义（K2：MissileDef 独立承载，WeaponDef 只保留发射语义）。</summary>
        public string missileDefName = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        public bool isWeaponized ;
        public string droneWeaponDef = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        public float weaponsEngageRange = 20f;
    }

    public enum WeaponType
    {
        Projectile,
        Ray,
        Missile,
        Drone
    }
}
