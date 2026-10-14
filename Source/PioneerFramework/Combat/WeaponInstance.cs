using UnityEngine;

namespace PioneerFramework.Combat
{
    public class WeaponInstance
    {
        public const int AmmoInfinite = -1;

        public WeaponDef def;
        public int cooldownRemaining = 0;
        public int overheatCounter = 0;
        public bool isOverheated = false;
        public Quaternion turretRotation = Quaternion.identity;
        public Quaternion targetRotation = Quaternion.identity;
        public bool isFiring = false;
        public int ammo = AmmoInfinite;
        public bool isDisabled = false;

        public void Update(int currentTick)
        {
            if (isDisabled) return;

            if (isOverheated)
            {
                cooldownRemaining--;
                if (cooldownRemaining <= 0)
                {
                    isOverheated = false;
                    overheatCounter = 0;
                }
                return;
            }

            if (cooldownRemaining > 0)
            {
                cooldownRemaining--;
            }

            UpdateTurretRotation();
        }

        private void UpdateTurretRotation()
        {
            if (!def.turret) return;

            float rotationSpeed = def.turretRotationSpeed * 0.01745f;
            // UnityStubs omits RotateTowards; retain its angle-limited slerp semantics.
            float angle = Quaternion.Angle(turretRotation, targetRotation);
            turretRotation = angle == 0f
                ? targetRotation
                : Quaternion.SlerpUnclamped(turretRotation, targetRotation, Mathf.Min(1f, rotationSpeed / angle));
        }

        public bool CanFire()
        {
            if (isDisabled) return false;
            if (isOverheated) return false;
            if (cooldownRemaining > 0) return false;
            if (ammo == 0) return false;
            return true;
        }

        public void Fire()
        {
            if (!CanFire()) return;

            overheatCounter++;
            cooldownRemaining = def.cooldownTicks;

            if (overheatCounter >= def.overheatThreshold)
            {
                isOverheated = true;
                cooldownRemaining = def.overheatCooldownTicks;
            }

            if (ammo > 0)
            {
                ammo--;
            }
        }

        public void SetTarget(Quaternion targetRot)
        {
            targetRotation = targetRot;
        }

        /// <summary>
        /// 禁用武器（部件被摧毁时调用）。
        /// </summary>
        public void Disable()
        {
            isDisabled = true;
        }
    }
}
