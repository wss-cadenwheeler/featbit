using Domain.Segments;
using Domain.Targeting;
using Infrastructure.Persistence.MongoDb;
using Infrastructure.Services.MongoDb;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Application.IntegrationTests.Segments;

/// <summary>
/// S1 acceptance (Mongo): the authoritative committed read must return the COMMITTED segment
/// value, never a pending (staged-but-not-committed) change. After promotion the committed read
/// returns the new value with an advanced CommittedVersion. Promotion is version-guarded.
///
/// Integration test against a real MongoDB instance. Uses a UNIQUE throwaway database that is
/// dropped on dispose. Fails loudly if no Mongo is reachable.
/// </summary>
public sealed class SegmentCommittedPendingTests : IAsyncLifetime
{
    private const string ConnectionString = "mongodb://admin:password@localhost:27017/?authSource=admin";

    private readonly string _dbName = $"featbit_s1_test_{Guid.NewGuid():N}";
    private MongoDbClient _mongoDb = null!;
    private SegmentService _sut = null!;
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _envId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var options = Options.Create(new MongoDbOptions
        {
            ConnectionString = ConnectionString,
            Database = _dbName
        });

        _mongoDb = new MongoDbClient(options);

        // fail fast / readable skip if Mongo is not available
        await _mongoDb.Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(
            new MongoDB.Bson.BsonDocument("ping", 1)
        );

        _sut = new SegmentService(_mongoDb, NullLogger<SegmentService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _mongoDb.Database.Client.DropDatabaseAsync(_dbName);
    }

    private Segment CreateSegment(string key, string description)
    {
        return new Segment(
            workspaceId: _workspaceId,
            envId: _envId,
            name: key,
            key: key,
            type: SegmentType.EnvironmentSpecific,
            scopes: [],
            included: [],
            excluded: [],
            rules: new List<MatchRule>(),
            description: description
        );
    }

    [Fact]
    public async Task SetPending_Then_Promote_Committed_Read_Returns_Old_Then_New_Value()
    {
        // Arrange: committed segment, description "old", CommittedVersion = 1
        var committed = CreateSegment("s1-segment", "old");
        committed.CommittedVersion = 1;
        await _sut.AddOneAsync(committed);

        // build the pending value: same segment but description "new"
        var pendingValue = CreateSegment("s1-segment", "new");

        // Act 1: stage a pending change (version 2)
        await _sut.SetPendingAsync(committed.Id, pendingValue, version: 2);

        // Assert 1: committed read still returns the OLD value, with no pending leaked
        var afterStage = await _sut.GetCommittedAsync(committed.Id);
        Assert.Equal("old", afterStage.Description);
        Assert.Equal(1, afterStage.CommittedVersion);
        Assert.Null(afterStage.Pending);

        // sanity: the pending change really was persisted (raw read keeps it)
        var raw = await _sut.GetAsync(committed.Id);
        Assert.NotNull(raw.Pending);
        Assert.Equal(2, raw.Pending!.Version);
        Assert.Equal("new", raw.Pending.Value.Description);

        // Act 2: promote pending -> committed (guarded on the staged version 2)
        var promoted = await _sut.PromotePendingAsync(committed.Id, expectedVersion: 2);
        Assert.True(promoted);

        // Assert 2: committed read now returns the NEW value and advanced version
        var afterPromote = await _sut.GetCommittedAsync(committed.Id);
        Assert.Equal("new", afterPromote.Description);
        Assert.Equal(2, afterPromote.CommittedVersion);
        Assert.Null(afterPromote.Pending);

        // pending slot cleared on the stored document too
        var rawAfter = await _sut.GetAsync(committed.Id);
        Assert.Null(rawAfter.Pending);
    }

    [Fact]
    public async Task GetCommitted_With_No_Pending_Returns_Committed_Value()
    {
        var committed = CreateSegment("s1-no-pending", "value");
        committed.CommittedVersion = 5;
        await _sut.AddOneAsync(committed);

        var read = await _sut.GetCommittedAsync(committed.Id);

        Assert.Equal("value", read.Description);
        Assert.Equal(5, read.CommittedVersion);
        Assert.Null(read.Pending);
    }

    [Fact]
    public async Task PromotePending_Is_NoOp_When_No_Pending()
    {
        var committed = CreateSegment("s1-promote-noop", "old");
        committed.CommittedVersion = 3;
        await _sut.AddOneAsync(committed);

        var promoted = await _sut.PromotePendingAsync(committed.Id, expectedVersion: 4);
        Assert.False(promoted);

        var read = await _sut.GetCommittedAsync(committed.Id);
        Assert.Equal("old", read.Description);
        Assert.Equal(3, read.CommittedVersion);
        Assert.Null(read.Pending);
    }

    [Fact]
    public async Task PromotePending_Is_NoOp_When_Version_Mismatch()
    {
        // a stale coordinator (expecting v2) must NOT clobber a newer staged pending (v3) — #33
        var committed = CreateSegment("s1-version-mismatch", "old");
        committed.CommittedVersion = 1;
        await _sut.AddOneAsync(committed);

        var pendingValue = CreateSegment("s1-version-mismatch", "new");
        await _sut.SetPendingAsync(committed.Id, pendingValue, version: 3);

        // coordinator tries to promote expecting the older version 2 -> guard rejects it
        var promoted = await _sut.PromotePendingAsync(committed.Id, expectedVersion: 2);
        Assert.False(promoted);

        // committed value untouched and the v3 pending is still intact
        var read = await _sut.GetCommittedAsync(committed.Id);
        Assert.Equal("old", read.Description);
        Assert.Equal(1, read.CommittedVersion);

        var raw = await _sut.GetAsync(committed.Id);
        Assert.NotNull(raw.Pending);
        Assert.Equal(3, raw.Pending!.Version);
    }

    [Fact]
    public async Task GetPending_Returns_Only_Segments_With_Pending()
    {
        var committedOnly = CreateSegment("s1-committed-only", "x");
        await _sut.AddOneAsync(committedOnly);

        var pendingSeg = CreateSegment("s1-with-pending", "x");
        await _sut.AddOneAsync(pendingSeg);
        await _sut.SetPendingAsync(pendingSeg.Id, CreateSegment("s1-with-pending", "y"), version: 2);

        var pending = await _sut.GetPendingAsync();

        Assert.Single(pending);
        Assert.All(pending, s => Assert.NotNull(s.Pending));
        Assert.Equal("s1-with-pending", pending[0].Key);
    }

    [Fact]
    public async Task GetAllCommitted_Returns_All_Segments_With_Pending_Stripped()
    {
        var a = CreateSegment("s1-all-a", "x");
        await _sut.AddOneAsync(a);

        var b = CreateSegment("s1-all-b", "x");
        await _sut.AddOneAsync(b);
        await _sut.SetPendingAsync(b.Id, CreateSegment("s1-all-b", "y"), version: 2);

        var all = await _sut.GetAllCommittedAsync();

        Assert.Equal(2, all.Count);
        Assert.All(all, s => Assert.Null(s.Pending));
    }
}
