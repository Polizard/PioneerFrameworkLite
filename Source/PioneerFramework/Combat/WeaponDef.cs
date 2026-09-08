using UnityEngine;
using Verse;

namespace PioneerFramework.Combat
{
    public class WeaponDef : Def
    {
        public WeaponType weaponType;

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

        public Vector3 mountSize = new Vector3(2, 1, 2);
        public bool turret = false;
        public float turretRotationSpeed = 30f;
        public float trackingArc = 360f;

        public float projectileSpeed = 300000f;
        public float projectileMass = 0.5f;
        public bool affectedByGravity = false;
        public int projectileLifetime = 600;

        public float referenceDistance = 100f;
        public bool inverseSquareFalloff = true;
        public float rayMaxLength = 3000f;
        public Color beamVisualColor = new Color(1f, 0.3f, 0.1f);

        public int missileCount = 1;
        /// <summary>出膛速度增量（m/s，相对母船）：2026-09-06 从 300 降到 150——发射观感慢速脱离，接近由制导加速接管。</summary>
        public float missileSpeed = 150f;
        public float terminalSpeed = 800f;
        public float missileFuel = 60f;
        public float engageDistance = 50f;
        public bool requiresDirectPath = false;

        /// <summary>导弹武器 → 弹体/弹头定义（K2：MissileDef 独立承载，WeaponDef 只保留发射语义）。</summary>
        public string missileDefName;

        public bool isWeaponized = false;
        public string droneWeaponDef;
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