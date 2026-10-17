using CodeCoverage.Entities;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Dependencies.Lifecycle;

/// <summary>
/// R23/R24: one trigger path for switching dependency updates on or off. A before-save hook of <see cref="Repository"/>
/// or <see cref="Account"/> records the transition in the save's facts (the payload of the durable after-commit hook,
/// which never sees the entity); the after-commit hook broadcasts <see cref="EDependencySignal.Onboard"/> (off → on) or
/// <see cref="EDependencySignal.Offboard"/> (on → off).
/// </summary>
/// <remarks>
/// <para>
/// <b>No new subscription</b> (R23): <see cref="IAfterSaveCommitted"/> writes an outbox message in the save's own commit,
/// delivered by Messaging's existing feeder. The broadcast is <c>BroadcastOnceAsync</c>, keyed by the repository and the
/// save's instant, so an after-commit hook that runs twice (it is at-least-once) queues one signal.
/// </para>
/// <para>
/// <b>Expedite</b> (R29/R34): a repository's own switch (the onboarding page, its Edit page) is a person waiting, so it is
/// expedited; an account's default fans out over many repositories and is not.
/// </para>
/// </remarks>
public static class DependencyUpdateSwitch
{
    /// <summary>The fact key: <see cref="On"/> or <see cref="Off"/>, absent when the save did not change the resolved setting.</summary>
    public const string Fact = "DependencyUpdates";

    public const string On = "off-to-on";

    public const string Off = "on-to-off";

    /// <summary>The transition between the stored and the saved repository, its account unchanged; null when there is none.</summary>
    public static string? Transition(Repository? before, Repository after, Account? account)
    {
        var was = before is not null && Repository.ResolveDependencyUpdates(before, account);
        var now = Repository.ResolveDependencyUpdates(after, account);
        return was == now ? null : now ? On : Off;
    }

    /// <summary>The transition of an account's default; only repositories that inherit it follow.</summary>
    public static string? Transition(Account? before, Account after)
    {
        var was = before?.DependencyUpdates ?? false;
        return was == after.DependencyUpdates ? null : after.DependencyUpdates ? On : Off;
    }

    /// <summary>The signal for one repository, deduplicated per save.</summary>
    public static Task SignalAsync(IMessageBus bus, string repositoryId, string transition, DateTimeOffset occurredAt, bool expedite, CancellationToken ct)
        => bus.BroadcastOnceAsync(new DependencyUpdateSignalMessage
        {
            RepositoryId = repositoryId,
            Kind = transition == On ? EDependencySignal.Onboard : EDependencySignal.Offboard,
            Expedite = expedite,
        }, $"dependency-{(transition == On ? "onboard" : "offboard")}:{repositoryId}:{occurredAt.UtcTicks}", new BroadcastOptions { Expedite = expedite }, ct);

    /// <summary>
    /// An account's switch, fanned out over its connected repositories that inherit the account's setting (the others
    /// decide for themselves, so their resolved setting did not change). Not expedited.
    /// </summary>
    public static async Task<int> FanOutAsync(IAsyncDocumentSession session, IMessageBus bus, string accountId, string transition, DateTimeOffset occurredAt,
        CancellationToken ct)
    {
        var repositories = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.Account == accountId)
            .Take(1024)
            .ToListAsync(ct);
        var followers = repositories.Where(r => r.Id is not null && r.Connection != RepositoryConnection.Disconnected && !r.Archived
            && r.DependencyUpdates == LookupReferences.EDependencyUpdatesPolicy.Inherit).ToList();
        foreach (var repository in followers)
            await SignalAsync(bus, repository.Id!, transition, occurredAt, expedite: false, ct);
        return followers.Count;
    }
}
