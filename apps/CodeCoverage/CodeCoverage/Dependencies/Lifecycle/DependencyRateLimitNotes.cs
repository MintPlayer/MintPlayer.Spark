using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents;

namespace CodeCoverage.Dependencies.Lifecycle;

public interface IDependencyRateLimitNotes
{
    /// <summary>
    /// Runs <paramref name="handle"/>; when it is rate-limited, notes "waiting for the forge's rate limit until hh:mm (park
    /// n of m)" on the repository's run document, then lets the exception reach the message bus, which parks the handler
    /// (R30). A handling that completes clears the note.
    /// </summary>
    Task WatchAsync(string repositoryId, Func<Task> handle, CancellationToken cancellationToken);
}

/// <summary>
/// The status pages' view of a rate-limit park (dependency-updates PRD R30, P16): the bus keeps the park on the
/// <c>SparkMessage</c>, which no page can find by repository without a new index, so the recipient copies it onto the
/// repository's <see cref="DependencyUpdateRun"/> (<see cref="DependencyUpdateRun.RateLimitedUntilUtc"/>), in a session
/// of its own: the handler's own session is discarded with the failed attempt.
/// </summary>
[Register(typeof(IDependencyRateLimitNotes), ServiceLifetime.Scoped)]
public partial class DependencyRateLimitNotes : IDependencyRateLimitNotes
{
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IMessageContext messageContext;
    [Inject] private readonly IOptions<SparkMessagingOptions> options;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<DependencyRateLimitNotes> logger;

    public async Task WatchAsync(string repositoryId, Func<Task> handle, CancellationToken cancellationToken)
    {
        try
        {
            await handle();
        }
        catch (Exception ex) when (ForgeRateLimitedException.Find(ex) is { } limited)
        {
            await NoteAsync(repositoryId, limited, cancellationToken);
            throw;
        }
        await ClearAsync(repositoryId, cancellationToken);
    }

    private async Task NoteAsync(string repositoryId, ForgeRateLimitedException limited, CancellationToken ct)
    {
        try
        {
            using var session = store.OpenAsyncSession();
            if (await session.LoadAsync<DependencyUpdateRun>(DependencyUpdateRun.ForRepository(repositoryId), ct) is not { } run) return;
            var messageId = messageContext.MessageId;
            run.RateLimitParks = run.RateLimitMessageId == messageId ? run.RateLimitParks + 1 : 1;
            run.RateLimitMessageId = messageId;
            run.RateLimitedUntilUtc = time.GetUtcNow().UtcDateTime + limited.Delay;
            var message = await session.LoadAsync<SparkMessage>(messageId, ct);
            var maxAttempts = message?.MaxAttempts is > 0 and var max ? max : options.Value.MaxAttempts;
            run.RateLimitParkBudget = maxAttempts * Math.Max(1, options.Value.RateLimitParksPerAttemptFor(messageContext.QueueName));
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A note for the page; the park itself is the bus's and must not be lost to it.
            logger.LogWarning(ex, "Dependency updates: could not note the rate limit of {Repository}", repositoryId);
        }
    }

    private async Task ClearAsync(string repositoryId, CancellationToken ct)
    {
        using var session = store.OpenAsyncSession();
        if (await session.LoadAsync<DependencyUpdateRun>(DependencyUpdateRun.ForRepository(repositoryId), ct) is not { RateLimitedUntilUtc: not null } run) return;
        run.RateLimitedUntilUtc = null;
        run.RateLimitMessageId = null;
        run.RateLimitParks = 0;
        await session.SaveChangesAsync(ct);
    }
}
