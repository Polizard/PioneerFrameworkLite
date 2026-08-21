using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 推进器建筑 Comp 的配置。挂在推进器 ThingDef 的 comps 列表里。
    /// 推进器不存储工质：燃烧时直接从所属地图 SpaceMapComp 的工质向量
    /// （储罐汇总）按质量流率 ṁ 扣减。
    /// </summary>
    public class CompProperties_Thruster : CompProperties
    {
        /// <summary>消耗的工质 Def 名。</summary>
        public string propellantDefName;

        /// <summary>排气速度 ve（m/s）。由引擎/推进器决定。</summary>
        public float exhaustVelocity = 9000f;

        /// <summary>单台质量流率 ṁ（kg/s）。推力 F = ve·ṁ。</summary>
        public float massFlowRate = 5f;

        public CompProperties_Thruster()
        {
            compClass = typeof(CompThruster);
        }
    }

    /// <summary>
    /// 推进器建筑 Comp：为所属地图（=船）提供推力参数。
    /// Spawn 时注册到 SpaceMapComp 推进器聚合（ve/ṁ/工质），DeSpawn 注销；
    /// 物理系统并行线程只读聚合值计算推力 a = ve·ṁ/m，并在主线程按 ṁ·dt 扣工质。
    /// IsBurning：空间层 burn 开始/结束时由 SpaceMapComp.SetThrustersBurning 设置，
    /// 表现层据此绘制喷焰。
    /// </summary>
    [StaticConstructorOnStartup]
    public class CompThruster : ThingComp
    {
        /// <summary>喷焰绘制材质（静态缓存，启动时在主线程创建）。</summary>
        private static readonly Material flameMaterial;

        static CompThruster()
        {
            flameMaterial = new Material(ShaderDatabase.Transparent)
            {
                color = new Color(1f, 0.6f, 0.15f, 0.75f)
            };
        }

        public CompProperties_Thruster Props => (CompProperties_Thruster)props;

        public float Ve => Props.exhaustVelocity;

        public float MassFlowRate => Props.massFlowRate;

        /// <summary>本推进器消耗的工质。Def 缺失（配置错误）时为 null。</summary>
        public PropellantDef PropellantDef { get; private set; }

        /// <summary>是否正在推进（burn 期间为 true，喷焰表现层读取）。</summary>
        public bool IsBurning { get; set; }

        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            if (!string.IsNullOrEmpty(Props.propellantDefName))
                PropellantDef = DefDatabase<PropellantDef>.GetNamedSilentFail(Props.propellantDefName);
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            RegisterToMap(+1, parent.Map);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode)
        {
            base.PostDeSpawn(map, mode);
            RegisterToMap(-1, map);
        }

        public override void PostDraw()
        {
            base.PostDraw();
            if (!IsBurning || parent == null) return;

            // 简单喷焰：建筑位置上方一个抖动发光的平面（表现层，无物理意义）
            float pulse = 1f + 0.35f * Mathf.Sin(Time.realtimeSinceStartup * 30f);
            float size = 0.55f * pulse;
            Vector3 pos = parent.DrawPos + Vector3.up * (0.35f + 0.2f * pulse);
            Matrix4x4 matrix = Matrix4x4.TRS(pos, Quaternion.identity, new Vector3(size, 1f, size));
            Graphics.DrawMesh(MeshPool.plane10, matrix, flameMaterial, 0);
        }

        private void RegisterToMap(int sign, Map map)
        {
            if (map == null) return;
            var mapComp = map.GetComponent<SpaceMapComp>();
            if (mapComp == null) return;
            if (sign > 0)
            {
                mapComp.RegisterEngine(PropellantDef, Ve, MassFlowRate);
                mapComp.RegisterThruster(this);
            }
            else
            {
                mapComp.UnregisterEngine(PropellantDef, Ve, MassFlowRate);
                mapComp.UnregisterThruster(this);
            }
        }
    }
}
