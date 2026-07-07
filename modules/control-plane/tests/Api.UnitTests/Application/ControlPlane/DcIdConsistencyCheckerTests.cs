using Api.Application.ControlPlane;
using Application.ControlPlane;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Api.UnitTests.Application.ControlPlane;

public class DcIdConsistencyCheckerTests
{
    [Fact]
    public void Compare_WhenConfiguredDcHasNoReportingLease_ReportsMissingLease()
    {
        // Acceptance: configured {west, east}, leases report {west} -> "east has no reporting lease".
        var configured = new[] { "west", "east" };
        var reporting = new[] { "west" };

        var result = DcIdConsistencyChecker.Compare(configured, reporting);

        Assert.Equal(new[] { "east" }, result.MissingLeases);
        Assert.Empty(result.UnknownDcs);
    }

    [Fact]
    public void Compare_WhenReportingDcIsNotConfigured_ReportsUnknownDc()
    {
        // Acceptance: leases report {west, south}, configured {west} -> "south is an unknown DC".
        var configured = new[] { "west" };
        var reporting = new[] { "west", "south" };

        var result = DcIdConsistencyChecker.Compare(configured, reporting);

        Assert.Empty(result.MissingLeases);
        Assert.Equal(new[] { "south" }, result.UnknownDcs);
    }

    [Fact]
    public void Compare_WhenAllMatch_ReportsNoMismatch()
    {
        // Acceptance: all-match -> no warning.
        var configured = new[] { "west", "east" };
        var reporting = new[] { "east", "west" };

        var result = DcIdConsistencyChecker.Compare(configured, reporting);

        Assert.Empty(result.MissingLeases);
        Assert.Empty(result.UnknownDcs);
        Assert.False(result.HasMismatch);
    }

    [Fact]
    public void Compare_IsCaseSensitiveAndOrdinal_MatchingLeaseStoreDcIdSemantics()
    {
        // Lease DcIds are compared ordinally elsewhere (HashSet<string> default), so a case
        // difference is a genuine mismatch (a likely typo), surfaced on both sides.
        var configured = new[] { "West" };
        var reporting = new[] { "west" };

        var result = DcIdConsistencyChecker.Compare(configured, reporting);

        Assert.Equal(new[] { "West" }, result.MissingLeases);
        Assert.Equal(new[] { "west" }, result.UnknownDcs);
        Assert.True(result.HasMismatch);
    }

    [Fact]
    public void Compare_IgnoresEmptyOrWhitespaceConfiguredDcIds()
    {
        // Empty/ordinal-fallback configured DcIds carry no operator-meaningful identity, so they
        // are excluded from the comparison rather than reported as missing leases.
        var configured = new[] { "west", "", "  " };
        var reporting = new[] { "west" };

        var result = DcIdConsistencyChecker.Compare(configured, reporting);

        Assert.Empty(result.MissingLeases);
        Assert.Empty(result.UnknownDcs);
    }

    [Fact]
    public void Compare_DeduplicatesAndSortsForStableMessages()
    {
        var configured = new[] { "east", "west", "east" };
        var reporting = new[] { "north", "south", "north" };

        var result = DcIdConsistencyChecker.Compare(configured, reporting);

        // both configured DcIds lack a reporting lease, sorted for stable log output
        Assert.Equal(new[] { "east", "west" }, result.MissingLeases);
        // both reporting DcIds are unknown, sorted
        Assert.Equal(new[] { "north", "south" }, result.UnknownDcs);
    }

    // ----- #71b leader gating -----

    /// <summary>
    /// Minimal construction of the worker itself (not just the static <see cref="DcIdConsistencyChecker.Compare"/>
    /// helper the rest of this file exercises): an in-memory <see cref="IConfiguration"/> and a
    /// scope factory resolving a mocked <see cref="ILeaseStore"/>, matching how DI wires the real
    /// worker via <c>IServiceScopeFactory</c>.
    /// </summary>
    [Fact]
    public async Task RunOnceAsync_WhenNotLeader_ReturnsNull_WithoutTouchingLeaseStore()
    {
        var leaseStore = new Mock<ILeaseStore>();
        var services = new ServiceCollection();
        services.AddTransient(_ => leaseStore.Object);
        var provider = services.BuildServiceProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlPlane:ConsistencyMode"] = "GatedCommit"
            })
            .Build();

        var sut = new DcIdConsistencyChecker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            new FakeLeaderElection(isLeader: false),
            NullLogger<DcIdConsistencyChecker>.Instance);

        var result = await sut.RunOnceAsync();

        Assert.Null(result);
        leaseStore.Verify(x => x.GetLiveSetAsync(It.IsAny<DateTimeOffset>()), Times.Never);
    }
}
