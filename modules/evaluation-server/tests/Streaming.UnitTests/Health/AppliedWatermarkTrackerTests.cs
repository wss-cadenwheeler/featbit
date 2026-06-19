using Streaming.Health;

namespace Streaming.UnitTests.Health;

public class AppliedWatermarkTrackerTests
{
    private static AppliedWatermarkTracker CreateSut() => new();

    [Fact]
    public void Snapshot_WhenNothingUpdated_IsEmpty()
    {
        var sut = CreateSut();

        var snapshot = sut.Snapshot();

        Assert.Empty(snapshot);
    }

    [Fact]
    public void Update_RecordsVersionForEnv()
    {
        var sut = CreateSut();
        var envId = Guid.NewGuid();

        sut.Update(envId, 100);

        Assert.Equal(100, sut.Snapshot()[envId]);
    }

    [Fact]
    public void Update_KeepsMaximum_WhenLowerVersionArrivesLater()
    {
        var sut = CreateSut();
        var envId = Guid.NewGuid();

        sut.Update(envId, 200);
        sut.Update(envId, 100);

        Assert.Equal(200, sut.Snapshot()[envId]);
    }

    [Fact]
    public void Update_AdvancesToMaximum_WhenHigherVersionArrives()
    {
        var sut = CreateSut();
        var envId = Guid.NewGuid();

        sut.Update(envId, 100);
        sut.Update(envId, 300);

        Assert.Equal(300, sut.Snapshot()[envId]);
    }

    [Fact]
    public void Update_TracksEnvironmentsIndependently()
    {
        var sut = CreateSut();
        var env1 = Guid.NewGuid();
        var env2 = Guid.NewGuid();

        sut.Update(env1, 100);
        sut.Update(env2, 250);

        var snapshot = sut.Snapshot();
        Assert.Equal(100, snapshot[env1]);
        Assert.Equal(250, snapshot[env2]);
    }

    [Fact]
    public void Snapshot_IsAPointInTimeCopy()
    {
        var sut = CreateSut();
        var envId = Guid.NewGuid();
        sut.Update(envId, 100);

        var snapshot = sut.Snapshot();
        sut.Update(envId, 200);

        // The previously captured snapshot must not be mutated by later updates.
        Assert.Equal(100, snapshot[envId]);
        Assert.Equal(200, sut.Snapshot()[envId]);
    }

    [Fact]
    public void Update_ConcurrentWrites_KeepMaximum()
    {
        var sut = CreateSut();
        var envId = Guid.NewGuid();

        Parallel.For(1, 1001, i => sut.Update(envId, i));

        Assert.Equal(1000, sut.Snapshot()[envId]);
    }
}
