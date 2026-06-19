using System.Text.Json;
using Application.Bases.Exceptions;
using Application.Bases.Models;
using Application.FeatureFlags;
using Domain.FeatureFlags;
using Domain.Segments;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services.EntityFrameworkCore;

public class FeatureFlagService(AppDbContext dbContext)
    : EntityFrameworkCoreService<FeatureFlag>(dbContext), IFeatureFlagService
{
    public async Task<PagedResult<FeatureFlag>> GetListAsync(Guid envId, FeatureFlagFilter userFilter)
    {
        var query = Queryable.Where(x => x.EnvId == envId && x.IsArchived == userFilter.IsArchived);

        // name/key filter
        var nameOrKey = userFilter.Name?.ToLower();
        if (!string.IsNullOrWhiteSpace(nameOrKey))
        {
            query = query.Where(flag => flag.Name.ToLower().Contains(nameOrKey) || flag.Key.ToLower().Contains(nameOrKey));
        }

        // isEnabled filter
        var isEnabled = userFilter.IsEnabled;
        if (isEnabled.HasValue)
        {
            query = query.Where(flag => flag.IsEnabled == isEnabled.Value);
        }

        // tags filter
        if (userFilter.Tags.Any())
        {
            query = query.Where(x => userFilter.Tags.All(y => x.Tags.Contains(y)));
        }

        var totalCount = await query.CountAsync();

        // sorting
        var sortQuery = userFilter.SortBy switch
        {
            "key" => query.OrderBy(x => x.Key),
            _ => query.OrderByDescending(x => x.CreatedAt)
        };

        var itemsQuery = sortQuery
            .Skip(userFilter.PageIndex * userFilter.PageSize)
            .Take(userFilter.PageSize);

        var items = await itemsQuery.ToListAsync();

        return new PagedResult<FeatureFlag>(totalCount, items);
    }

    public async Task<FeatureFlag> GetAsync(Guid envId, string key)
    {
        var flag = await FindOneAsync(x => x.EnvId == envId && x.Key == key);
        if (flag == null)
        {
            throw new EntityNotFoundException(nameof(FeatureFlag), $"{envId}-{key}");
        }

        return flag;
    }

    public async Task<bool> HasKeyBeenUsedAsync(Guid envId, string key)
    {
        return await AnyAsync(flag =>
            flag.EnvId == envId &&
            string.Equals(flag.Key.ToLower(), key.ToLower())
        );
    }

    public async Task<ICollection<string>> GetAllTagsAsync(Guid envId)
    {
        // https://github.com/npgsql/efcore.pg/issues/1525
        // https://github.com/dotnet/efcore/issues/32505
        // SelectMany is not supported in efcore 8.x

        var allTags = await Queryable
            .Where(x => x.EnvId == envId && !x.IsArchived)
            .Select(x => x.Tags)
            .ToListAsync();

        return allTags.SelectMany(x => x).Distinct().ToArray();
    }

    public async Task<ICollection<Segment>> GetRelatedSegmentsAsync(ICollection<FeatureFlag> flags)
    {
        var segmentIds = flags
            .SelectMany(flag => flag.Rules)
            .SelectMany(rule => rule.Conditions)
            .Where(condition => condition.IsSegmentCondition())
            .SelectMany(condition => JsonSerializer.Deserialize<string[]>(condition.Value)!)
            .Distinct()
            .Select(Guid.Parse)
            .ToArray();

        if (segmentIds.Length == 0)
        {
            return [];
        }

        var segments = await QueryableOf<Segment>()
            .Where(x => segmentIds.Contains(x.Id))
            .ToListAsync();

        return segments;
    }

    public async Task MarkAsUpdatedAsync(ICollection<Guid> flagIds, Guid operatorId)
    {
        var now = DateTime.UtcNow;

        await Set
            .Where(x => flagIds.Contains(x.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(f => f.UpdatedAt, now)
                .SetProperty(f => f.UpdatorId, operatorId)
            );
    }

    // Committed-vs-pending, Postgres/EF parity with the Mongo FeatureFlagService.
    // Matches the Mongo behavior exactly (including its current limitations); no
    // concurrency/monotonicity guards here — those are tracked as #33/#34.

    public async Task<FeatureFlag> GetCommittedAsync(Guid envId, string key)
    {
        // No-tracking so stripping the pending slot below is purely a read-shaping
        // operation and never accidentally persisted on a later SaveChanges.
        var flag = await Queryable
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.EnvId == envId && x.Key == key);
        if (flag == null)
        {
            throw new EntityNotFoundException(nameof(FeatureFlag), $"{envId}-{key}");
        }

        // The committed read must NEVER expose a pending (staged) change. The top-level
        // row is the committed value; drop the pending slot before returning it.
        flag.Pending = null;

        return flag;
    }

    public async Task SetPendingAsync(Guid envId, string key, FeatureFlag pendingValue, long version)
    {
        // load the committed row (left otherwise untouched)
        var flag = await GetAsync(envId, key);

        // write ONLY the pending data; committed fields stay as they are
        flag.SetPending(pendingValue, version);

        await UpdateAsync(flag);
    }

    public async Task<bool> PromotePendingAsync(Guid envId, string key, long expectedVersion)
    {
        var flag = await GetAsync(envId, key);

        // Version guard (#33/#34): only promote if the pending change still matches the version
        // the caller observed. If it was replaced by a racing SetPendingAsync (different version)
        // or already promoted (null), do nothing.
        if (flag.Pending?.Version != expectedVersion)
        {
            return false;
        }

        // promote pending -> committed, then persist the full row so the committed
        // value advances and the pending slot is cleared.
        flag.PromotePending();

        // NOTE (#33): without a rowversion/concurrency token there is a residual non-atomic window
        // between the GetAsync above and SaveChanges here — a racing SetPendingAsync committing in
        // that window could be overwritten. The in-memory version check narrows but does not close
        // it for the EF provider; closing it requires an optimistic-concurrency token (tracked in
        // #33). The Mongo provider closes it via a version-filtered ReplaceOneAsync.
        await UpdateAsync(flag);
        return true;
    }

    public async Task<IReadOnlyList<FeatureFlag>> GetPendingAsync()
    {
        // Pending is the jsonb column (B4). Postgres translates "pending IS NOT NULL"
        // for the whole jsonb document, so this is a server-side scan across all envs.
        return await Queryable
            .Where(f => f.Pending != null)
            .ToListAsync();
    }
}