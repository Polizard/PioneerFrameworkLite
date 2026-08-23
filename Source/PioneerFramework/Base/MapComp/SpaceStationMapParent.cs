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
    ///
    /// 注意：此类继续保留，供其他分支/旧空间站逻辑使用。
    /// 新的航天器平级地图请使用 SpacecraftMapParent / SpacecraftMapRegistry。
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
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            yield return new Command_Action
            {
                defaultLabel = "返回地面",
                defaultDesc = "离开当前舱内地图，返回锚点地面地图。",
                icon = TexCommand.Install,
                action = delegate
                {
                    Map anchor = SpaceMapGenerator.GetAnchorMap();
                    if (anchor != null)
                        SpaceMapGenerator.EnterPocketMap(anchor);
                    else
                        Messages.Message("找不到可返回的地面地图。", MessageTypeDefOf.RejectInput, false);
                }
            };
        }
    }
}
