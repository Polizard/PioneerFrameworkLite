using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Combat;

namespace PioneerFramework
{
    /// <summary>炮塔建筑安装类型（决定锚点数量）。</summary>
    public enum TurretMountConfig { Both, Top, Bottom }

    /// <summary>单个锚点的朝向（半球射界语义：Top = 船顶半球，Bottom = 船底半球）。</summary>
    public enum TurretMountSide { Top, Bottom }

    public class CompProperties_MapTurret : CompProperties
    {
        /// <summary>武器 Def 名（WeaponDef）。</summary>
        public string weaponDefName;

        /// <summary>安装类型：Both = 上下双侧，Top = 向上单侧，Bottom = 向下单侧。</summary>
        public TurretMountConfig mountConfig = TurretMountConfig.Top;

        /// <summary>炮口相对船体中心面在局部 Y 的偏移（米）。射界分界 = 船体中心面（y=0）。</summary>
        public float yOffset = 25f;

        public CompProperties_MapTurret()
        {
            compClass = typeof(CompMapTurret);
        }
    }

    /// <summary>
    /// Map 炮塔建筑：纯发射锚点。索敌/开火调度由绑定的 CompMapDesignator 负责。
    /// 每个锚点（TurretMount）持独立 WeaponInstance（冷却/过热/弹药状态机）。
    /// 只渲染上方锚点（原版铀炮素材），朝向用 Graphic.extraRotation 无反射实现。
    /// </summary>
    [StaticConstructorOnStartup]
    public class CompMapTurret : ThingComp
    {
        private List<TurretMount> mounts = new List<TurretMount>();

        /// <summary>炮头贴图（启动时主线程加载；暂用原版铀炮 Turret_Sniper 炮头，后续换专属贴图）。</summary>
        private static readonly Graphic topGraphic = GraphicDatabase.Get<Graphic_Single>(
            "Things/Building/Security/TurretSniper_Top",
            ShaderDatabase.Cutout, Vector2.one, Color.white);

        public CompProperties_MapTurret Props => (CompProperties_MapTurret)props;
        public IReadOnlyList<TurretMount> Mounts => mounts;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            BuildMounts();
        }

        public override void CompTick()
        {
            base.CompTick();
            // 武器冷却/过热状态机需要每 tick 更新，否则打一发后 cooldownRemaining 永久卡死、
            // CanFire() 恒 false → 炮塔哑火（炮塔武器未注册到 groupWeapons，必须自 tick）。
            foreach (var m in mounts)
                m.weapon?.Update(Find.TickManager.TicksGame);
        }

        private void BuildMounts()
        {
            mounts.Clear();
            var weaponDef = DefDatabase<WeaponDef>.GetNamedSilentFail(Props.weaponDefName);
            if (weaponDef == null)
            {
                Log.Warning("[CompMapTurret] 武器 Def 未找到: " + Props.weaponDefName);
                return;
            }
            if (Props.mountConfig == TurretMountConfig.Top || Props.mountConfig == TurretMountConfig.Both)
                mounts.Add(new TurretMount { side = TurretMountSide.Top, weapon = new WeaponInstance { def = weaponDef } });
            if (Props.mountConfig == TurretMountConfig.Bottom || Props.mountConfig == TurretMountConfig.Both)
                mounts.Add(new TurretMount { side = TurretMountSide.Bottom, weapon = new WeaponInstance { def = weaponDef } });
        }

        public TurretMount GetMount(TurretMountSide side)
        {
            foreach (var m in mounts)
                if (m.side == side) return m;
            return null;
        }

        public override void PostDraw()
        {
            base.PostDraw();
            if (mounts.Count == 0) return;

            foreach (var m in mounts)
            {
                if (m.side != TurretMountSide.Top) continue; // 只渲染上方炮塔
                topGraphic.Draw(parent.DrawPos, Rot4.North, parent, m.rotation);
            }
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra()) yield return g;
            if (parent.Faction != Faction.OfPlayer) yield break;

            foreach (var m in mounts)
            {
                string sideName = m.side == TurretMountSide.Top ? "上炮" : "下炮";
                bool bound = m.boundDesignator != null && !m.boundDesignator.Destroyed;
                yield return new Command_Action
                {
                    defaultLabel = bound ? "解绑" + sideName : "绑定" + sideName,
                    defaultDesc = bound ? "释放" + sideName + "的定位机绑定。" : "选择定位机绑定" + sideName + "。",
                    icon = TexButton.Rename,
                    action = () =>
                    {
                        if (bound) UnbindMount(m);
                        else BindMount(m);
                    }
                };
            }
        }

        private void BindMount(TurretMount mount)
        {
            if (parent.Map == null) return;
            var options = new List<FloatMenuOption>();
            foreach (var thing in parent.Map.spawnedThings)
            {
                var des = thing.TryGetComp<CompMapDesignator>();
                if (des == null || thing.Faction != parent.Faction || thing == parent) continue;
                options.Add(new FloatMenuOption(thing.LabelCap, () =>
                {
                    if (des.RegisterTurret(parent, mount.side))
                        mount.boundDesignator = thing;
                }));
            }
            if (options.Count == 0)
                options.Add(new FloatMenuOption("无可用的定位机", null));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void UnbindMount(TurretMount mount)
        {
            if (mount.boundDesignator == null) return;
            mount.boundDesignator.TryGetComp<CompMapDesignator>()?.UnregisterTurret(parent, mount.side);
            mount.boundDesignator = null;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref mounts, "mounts", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // 读档后补齐武器实例（状态机冷启动，冷却重置可接受；弹药默认无限）
                var weaponDef = DefDatabase<WeaponDef>.GetNamedSilentFail(Props.weaponDefName);
                foreach (var m in mounts)
                    if (m.weapon == null && weaponDef != null)
                        m.weapon = new WeaponInstance { def = weaponDef };
            }
        }
    }

    /// <summary>单个发射锚点：独立武器 / 朝向 / 定位机绑定。</summary>
    public class TurretMount
    {
        public TurretMountSide side;
        public WeaponInstance weapon;
        public float rotation; // 朝向角（船体局部系，度，顺时针绕局部 Y）
        public Thing boundDesignator;

        public void ExposeData()
        {
            Scribe_Values.Look(ref side, "side");
            Scribe_Values.Look(ref rotation, "rotation");
            Scribe_References.Look(ref boundDesignator, "boundDesignator");
        }
    }
}
