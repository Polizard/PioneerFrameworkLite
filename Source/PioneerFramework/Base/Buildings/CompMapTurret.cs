using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Combat;

namespace PioneerFramework
{
    public class CompProperties_MapTurret : CompProperties
    {
        /// <summary>武器 Def 名（WeaponDef）。</summary>
        public string weaponDefName;

        public CompProperties_MapTurret()
        {
            compClass = typeof(CompMapTurret);
        }
    }

    /// <summary>
    /// Map 炮塔建筑：整体式发射锚点（无上下分体）。索敌/开火调度由绑定的 CompMapDesignator 负责。
    /// 单一 TurretMount 持 WeaponInstance（冷却/过热/弹药状态机），360° 无射界限制。
    /// 朝向用 Graphic.extraRotation 无反射实现。
    /// </summary>
    [StaticConstructorOnStartup]
    public class CompMapTurret : ThingComp
    {
        private TurretMount mount;

        /// <summary>
        /// 炮头贴图（启动时主线程加载；暂用原版铀炮炮头 TurretSniper_Top，后续换专属贴图）。
        /// 底座由 ThingDef graphicData 绘制（TurretHeavy_Base），本 Comp 只画会转的炮头，避免同一贴图画两遍。
        /// </summary>
        private static readonly Graphic turretGraphic = GraphicDatabase.Get<Graphic_Single>(
            "Things/Building/Security/TurretSniper_Top",
            ShaderDatabase.Cutout, new Vector2(1.3f, 1.3f), Color.white);

        public CompProperties_MapTurret Props => (CompProperties_MapTurret)props;
        public TurretMount Mount => mount;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (mount == null) BuildMount();
        }

        public override void CompTick()
        {
            base.CompTick();
            // 武器冷却/过热状态机需要每 tick 更新，否则打一发后 cooldownRemaining 永久卡死、
            // CanFire() 恒 false → 炮塔哑火（炮塔武器未注册到 groupWeapons，必须自 tick）。
            mount?.weapon?.Update(Find.TickManager.TicksGame);
        }

        private void BuildMount()
        {
            var weaponDef = DefDatabase<WeaponDef>.GetNamedSilentFail(Props.weaponDefName);
            if (weaponDef == null)
            {
                Log.Warning("[CompMapTurret] 武器 Def 未找到: " + Props.weaponDefName);
                return;
            }
            mount = new TurretMount { weapon = new WeaponInstance { def = weaponDef } };
        }

        public override void PostDraw()
        {
            base.PostDraw();
            if (mount == null) return;
            // 整体式单炮塔：底座由 ThingDef graphicData 绘制（TurretHeavy_Base）。
            // 炮头用 DrawWorker + Altitudes.AltIncVect 抬高一个高度层绘制（原版 TurretTop.DrawTurret
            // 同款：drawLoc + Altitudes.AltIncVect），保证炮头始终盖在底座之上、不 z-fight。
            Vector3 topPos = parent.DrawPos + Altitudes.AltIncVect;
            turretGraphic.DrawWorker(topPos, Rot4.North, parent.def, parent, mount.rotation);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra()) yield return g;
            if (parent.Faction != Faction.OfPlayer) yield break;
            if (mount == null) yield break;

            bool bound = mount.boundDesignator != null && !mount.boundDesignator.Destroyed;
            yield return new Command_Action
            {
                defaultLabel = bound ? "解绑定位机" : "绑定定位机",
                defaultDesc = bound ? "释放定位机绑定。" : "选择定位机绑定本炮塔。",
                icon = TexButton.Rename,
                action = () =>
                {
                    if (bound) UnbindMount(mount);
                    else BindMount(mount);
                }
            };
        }

        private void BindMount(TurretMount target)
        {
            if (parent.Map == null) return;
            var options = new List<FloatMenuOption>();
            foreach (var thing in parent.Map.spawnedThings)
            {
                var des = thing.TryGetComp<CompMapDesignator>();
                if (des == null || thing.Faction != parent.Faction || thing == parent) continue;
                options.Add(new FloatMenuOption(thing.LabelCap, () =>
                {
                    if (des.RegisterTurret(parent))
                        target.boundDesignator = thing;
                }));
            }
            if (options.Count == 0)
                options.Add(new FloatMenuOption("无可用的定位机", null));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void UnbindMount(TurretMount target)
        {
            if (target.boundDesignator == null) return;
            target.boundDesignator.TryGetComp<CompMapDesignator>()?.UnregisterTurret(parent);
            target.boundDesignator = null;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref mount, "mount");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // 读档后补齐武器实例（状态机冷启动，冷却重置可接受；弹药默认无限）
                if (mount == null) BuildMount();
                else if (mount.weapon == null)
                {
                    var weaponDef = DefDatabase<WeaponDef>.GetNamedSilentFail(Props.weaponDefName);
                    if (weaponDef != null)
                        mount.weapon = new WeaponInstance { def = weaponDef };
                }
            }
        }
    }

    /// <summary>单个发射锚点：独立武器 / 朝向 / 定位机绑定（整体式，无上下之分）。</summary>
    public class TurretMount : IExposable
    {
        public WeaponInstance weapon;
        public float rotation; // 朝向角（船体局部系，度，顺时针绕局部 Y）
        public Thing boundDesignator;

        public void ExposeData()
        {
            Scribe_Values.Look(ref rotation, "rotation");
            Scribe_References.Look(ref boundDesignator, "boundDesignator");
        }
    }
}
