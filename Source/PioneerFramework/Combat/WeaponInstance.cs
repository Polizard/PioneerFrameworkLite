using UnityEngine;

namespace PioneerFramework.Combat
{
    public class WeaponInstance
    {
        public const int AmmoInfinite = -1;

        public WeaponDef def = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        public int cooldownRemaining ;
        public int overheatCounter ;
        public bool isOverheated ;
        public Quaternion turretRotation = Quaternion.identity;
        public Quaternion targetRotation = Quaternion.identity;
        public bool isFiring ;
        public int ammo = AmmoInfinite;
        public bool isDisabled ;

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
            turretRotation = RotateTowards(turretRotation, targetRotation, rotationSpeed);
        }

        /// <summary>
        /// 与 `UnityEngine.Quaternion.RotateTowards` 同语义的本地实现（2026-10-13，P2）。
        /// 1.7 的 UnityStubs 没有该静态方法（元数据核实）⇒ 自实现（D5：缺什么自己解）。
        /// 沿最短弧朝目标转最多 maxRadiansDelta、不越过目标。
        /// </summary>
        private static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxRadiansDelta)
        {
            float dot = Quaternion.Dot(from, to);
            if (dot < 0f) { to = new Quaternion(-to.x, -to.y, -to.z, -to.w); dot = -dot; }
            float angle = 2f * Mathf.Acos(Mathf.Clamp(dot, -1f, 1f));
            if (angle <= maxRadiansDelta || angle <= 1e-6f) return to;
            return Quaternion.SlerpUnclamped(from, to, maxRadiansDelta / angle);
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