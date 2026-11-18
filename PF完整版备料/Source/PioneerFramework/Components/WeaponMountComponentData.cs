using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>武器挂载组件：挂载武器 Def、炮塔参数与出膛偏移。</summary>
    public class WeaponMountComponentData : PartComponentData
    {
        public string weaponDefName;
        public float turretRotationSpeed = 30f;
        public float trackingArc = 360f;
        public DVector3 muzzleOffset = DVector3.zero;
        public float dryMass = 400f; // 组件自重 kg

        static WeaponMountComponentData()
        {
            ComponentRegistry.Register<WeaponMountComponentData>();
        }

        public override void AccumulateStats(ref ShipStats s)
        {
            s.mass += dryMass;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref weaponDefName, "weaponDefName");
            Scribe_Values.Look(ref turretRotationSpeed, "turretRotationSpeed", 30f);
            Scribe_Values.Look(ref trackingArc, "trackingArc", 360f);
            DVector3.Scribe("muzzleOffset", ref muzzleOffset);
            Scribe_Values.Look(ref dryMass, "dryMass", 400f);
        }

        public override void Write(BinaryWriter w)
        {
            w.Write(weaponDefName ?? "");
            w.Write(turretRotationSpeed);
            w.Write(trackingArc);
            w.Write(muzzleOffset.x);
            w.Write(muzzleOffset.y);
            w.Write(muzzleOffset.z);
            w.Write(dryMass);
        }

        public override void Read(BinaryReader r)
        {
            weaponDefName = r.ReadString();
            turretRotationSpeed = r.ReadSingle();
            trackingArc = r.ReadSingle();
            muzzleOffset = new DVector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            dryMass = r.ReadSingle();
        }
    }
}
