using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>航天器地图状态。</summary>
    public enum SpacecraftState
    {
        Ground,
        InSpace
    }

    /// <summary>
    /// 航天器独立地图父对象（非口袋地图语义）。
    /// 与地面/其他航天器地图平级，使用 SpacecraftMapRegistry 管理。
    /// </summary>
    public class SpacecraftMapParent : MapParent
    {
        /// <summary>创建时记录的地面/母地图，仅用于相机/上下文，不表示从属关系。</summary>
        public Map sourceMap;

        /// <summary>本地图使用的生成器。</summary>
        public MapGeneratorDef mapGenerator;

        /// <summary>所属航天器唯一 ID。</summary>
        public string spacecraftId;

        /// <summary>当前状态：未起飞 / 在轨。</summary>
        public SpacecraftState state;

        /// <summary>玩家是否把该地图设为自己的家园/殖民地。</summary>
        public bool IsPlayerHome;

        /// <summary>地面桥（脐带塔）数据；未绑定时为 null。</summary>
        public SpacecraftBridge GroundBridge;

        /// <summary>未来对接端口数据。</summary>
        public List<DockingPort> DockingPorts = new List<DockingPort>();

        public override MapGeneratorDef MapGeneratorDef => mapGenerator ?? base.MapGeneratorDef;

        public override Vector3 WorldCameraPosition => Vector3.zero;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref sourceMap, "sourceMap");
            Scribe_Defs.Look(ref mapGenerator, "mapGenerator");
            Scribe_Values.Look(ref spacecraftId, "spacecraftId");
            Scribe_Values.Look(ref state, "state", SpacecraftState.Ground);
            Scribe_Values.Look(ref IsPlayerHome, "isPlayerHome", false);
            Scribe_Deep.Look(ref GroundBridge, "groundBridge");
            Scribe_Collections.Look(ref DockingPorts, "dockingPorts", LookMode.Deep);
            if (DockingPorts == null)
                DockingPorts = new List<DockingPort>();
        }

        public void SetPlayerHome(bool value)
        {
            IsPlayerHome = value;
        }

        public void MarkLaunched()
        {
            state = SpacecraftState.InSpace;
            GroundBridge = null;
        }

        public void BindGroundBridge(SpacecraftBridge bridge)
        {
            GroundBridge = bridge;
        }

        public void ClearGroundBridge()
        {
            GroundBridge = null;
        }

        public override void FinalizeLoading()
        {
            base.FinalizeLoading();
            SpacecraftMapRegistry.Register(this);
        }

        public override void PostMapGenerate()
        {
            base.PostMapGenerate();
            SpacecraftMapRegistry.Register(this);
        }

        public override void Notify_MyMapRemoved(Map map)
        {
            base.Notify_MyMapRemoved(map);
            SpacecraftMapRegistry.Unregister(this);
        }

        public override void Destroy()
        {
            SpacecraftMapRegistry.Unregister(this);
            base.Destroy();
        }
    }
}
