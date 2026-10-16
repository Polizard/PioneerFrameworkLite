using System;
using PioneerFramework.Landing;
using Xunit;
namespace PioneerFramework.Tests;
public class LandingTransitServiceTests
{
    [Fact]
    public void DuplicateGroupIsRejectedEvenWithoutMarker()
    {
        var manager = new LandingTransitManager(null);
        manager.Register(new LandingTaskData { TaskId = "first", GroupId = 7 });
        Assert.Throws<InvalidOperationException>(() => manager.Register(new LandingTaskData { TaskId = "second", GroupId = 7 }));
        Assert.Single(manager.Tasks);
    }
    [Fact]
    public void CompletedTaskAllowsFutureGroupIdentity()
    {
        var manager = new LandingTransitManager(null);
        manager.Register(new LandingTaskData { TaskId = "first", GroupId = 7, Status = LandingTransitStatus.Completed });
        manager.Register(new LandingTaskData { TaskId = "second", GroupId = 7 });
        Assert.Equal("second", manager.FindForGroup(7).TaskId);
    }
}
