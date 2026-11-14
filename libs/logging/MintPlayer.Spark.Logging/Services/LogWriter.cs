using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Logging.Models;

namespace MintPlayer.Spark.Logging.Services;

/// <summary>
/// Appends a closed scope's entries to its <c>Log</c> document (#660 G10): load, add, save against the loaded version,
/// and start over when another writer got there first. The storage abstraction has no patch, so this is the append.
/// Its own session: the request's may be rolled back, and the log must survive exactly that.
/// </summary>
internal sealed partial class LogWriter
{
    internal const int MaxAttempts = 5;

    [Inject] private readonly ISparkStore store;

    public async Task AppendAsync(LogScopeState state, IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var session = store.OpenSession();
            var log = await session.LoadAsync<Log>(state.Id, cancellationToken);
            string expectedVersion;
            if (log is null)
            {
                log = new Log { Id = state.Id };
                expectedVersion = string.Empty; // only while nothing is stored there yet
            }
            else
            {
                // The Logs/ prefix keeps keys out of other collections; this keeps a document stored there by hand out.
                if (!string.Equals(session.GetStoredCollectionName(log), session.GetCollectionName(typeof(Log)), StringComparison.Ordinal))
                    throw new InvalidOperationException($"'{state.Id}' is not a log.");
                expectedVersion = session.GetVersion(log) ?? string.Empty;
            }

            log.RelatedPersistentObjectId ??= state.RelatedPersistentObjectId;
            if (state.ExpiresAtUtc is { } expiresAtUtc)
                log.ExpiresAtUtc = expiresAtUtc;
            log.Entries.AddRange(entries);

            await session.StoreAsync(log, expectedVersion, state.Id, cancellationToken);
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (SparkStoreConcurrencyException) when (attempt < MaxAttempts)
            {
                // Another scope with the same key saved in between: reload and add ours after theirs.
            }
        }
    }
}
