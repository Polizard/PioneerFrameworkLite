using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework.Landing;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class LandingGroupLifecycleTests
{
    [Fact]
    public void LandingCompletionDetachesOrbitOnceWithoutDestroyingSourceMap()
    {
        Game previous = Current.Game;
        var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        var sg = new SpaceGameComp(game);
        game.components = new List<GameComponent> { sg };
        var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
        map.uniqueID = 42;
        AccessTools.Field(typeof(Map), "components").SetValue(map, new List<MapComponent>());
        AccessTools.Field(typeof(Game), "maps").SetValue(game, new List<Map> { map });
        var entity = new SpaceEntity(11) { MapId = 42, HasMap = true, GroupId = 7 };
        var group = new SpaceEntityGroup(7) { LandingTaskId = "landing-7" };
        group.GetEntityIds().Add(11);
        ((Dictionary<int, SpaceEntity>)AccessTools.Field(typeof(SpaceGameComp), "entities").GetValue(sg)).Add(11, entity);
        ((Dictionary<int, SpaceEntityGroup>)AccessTools.Field(typeof(SpaceGameComp), "groups").GetValue(sg)).Add(7, group);
        var task = new LandingTaskData { TaskId = "landing-7", GroupId = 7 };
        int landed = 0, destroyed = 0;
        sg.GroupLanded += _ => landed++;
        sg.GroupDestroyed += _ => destroyed++;
        try
        {
            Current.Game = game;
            var result = LandingGroupLifecycle.Finish(sg, task);
            Assert.True(result.Success, result.ReasonKey + ": " + string.Join("; ", result.Arguments));
            Assert.True(LandingGroupLifecycle.Finish(sg, task).Success);
            Assert.Null(sg.GetGroupById(7));
            Assert.Null(sg.GetEntityById(11));
            Assert.True(task.Cleanup.SpaceRemoved);
            Assert.True(task.Cleanup.Notified);
            Assert.Equal(1, landed);
            Assert.Equal(0, destroyed);
            Assert.Same(map, Assert.Single(game.Maps));
        }
        finally { Current.Game = previous; }
    }
}
