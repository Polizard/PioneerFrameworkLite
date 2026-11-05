using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class PFViewMapNavigationTests
    {
        [Fact]
        public void AvailableMapsBelongToTheGroupAreLoadedAndAppearOnlyOnce()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip();
            var first = MapWithId(41);
            var second = MapWithId(42);
            AddEntity(host, group, 31, 42);
            AddEntity(host, group, 32, 41);
            AddEntity(host, group, 33, 42);
            AddEntity(host, group, 34, 43).IsMapArchived = true;
            AddEntity(host, group, 35, 44).HasMap = false;
            AddEntity(host, group, 36, 45);
            AddEntity(host, group, 37, -1);
            group.AddEntity(999);
            var other = new SpaceEntityGroup(20, 2) { CelestialBodyId = 0 };
            host.Space.RegisterGroup(other);
            AddEntity(host, other, 38, 46);
            var loaded = new[] { MapWithId(46), second, MapWithId(44), MapWithId(43), first };
            var result = new List<Map>();

            CollectMaps(group.GroupId, 2, loaded, result);

            Assert.Equal(new[] { first, second }, result);
        }

        [Theory]
        [InlineData(3, true)]
        [InlineData(-1, true)]
        [InlineData(2, false)]
        public void MissingPermissionOrInactiveGroupHasNoMapShortcut(int playerFactionId, bool active)
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip();
            group.IsActive = active;
            AddEntity(host, group, 31, 41);
            var result = new List<Map> { MapWithId(99) };

            CollectMaps(group.GroupId, playerFactionId, new[] { MapWithId(41) }, result);

            Assert.Empty(result);
        }

        [Fact]
        public void RefreshRejectsMapsUnloadedArchivedOrMovedSinceTheMenuWasOpened()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip();
            var entity = AddEntity(host, group, 31, 41);
            var loaded = new List<Map> { MapWithId(41) };
            var result = new List<Map>();
            CollectMaps(group.GroupId, 2, loaded, result);
            Assert.Single(result);

            entity.IsMapArchived = true;
            CollectMaps(group.GroupId, 2, loaded, result);
            Assert.Empty(result);
            entity.IsMapArchived = false;
            CollectMaps(group.GroupId, 2, loaded, result);
            Assert.Single(result);

            loaded.Clear();
            CollectMaps(group.GroupId, 2, loaded, result);
            Assert.Empty(result);
            loaded.Add(MapWithId(41));
            entity.GroupId = 20;
            CollectMaps(group.GroupId, 2, loaded, result);
            Assert.Empty(result);
        }

        [Fact]
        public void MissingGroupOrSimulationClearsPreviouslyCollectedMaps()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var result = new List<Map> { MapWithId(99) };
            CollectMaps(999, 2, new[] { MapWithId(41) }, result);
            Assert.Empty(result);
            result.Add(MapWithId(99));
            var savedGame = Current.Game;
            Current.Game = null;
            try { CollectMaps(10, 2, new[] { MapWithId(41) }, result); }
            finally { Current.Game = savedGame; }
            Assert.Empty(result);
        }

        private static SpaceEntity AddEntity(SpaceAiRuntimeTestHost host, SpaceEntityGroup group, int id, int mapId)
        {
            var entity = new SpaceEntity(id, group.FactionId)
                { GroupId = group.GroupId, HasMap = true, MapId = mapId };
            host.Space.RegisterEntity(entity);
            return entity;
        }

        private static Map MapWithId(int id)
        {
            var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
            map.uniqueID = id;
            return map;
        }

        private static void CollectMaps(int groupId, int playerFactionId,
            IReadOnlyList<Map> loadedMaps, List<Map> result)
        {
            PFViewMapNavigation.CollectMaps(groupId, playerFactionId, loadedMaps, result);
        }
    }
}
