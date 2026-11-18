using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 空间站口袋地图的父级 WorldObject。
    /// 继承自 PocketMapParent，只覆写了 WorldCameraPosition -> Vector3.zero，
    /// 避免相机在切换到本站地图时因 tile = -1 而崩溃。
    /// </summary>
    public class SpaceStationMapParent : PocketMapParent
    {
        /// <summary>
        /// 口袋地图的 tile 为 PlanetTile.Invalid (-1)，
        /// 若相机尝试 GetTileCenter(-1) 会越界崩溃。
        /// 返回 Vector3.zero 后 WorldCameraDriver.ApplyMapPositionToGameObject()
        /// 会跳过定位。
        /// </summary>
        public override Vector3 WorldCameraPosition => Vector3.zero;

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos())
                yield return g;

            if (!HasMap)
                yield break;

            var controller = Map.GetComponent<SpaceStationController>();
            if (controller == null)
                yield break;

            if (controller.Frozen)
            {
                // 解冻
                yield return new Command_Action
                {
                    defaultLabel = "解冻空间站",
                    defaultDesc = "解冻空间站，恢复活动。",
                    icon = TexCommand.Attack,
                    action = controller.Unfreeze
                };
            }
            else
            {
                // 冻结
                yield return new Command_Action
                {
                    defaultLabel = "冻结空间站",
                    defaultDesc = "冻结空间站。\n要求：空间站上不能有角色。",
                    icon = TexCommand.DesirePower,
                    action = delegate { controller.Freeze(); }
                };
            }
        }
    }
}