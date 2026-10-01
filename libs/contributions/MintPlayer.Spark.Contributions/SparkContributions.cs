using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Contributions;

/// <summary>
/// <see cref="IContributions"/>: repairs the current-document cache from the contributions. Each run uses
/// its own session (never the request's), and a concurrent write is retried with fresh reads.
/// </summary>
internal sealed class SparkContributions(IDocumentStore store, ContributionCatalog catalog, ILogger<SparkContributions> logger) : IContributions
{
    /// <summary>Attempts per unit of work when a concurrent write moves a current document (S-C4).</summary>
    internal const int MaxAttempts = 3;

    public async Task RebuildCurrentAsync(string targetId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetId);
        foreach (var handler in catalog.All)
            await RebuildAsync(store, handler, [targetId], logger, cancellationToken);
    }

    /// <summary>Rebuilds <paramref name="targetIds"/> for one declaration in one session, retried on a conflict.</summary>
    internal static async Task RebuildAsync(IDocumentStore store, IContributionHandler handler, IReadOnlyCollection<string> targetIds, ILogger logger, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var session = store.OpenAsyncSession();
            // A prefix load per target and per slot: allow what the batch needs, not the default 30.
            session.Advanced.MaxNumberOfRequestsPerSession = Math.Max(30, targetIds.Count * 8);
            try
            {
                foreach (var targetId in targetIds)
                    await handler.RebuildAsync(targetId, session);
                await session.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
                logger.LogDebug("Contribution rebuild of {Count} {Target} target(s) met a concurrent write; retrying ({Attempt}/{Max}).",
                    targetIds.Count, handler.Descriptor.TargetType.Name, attempt, MaxAttempts);
            }
        }
    }
}
