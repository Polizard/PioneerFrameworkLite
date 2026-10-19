using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework.SpaceAI;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class SpaceAiLifecycleRegressionTests
    {
        [Fact] public void ManualPairRegistration_CannotCollideWithNextEncounterAllocation()
        {
            using var host = new SpaceAiRuntimeTestHost();
            int first = host.Space.AllocGroupId();
            host.Space.RegisterGroup(new SpaceEntityGroup(first,2));
            host.Space.RegisterGroup(new SpaceEntityGroup(first+1,2));
            Assert.Equal(first+2,host.Space.AllocGroupId());
            int entity = host.Space.AllocEntityId();
            host.Space.RegisterEntity(new SpaceEntity(entity,2) { GroupId = first });
            host.Space.RegisterEntity(new SpaceEntity(entity+1,2) { GroupId = first });
            Assert.Equal(entity+2,host.Space.AllocEntityId());
        }

        [Fact] public void RejectedCreationRollback_CannotDestroyExistingSameNumberedShip()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var existing = host.AddShip();
            var rejected = new SpaceEntityGroup(existing.GroupId,2);
            EncounterShipFactory.RollbackCreation(host.Space,rejected,null,false);
            Assert.Same(existing,host.Space.GetGroupById(existing.GroupId));
        }

        private static bool RemoveMapOnly(Game __instance,Map map)
        { __instance.Maps.Remove(map); return false; }

        [Fact] public void ShipMapDisposal_RemovesNativePocketParentAndMapRegistration()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
            world.pocketMaps = new List<PocketMapParent>();
            world.renderer = (WorldRenderer)RuntimeHelpers.GetUninitializedObject(typeof(WorldRenderer));
            AccessTools.Field(typeof(Game),"worldInt").SetValue(host.Game,world);
            AccessTools.Field(typeof(Game),"maps").SetValue(host.Game,new List<Map>());
            var parent = (SpaceStationMapParent)RuntimeHelpers.GetUninitializedObject(typeof(SpaceStationMapParent));
            var other = (SpaceStationMapParent)RuntimeHelpers.GetUninitializedObject(typeof(SpaceStationMapParent));
            var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
            map.uniqueID = 42; map.info = new MapInfo { parent = parent };
            host.Game.Maps.Add(map); world.pocketMaps.Add(parent); world.pocketMaps.Add(other);
            var component = new SpaceMapComp(map); host.Space.RegisterMapComponent(42,component);
            var bootstrap = new Harmony("PFL.SpaceAI.MapDisposal");
            bootstrap.Patch(AccessTools.Method(typeof(Game),nameof(Game.DeinitAndRemoveMap)),
                prefix:new HarmonyMethod(typeof(SpaceAiLifecycleRegressionTests),nameof(RemoveMapOnly)));
            try
            {
                SpaceMapGenerator.DestroyShipMap(map);
                Assert.DoesNotContain(map,host.Game.Maps);
                Assert.DoesNotContain(parent,world.pocketMaps);
                Assert.Contains(other,world.pocketMaps);
                Assert.Null(host.Space.GetMapComponent<SpaceMapComp>(42));
            }
            finally { bootstrap.UnpatchAll(bootstrap.Id); }
        }
    }
}
