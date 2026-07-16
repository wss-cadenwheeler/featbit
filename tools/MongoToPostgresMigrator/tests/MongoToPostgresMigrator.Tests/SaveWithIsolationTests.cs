using Domain.Bases;
using Microsoft.EntityFrameworkCore;

namespace MongoToPostgresMigrator.Tests;

/// <summary>
/// Unit tests for <see cref="EntityStep{T}.SaveWithIsolationAsync"/> — the
/// binary-split path that keeps a chunk containing a constraint-violating row
/// (e.g. a string longer than a <c>varchar(128)</c> column, such as the
/// scanner-payload property names seen in production) from aborting the copy.
/// The bad row is isolated and skipped; every clean row is still saved. DB-free:
/// the save is a delegate that throws for poisoned rows, so the algorithm is
/// exercised without EF Core or PostgreSQL.
/// </summary>
public class SaveWithIsolationTests
{
    private sealed class FakeEntity : Entity;

    // Exposes the protected static isolation algorithm for testing.
    private sealed class TestableStep() : EntityStep<FakeEntity>("Fake")
    {
        public static Task<long> Isolate(
            FakeEntity[] chunk, Func<FakeEntity[], Task> saveAsync, Action<FakeEntity, Exception> onSkip) =>
            SaveWithIsolationAsync(chunk, saveAsync, onSkip);
    }

    /// <summary>
    /// A save that mimics an atomic batch insert: if any row in the batch is
    /// poisoned the whole batch throws (as a real INSERT would on a length
    /// violation); otherwise every row is recorded as saved.
    /// </summary>
    private sealed class FakeSave(IReadOnlySet<Guid> poisoned)
    {
        public readonly HashSet<Guid> Saved = new();
        public int Calls { get; private set; }

        public Task SaveAsync(FakeEntity[] batch)
        {
            Calls++;
            if (batch.Any(e => poisoned.Contains(e.Id)))
            {
                throw new DbUpdateException("value too long for type character varying(128)");
            }

            foreach (var e in batch)
            {
                Saved.Add(e.Id);
            }

            return Task.CompletedTask;
        }
    }

    private static FakeEntity[] Rows(int count) =>
        Enumerable.Range(0, count).Select(_ => new FakeEntity { Id = Guid.NewGuid() }).ToArray();

    [Fact]
    public async Task EmptyChunk_SavesNothing()
    {
        var save = new FakeSave(new HashSet<Guid>());

        var written = await TestableStep.Isolate([], save.SaveAsync, (_, _) => { });

        Assert.Equal(0, written);
        Assert.Equal(0, save.Calls);
    }

    [Fact]
    public async Task CleanChunk_SavedInOneRoundTrip()
    {
        var rows = Rows(64);
        var save = new FakeSave(new HashSet<Guid>());

        var written = await TestableStep.Isolate(rows, save.SaveAsync, (_, _) => { });

        Assert.Equal(64, written);
        Assert.Equal(1, save.Calls);
        Assert.Equal(rows.Select(r => r.Id).ToHashSet(), save.Saved);
    }

    [Fact]
    public async Task SingleBadRow_IsSkipped_AndEveryCleanRowIsSaved()
    {
        var rows = Rows(64);
        var bad = rows[40];
        var save = new FakeSave(new HashSet<Guid> { bad.Id });
        var skipped = new List<FakeEntity>();

        var written = await TestableStep.Isolate(rows, save.SaveAsync, (r, _) => skipped.Add(r));

        Assert.Equal(63, written);
        Assert.Equal(bad.Id, Assert.Single(skipped).Id);
        Assert.DoesNotContain(bad.Id, save.Saved);
        Assert.Equal(63, save.Saved.Count);
    }

    [Fact]
    public async Task SingleBadRow_IsIsolatedInAboutLog2Steps_NotOneRowAtATime()
    {
        var rows = Rows(64);
        var save = new FakeSave(new HashSet<Guid> { rows[0].Id });

        await TestableStep.Isolate(rows, save.SaveAsync, (_, _) => { });

        // A one-row-at-a-time fallback would cost ~64 calls; binary-split isolates
        // a lone bad row in far fewer. Guard well under that to prove the split.
        Assert.True(save.Calls < 20, $"expected a binary-split (<20 calls) but took {save.Calls}");
    }

    [Fact]
    public async Task MultipleBadRows_AllSkipped_RestSaved()
    {
        var rows = Rows(100);
        var poisoned = new HashSet<Guid> { rows[3].Id, rows[50].Id, rows[99].Id };
        var save = new FakeSave(poisoned);
        var skipped = new List<FakeEntity>();

        var written = await TestableStep.Isolate(rows, save.SaveAsync, (r, _) => skipped.Add(r));

        Assert.Equal(97, written);
        Assert.Equal(poisoned, skipped.Select(r => r.Id).ToHashSet());
        Assert.Equal(97, save.Saved.Count);
        Assert.Empty(save.Saved.Intersect(poisoned));
    }

    [Fact]
    public async Task SkipCallback_ReceivesTheViolationReason()
    {
        var rows = Rows(2);
        var save = new FakeSave(new HashSet<Guid> { rows[1].Id });
        Exception? reason = null;

        await TestableStep.Isolate(rows, save.SaveAsync, (_, ex) => reason = ex);

        Assert.NotNull(reason);
        Assert.Contains("character varying(128)", reason!.Message);
    }

    [Fact]
    public async Task NonDbUpdateException_IsNotSwallowed()
    {
        var rows = Rows(8);
        Func<FakeEntity[], Task> save = _ => throw new InvalidOperationException("connection lost");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestableStep.Isolate(rows, save, (_, _) => { }));
    }
}
