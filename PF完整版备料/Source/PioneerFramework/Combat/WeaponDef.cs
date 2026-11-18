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
        public float missileSpeed = 300f;
        public float terminalSpeed = 800f;
        public float missileFuel = 60f;
        public float engageDistance = 50f;
        public bool requiresDirectPath = false;

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