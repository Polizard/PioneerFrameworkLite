using PioneerFramework.Landing;
using Xunit;

namespace PioneerFramework.Tests;

public class LandingTaskDataTests
{
    [Fact]
    public void CompletedTaskCannotReenterFlight()
    {
        var task = new LandingTaskData { TaskId = "test", Status = LandingTransitStatus.Completed };
        Assert.False(task.TryTransition(LandingTransitStatus.InFlight, out _));
        Assert.Equal(LandingTransitStatus.Completed, task.Status);
    }

    [Fact]
    public void MapIdentitySurvivesRuntimeIdChange()
    {
        var map = new LandingSourceMapData { SourceKey = "source-42", MapId = 42 };
        string operation = map.OperationId("task");
        map.MapId = 84;
        Assert.Equal(operation, map.OperationId("task"));
    }
}
