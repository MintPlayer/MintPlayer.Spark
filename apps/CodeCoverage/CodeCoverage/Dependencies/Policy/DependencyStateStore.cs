using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Dependencies.Policy;

/// <summary>Loads and stores the dependency policy's state documents (<see cref="DependencyUpdateState"/>,
/// <see cref="DependencyFleetState"/>).</summary>
public interface IDependencyStateStore
{
    /// <summary>The repository's state; a new, tracked document when there is none yet.</summary>
    Task<DependencyUpdateState> LoadAsync(string repositoryId, CancellationToken cancellationToken = default);

    /// <summary>The account's fleet state; a new, tracked document when there is none yet.</summary>
    Task<DependencyFleetState> LoadFleetAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the repository's stored state with <paramref name="next"/> (an evaluation's
    /// <see cref="DependencyPolicyOutcome.NextState"/>) and saves.</summary>
    Task SaveAsync(string repositoryId, DependencyUpdateState next, CancellationToken cancellationToken = default);

    /// <summary>Q15: records a version that went red, for every repository of the account that has not merged it.</summary>
    Task RecordFleetRedAsync(string accountId, DependencyFleetRed red, CancellationToken cancellationToken = default);

    /// <summary>§7.6, gap E: records a release confirmed as the account's own (exempt from the cooldown).</summary>
    Task RecordOwnReleaseAsync(string accountId, DependencyOwnRelease release, CancellationToken cancellationToken = default);
}

/// <summary>
/// The state documents, always by id (<see cref="DependencyUpdateState.ForRepository"/>,
/// <see cref="DependencyFleetState.ForAccount"/>): no query, so nothing is ever spliced into RQL.
/// </summary>
[Register(typeof(IDependencyStateStore), ServiceLifetime.Scoped)]
public partial class DependencyStateStore : IDependencyStateStore
{
    [Inject] private readonly IAsyncDocumentSession session;

    public async Task<DependencyUpdateState> LoadAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        var id = DependencyUpdateState.ForRepository(repositoryId);
        var state = await session.LoadAsync<DependencyUpdateState>(id, cancellationToken);
        if (state is not null)
            return state;
        state = new DependencyUpdateState { Id = id, Repository = repositoryId };
        await session.StoreAsync(state, id, cancellationToken);
        return state;
    }

    public async Task<DependencyFleetState> LoadFleetAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var id = DependencyFleetState.ForAccount(accountId);
        var fleet = await session.LoadAsync<DependencyFleetState>(id, cancellationToken);
        if (fleet is not null)
            return fleet;
        fleet = new DependencyFleetState { Id = id, Account = accountId };
        await session.StoreAsync(fleet, id, cancellationToken);
        return fleet;
    }

    public async Task SaveAsync(string repositoryId, DependencyUpdateState next, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(repositoryId, cancellationToken);
        state.FirstSeen = next.FirstSeen;
        state.Holds = next.Holds;
        state.Approvals = next.Approvals;
        state.Slots = next.Slots;
        state.LastEvaluatedAtUtc = next.LastEvaluatedAtUtc;
        await session.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordFleetRedAsync(string accountId, DependencyFleetRed red, CancellationToken cancellationToken = default)
    {
        var fleet = await LoadFleetAsync(accountId, cancellationToken);
        fleet.RedVersions.RemoveAll(r => r.Ecosystem == red.Ecosystem && r.DepName == red.DepName && r.Version == red.Version && r.Repository == red.Repository);
        fleet.RedVersions.Add(red);
        if (fleet.RedVersions.Count > DependencyFleetState.MaxEntries)
            fleet.RedVersions = [.. fleet.RedVersions.OrderByDescending(r => r.AtUtc).Take(DependencyFleetState.MaxEntries)];
        await session.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordOwnReleaseAsync(string accountId, DependencyOwnRelease release, CancellationToken cancellationToken = default)
    {
        var fleet = await LoadFleetAsync(accountId, cancellationToken);
        fleet.OwnReleases.RemoveAll(r => r.Ecosystem == release.Ecosystem && r.DepName == release.DepName && r.Version == release.Version);
        fleet.OwnReleases.Add(release);
        if (fleet.OwnReleases.Count > DependencyFleetState.MaxEntries)
            fleet.OwnReleases = [.. fleet.OwnReleases.OrderByDescending(r => r.AtUtc).Take(DependencyFleetState.MaxEntries)];
        await session.SaveChangesAsync(cancellationToken);
    }
}
