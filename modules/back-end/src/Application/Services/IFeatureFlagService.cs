using Application.Bases.Models;
using Application.FeatureFlags;
using Domain.FeatureFlags;
using Domain.Segments;

namespace Application.Services;

public interface IFeatureFlagService : IService<FeatureFlag>
{
    Task<PagedResult<FeatureFlag>> GetListAsync(Guid envId, FeatureFlagFilter filter);

    Task<FeatureFlag> GetAsync(Guid envId, string key);

    Task<bool> HasKeyBeenUsedAsync(Guid envId, string key);

    Task<ICollection<string>> GetAllTagsAsync(Guid envId);

    Task<ICollection<Segment>> GetRelatedSegmentsAsync(ICollection<FeatureFlag> flags);

    Task MarkAsUpdatedAsync(ICollection<Guid> flagIds, Guid operatorId);

    /// <summary>
    /// Authoritative read: returns the COMMITTED flag value, never a pending
    /// (staged-but-not-committed) change. The returned flag has its <c>Pending</c>
    /// slot cleared so callers cannot accidentally serve staged data.
    /// </summary>
    Task<FeatureFlag> GetCommittedAsync(Guid envId, string key);

    /// <summary>
    /// Stage <paramref name="pendingValue"/> as a pending change on the flag identified
    /// by <paramref name="envId"/>/<paramref name="key"/>. The committed value is left
    /// untouched, so <see cref="GetCommittedAsync"/> still returns the old value.
    /// </summary>
    Task SetPendingAsync(Guid envId, string key, FeatureFlag pendingValue, long version);

    /// <summary>
    /// Promote the staged pending change to committed for the flag identified by
    /// <paramref name="envId"/>/<paramref name="key"/>, so <see cref="GetCommittedAsync"/>
    /// returns the new value. No-op when there is no pending change.
    /// </summary>
    Task PromotePendingAsync(Guid envId, string key);

    /// <summary>
    /// Enumerate every flag (across all envs) that currently has a staged-but-not-committed
    /// change, i.e. <see cref="FeatureFlag.Pending"/> is not null. Used by the commit
    /// coordinator to discover the set of pending changes it must reconcile.
    /// </summary>
    Task<IReadOnlyList<FeatureFlag>> GetPendingAsync();
}