using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class CelestialCatalogTests
{
    [Fact]
    public void UpsertCannotPromoteBlockedBodyToReady()
    {
        var catalog = new CelestialCatalog();
        var description = Description("blocked", 0);
        var body = new CelestialBodyInstance(description) { Lifecycle = CelestialLifecycle.Blocked };
        Assert.True(catalog.Stage(body).Success);
        var rejected = catalog.Publish(new(0, new[] { description }));
        Assert.Equal("BodyNotReady", Assert.Single(rejected.Errors).Code);
        Assert.Equal(CelestialLifecycle.Blocked, body.Lifecycle);
        Assert.Empty(catalog.Capture().Bodies);
    }
    [Fact]
    public void SameTemplateInstancesDoNotShareComponentState()
    {
        var first = new CelestialBodyInstance(Description("moon[0]", 0));
        var second = new CelestialBodyInstance(Description("moon[1]", 1));
        first.SetCompRecord(new CelestialCompSaveRecord("counter", "sample.counter", 1, 1,
            new byte[] { 3 }, new byte[] { 4 }));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Description.DefinitionId, second.Description.DefinitionId);
        Assert.Single(first.CompStateRecords);
        Assert.Empty(second.CompStateRecords);
    }

    [Fact]
    public void PublishedSnapshotsKeepTheirOwnDescriptionRevision()
    {
        var catalog = new CelestialCatalog();
        var initial = Description("moon[0]", 0);
        Assert.True(catalog.Stage(new CelestialBodyInstance(initial)).Success);
        Assert.False(catalog.TryGet(initial.Id, out _));
        Assert.True(catalog.Publish(new CelestialCatalogChangeSet(0, new[] { initial })).Success);
        var oldSnapshot = catalog.Capture();
        var updated = Description("moon[0]", 0, radius: 42, revision: 2);

        Assert.True(catalog.Publish(new CelestialCatalogChangeSet(1, new[] { updated })).Success);

        Assert.Equal(30, oldSnapshot.Get(initial.Id).RadiusMeters);
        Assert.Equal(1, oldSnapshot.Revision);
        Assert.Equal(42, catalog.Capture().Get(initial.Id).RadiusMeters);
        Assert.Equal(2, catalog.Revision);
    }

    [Fact]
    public void StalePublicationDoesNotOverwriteCurrentState()
    {
        var catalog = new CelestialCatalog();
        var body = Description("moon[0]", 0);
        catalog.Stage(new CelestialBodyInstance(body));
        catalog.Publish(new CelestialCatalogChangeSet(0, new[] { body }));

        var rejected = catalog.Publish(new CelestialCatalogChangeSet(0,
            new[] { Description("moon[0]", 0, radius: 42, revision: 2) }));

        Assert.False(rejected.Success);
        Assert.Equal("RevisionConflict", Assert.Single(rejected.Errors).Code);
        Assert.Equal(30, catalog.Capture().Get(body.Id).RadiusMeters);
        Assert.Equal(1, catalog.Revision);
    }

    [Fact]
    public void InvalidMemberPreventsAnEntireBatchFromPublishing()
    {
        var catalog = new CelestialCatalog();
        var first = Description("moon[0]", 0);
        var second = Description("moon[1]", 1);
        catalog.Stage(new CelestialBodyInstance(first));
        catalog.Stage(new CelestialBodyInstance(second));
        catalog.Publish(new CelestialCatalogChangeSet(0, new[] { first, second }));

        var rejected = catalog.Publish(new CelestialCatalogChangeSet(1, new[]
        {
            Description("moon[0]", 0, radius: 42, revision: 2),
            Description("moon[1]", 0, radius: 42, revision: 2)
        }));

        Assert.False(rejected.Success);
        Assert.Equal(30, catalog.Capture().Get(first.Id).RadiusMeters);
        Assert.Equal(30, catalog.Capture().Get(second.Id).RadiusMeters);
        Assert.Equal(1, catalog.Revision);
    }

    [Fact]
    public void RemovedIdentityAndHandleCannotBeAssignedToAnotherBody()
    {
        var catalog = new CelestialCatalog();
        var first = Description("moon[0]", 0);
        var second = Description("moon[1]", 1);
        catalog.Stage(new CelestialBodyInstance(first));
        catalog.Stage(new CelestialBodyInstance(second));
        catalog.Publish(new CelestialCatalogChangeSet(0, new[] { first, second }));
        Assert.True(catalog.Publish(new CelestialCatalogChangeSet(1,
            Array.Empty<CelestialBodyDescription>(), new[] { first.Id })).Success);

        Assert.Equal(2, catalog.AllocateHandle());
        Assert.False(catalog.Stage(new CelestialBodyInstance(Description("replacement", 0))).Success);
        Assert.False(catalog.Stage(new CelestialBodyInstance(Description("moon[0]", 3))).Success);
        Assert.False(catalog.Capture().TryGet(first.Id, out _));
        Assert.True(catalog.TryResolveHandle(0, out var historicalId));
        Assert.Equal(first.Id, historicalId);
    }

    [Fact]
    public void CallerOwnedCollectionsCannotMutatePublishedFactsOrState()
    {
        var tags = new List<string> { "natural" };
        var description = new CelestialBodyDescription(CelestialInstanceId.Parse("moon"),
            "PF_Moon", 0, 1, "Moon", 30, 5e12, tags);
        var bytes = new byte[] { 4 };
        var record = new CelestialCompSaveRecord("counter", "sample.counter", 1, 1, bytes, bytes);
        tags.Clear();
        bytes[0] = 99;

        Assert.Equal("natural", Assert.Single(description.Tags));
        Assert.Equal(4, record.StatePayload.Span[0]);
        Assert.Equal(4, record.ConfigurationPayload.Span[0]);
    }

    private static CelestialBodyDescription Description(string id, int handle, double radius = 30, long revision = 1)
        => new(CelestialInstanceId.Parse(id), "PF_Moon", handle, revision, "Moon", radius, 5e12);
}
