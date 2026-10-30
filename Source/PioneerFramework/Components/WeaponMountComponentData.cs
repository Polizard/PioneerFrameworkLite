using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **武器挂点**（2026-10-22 部件功能化 §6：武器 part 应当存在）。
    ///
    /// ⚠ **只承载数据**：挂哪个武器、挂点在哪、朝哪、弹药基数、供电需求。
    /// **开火仍走既有桥接**：`CompMapDesignator`（索敌）→ `CompMapTurret`（开火）→ 3D 射弹。
    /// ⛔ **不许**在 part 层再长一套开火/冷却/命中逻辑——`WeaponDef` 那 7 个"声明未实现"字段
    /// 就是双轨的教训（设计稿 §6 原文）。
    ///
    /// 与 `AccumulateStats` 的关系：只贡献 `powerDraw`（满负荷标称）与 `heatProduction`，**不贡献推力/弹药**。
    /// 弹药容量（`ammoCapacity`）目前没有消费者：口径待定（`WeaponDef` 弹药 / `CompMapTurret` 现有口径，
    /// 见设计稿 §10 未定项），先只当数据存着。
    /// </summary>
    public class WeaponMountComponentData : PartComponentData
    {
        /// <summary>挂载的武器 DefName（`PioneerFramework.Combat.WeaponDef`）。</summary>
        public string weaponDefName = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        /// <summary>挂点相对本件原点（船体系，米）。</summary>
        public DVector3 mountOffsetShip = DVector3.zero;

        /// <summary>挂点朝向（船体系四元数；默认 identity）。</summary>
        public DQuaternion mountRotationShip = DQuaternion.identity;

        /// <summary>弹药基数（发；0 = 不适用）。暂无消费者（口径待定，见类头注）。</summary>
        public int ammoCapacity;

        /// <summary>开火时的标称功耗（W，≥0）——进 `ShipStats.powerDraw`，由电闭环按活动状态折算。</summary>
        public float powerDrawWatts;

        /// <summary>开火时的标称产热（W，≥0）——进 `ShipStats.heatProduction`（与功耗成对填）。</summary>
        public float heatProductionWatts;

        static WeaponMountComponentData()
        {
            ComponentRegistry.Register<WeaponMountComponentData>();
        }

        public override PartRole Role => PartRole.Weapon;

        public override void AccumulateStats(ref ShipStats s, float efficiency)
        {
            // §11.4：挂点失修 ⇒ 供弹/随动跟不上 ⇒ 功耗与产热按效率缩。
            s.powerDrawWeapon += powerDrawWatts * efficiency;   // §5：走"武器"通道（优先级第三）
            s.heatProduction += heatProductionWatts * efficiency;
        }

        public override bool ApplyPreset(string key, string value)
        {
            switch (key)
            {
                case "weaponDef":
                    weaponDefName = value;
                    return true;
                case "mountOffsetX":
                    if (!TryParseFloat(value, out float ox)) return false;
                    mountOffsetShip = new DVector3(ox, mountOffsetShip.y, mountOffsetShip.z);
                    return true;
                case "mountOffsetY":
                    if (!TryParseFloat(value, out float oy)) return false;
                    mountOffsetShip = new DVector3(mountOffsetShip.x, oy, mountOffsetShip.z);
                    return true;
                case "mountOffsetZ":
                    if (!TryParseFloat(value, out float oz)) return false;
                    mountOffsetShip = new DVector3(mountOffsetShip.x, mountOffsetShip.y, oz);
                    return true;
                case "mountYawDegrees":
                    if (!TryParseFloat(value, out float yaw)) return false;
                    mountRotationShip = DQuaternion.AngleAxis(yaw * System.Math.PI / 180.0, DVector3.up);
                    return true;
                case "ammoCapacity":
                    if (!TryParseInt(value, out int ammo)) return false;
                    ammoCapacity = ammo;
                    return true;
                case "powerDrawWatts":
                    if (!TryParseFloat(value, out float draw)) return false;
                    powerDrawWatts = draw;
                    return true;
                case "heatProductionWatts":
                    if (!TryParseFloat(value, out float heat)) return false;
                    heatProductionWatts = heat;
                    return true;
                default:
                    return false;
            }
        }

        public override void ExposeData()
        {
            string? loadedWeapon = weaponDefName;
            Scribe_Values.Look(ref loadedWeapon, "weaponMountDef");
            if (loadedWeapon != null)
                weaponDefName = loadedWeapon;
            DVector3.Scribe("weaponMountOffset", ref mountOffsetShip);
            DQuaternion.Scribe("weaponMountRotation", ref mountRotationShip);
            Scribe_Values.Look(ref ammoCapacity, "weaponMountAmmo", 0);
            Scribe_Values.Look(ref powerDrawWatts, "weaponMountPowerDraw", 0f);
            Scribe_Values.Look(ref heatProductionWatts, "weaponMountHeat", 0f);
        }

        public override void Write(BinaryWriter w)
        {
            w.Write(weaponDefName ?? "");
            w.Write(mountOffsetShip.x);
            w.Write(mountOffsetShip.y);
            w.Write(mountOffsetShip.z);
            w.Write(mountRotationShip.x);
            w.Write(mountRotationShip.y);
            w.Write(mountRotationShip.z);
            w.Write(mountRotationShip.w);
            w.Write(ammoCapacity);
            w.Write(powerDrawWatts);
            w.Write(heatProductionWatts);
        }

        public override void Read(BinaryReader r)
        {
            weaponDefName = r.ReadString() ?? "";
            mountOffsetShip = new DVector3(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
            mountRotationShip = new DQuaternion(r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
            ammoCapacity = r.ReadInt32();
            powerDrawWatts = r.ReadSingle();
            heatProductionWatts = r.ReadSingle();
        }
    }
}
