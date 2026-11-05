using UnityEngine;

using Verse;
using System.Linq;

namespace PioneerFramework.Combat
{
    public class WeaponInstance : IExposable
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
        private int lastUpdateTick = -1;
        public ShotDef? selectedShot;
        private string? selectedShotName;
        public float mountVelocityFactor = 1f;

        public bool TryGetSelectedShot(out ShotDef? shot)
        {
            shot = null;
            if (def == null) return false;
            if (selectedShot == null && !string.IsNullOrEmpty(selectedShotName)) return false;
            if (!def.UsesShotComponents) return selectedShot == null;
            shot = selectedShot ?? def.defaultShot?.Resolve();
            return shot != null && def.AllowsShot(shot) &&
                !def.ConfigErrors().Any() && !shot.ConfigErrors().Any();
        }

        public bool TrySelectShot(ShotDef shot)
        {
            if (def == null || !def.AllowsShot(shot) || shot.ConfigErrors().Any()) return false;
            selectedShot = shot;
            selectedShotName = shot.defName;
            return true;
        }

        public bool TryCreateShot(out ShotRuntime? shot, out string error)
        {
            shot = null;
            error = "";
            if (!TryGetSelectedShot(out var definition)) { error = "invalid selected/default shot"; return false; }
            return definition == null || definition.TryCreateRuntime(def.muzzleVelocityFactor, mountVelocityFactor, out shot, out error);
        }

        // Def 由设施 Comp 重新绑定；其余字段保留真实弹药和状态机进度。
        public void ExposeData()
        {
            Scribe_Values.Look(ref ammo, "ammo", AmmoInfinite);
            Scribe_Values.Look(ref cooldownRemaining, "cooldownRemaining");
            Scribe_Values.Look(ref overheatCounter, "overheatCounter");
            Scribe_Values.Look(ref isOverheated, "isOverheated");
            Scribe_Values.Look(ref isDisabled, "isDisabled");
            Scribe_Values.Look(ref lastUpdateTick, "lastUpdateTick", -1);
            Scribe_Defs.Look(ref selectedShot, "selectedShot");
            if (Scribe.mode == LoadSaveMode.Saving && selectedShot != null) selectedShotName = selectedShot.defName;
            Scribe_Values.Look(ref selectedShotName, "selectedShotName");
        }

        public void Update(int currentTick)
        {
            if (lastUpdateTick == currentTick) return;
            int elapsed = lastUpdateTick < 0 ? 1 : System.Math.Max(0, currentTick - lastUpdateTick);
            lastUpdateTick = currentTick;
            if (isDisabled) return;

            if (isOverheated)
            {
                cooldownRemaining -= elapsed;
                if (cooldownRemaining <= 0)
                {
                    isOverheated = false;
                    overheatCounter = 0;
                }
                return;
            }

            if (cooldownRemaining > 0)
            {
                cooldownRemaining = System.Math.Max(0, cooldownRemaining - elapsed);
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
        /// 沿最短弧朝目标转最多 maxDegreesDelta、不越过目标。
        /// </summary>
        private static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegreesDelta)
        {
            float angle = Quaternion.Angle(from, to);
            if (angle <= maxDegreesDelta || angle <= 1e-6f) return to;
            return Quaternion.SlerpUnclamped(from, to, maxDegreesDelta / angle);
        }

        public bool CanFire()
        {
            if (isDisabled) return false;
            if (isOverheated) return false;
            if (cooldownRemaining > 0) return false;
            if (ammo == 0) return false;
            if (def != null && !TryGetSelectedShot(out _)) return false;
            return true;
        }

        public int AvailableVolleyCount(int requested, int maximum)
        {
            int count = System.Math.Clamp(requested, 1, maximum);
            return ammo >= 0 ? System.Math.Min(count, ammo) : count;
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
