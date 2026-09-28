using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>What one <see cref="SparkUserBackfill{TUser}.RunAsync"/> pass did.</summary>
/// <param name="Scanned">User documents read.</param>
/// <param name="SecretsProtected">Users whose legacy plaintext authenticator key or tokens were rewritten protected.</param>
/// <param name="CreatedAtFilled">Users whose <see cref="SparkUser.CreatedAtUtc"/> was filled in.</param>
/// <param name="Conflicts">Users skipped because another writer changed them during the pass (retried on the next pass).</param>
/// <param name="Skipped">True when the completion marker already existed and nothing was read.</param>
public sealed record SparkUserBackfillResult(int Scanned, int SecretsProtected, int CreatedAtFilled, int Conflicts, bool Skipped);

/// <summary>
/// The one-off rewrite of existing user documents for #460 (D5, item 6): legacy plaintext
/// authenticator keys and external-login tokens are stored protected, and a missing
/// <see cref="SparkUser.CreatedAtUtc"/> is filled from the document's revision history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent.</b> A protected value is never re-protected, a present <c>CreatedAtUtc</c> is never
/// overwritten, and a completed pass writes the marker document <see cref="MarkerId"/>, after which a
/// pass reads nothing. A pass that lost an optimistic-concurrency race on some user leaves the marker
/// unwritten, so the next start finishes the job.
/// </para>
/// <para>
/// <b>Where <c>CreatedAtUtc</c> comes from.</b> RavenDB keeps no creation timestamp in document
/// metadata (measured, PRD §4.1 SP-B: a stored document carries <c>@last-modified</c> and no
/// <c>@created</c>), so the value is the <c>@last-modified</c> of the document's <em>oldest
/// revision</em> when revisions are enabled for the collection, and stays <see langword="null"/>
/// otherwise — a last-modified date is not a creation date, and a guess would be worse than "unknown".
/// </para>
/// <para>
/// Runs once in the background after the host starts (<see cref="SparkUserBackfillHostedService{TUser}"/>);
/// callable directly for tests and maintenance.
/// </para>
/// </remarks>
public sealed class SparkUserBackfill<TUser>
    where TUser : SparkUser
{
    /// <summary>The completion marker. Delete it to force another pass.</summary>
    public const string MarkerId = "SparkAuth/Backfills/Users.v1";

    private const int BatchSize = 256;

    private readonly IDocumentStore documentStore;
    private readonly IServiceProvider services;
    private readonly ILogger<SparkUserBackfill<TUser>> logger;

    public SparkUserBackfill(IDocumentStore documentStore, IServiceProvider services, ILogger<SparkUserBackfill<TUser>> logger)
    {
        this.documentStore = documentStore;
        this.services = services;
        this.logger = logger;
    }

    public async Task<SparkUserBackfillResult> RunAsync(CancellationToken cancellationToken = default)
    {
        using (var check = documentStore.OpenAsyncSession())
        {
            if (await check.Advanced.ExistsAsync(MarkerId, cancellationToken))
                return new(0, 0, 0, 0, Skipped: true);
        }

        // The store is used only for its protection helpers; it opens no session of its own here.
        using var protector = ActivatorUtilities.CreateInstance<UserStore<TUser>>(services);

        int scanned = 0, protectedCount = 0, createdCount = 0, conflicts = 0;
        var ids = new List<string>();

        using (var session = documentStore.OpenAsyncSession())
        {
            await using var stream = await session.Advanced.StreamAsync(session.Query<TUser>(), cancellationToken);
            while (await stream.MoveNextAsync())
            {
                if (stream.Current.Id is { } id)
                    ids.Add(id);
            }
        }

        foreach (var batch in ids.Chunk(BatchSize))
        {
            foreach (var id in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var session = documentStore.OpenAsyncSession();
                session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;

                var user = await session.LoadAsync<TUser>(id, cancellationToken);
                if (user is null)
                    continue;
                scanned++;

                var secrets = protector.ProtectLegacySecrets(user);
                var created = false;
                if (user.CreatedAtUtc is null && await OldestRevisionAtAsync(session, id, cancellationToken) is { } at)
                {
                    user.CreatedAtUtc = at;
                    created = true;
                }

                if (!secrets && !created)
                    continue;

                try
                {
                    await session.SaveChangesAsync(cancellationToken);
                    if (secrets) protectedCount++;
                    if (created) createdCount++;
                }
                catch (Raven.Client.Exceptions.ConcurrencyException)
                {
                    conflicts++;
                }
            }
        }

        if (conflicts == 0)
        {
            using var marker = documentStore.OpenAsyncSession();
            await marker.StoreAsync(new SparkUserBackfillMarker
            {
                CompletedAtUtc = DateTime.UtcNow,
                Scanned = scanned,
                SecretsProtected = protectedCount,
                CreatedAtFilled = createdCount,
            }, MarkerId, cancellationToken);
            await marker.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Spark user backfill: {Scanned} users read, {Protected} had plaintext secrets protected, {Created} had CreatedAtUtc filled, {Conflicts} conflicts{Retry}.",
            scanned, protectedCount, createdCount, conflicts, conflicts > 0 ? " (retried on the next start)" : string.Empty);

        return new(scanned, protectedCount, createdCount, conflicts, Skipped: false);
    }

    private static async Task<DateTime?> OldestRevisionAtAsync(IAsyncDocumentSession session, string id, CancellationToken cancellationToken)
    {
        try
        {
            // Newest first; a document has at most a handful of user revisions worth paging through.
            var metadata = await session.Advanced.Revisions.GetMetadataForAsync(id, start: 0, pageSize: 1024, token: cancellationToken);
            if (metadata.Count == 0)
                return null;

            return metadata[^1].TryGetValue("@last-modified", out var raw)
                && DateTime.TryParse(raw?.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : null;
        }
        catch (Raven.Client.Exceptions.RavenException)
        {
            return null;
        }
    }
}

/// <summary>The completion marker <see cref="SparkUserBackfill{TUser}"/> writes.</summary>
public sealed class SparkUserBackfillMarker
{
    public DateTime CompletedAtUtc { get; set; }
    public int Scanned { get; set; }
    public int SecretsProtected { get; set; }
    public int CreatedAtFilled { get; set; }
}

/// <summary>Runs <see cref="SparkUserBackfill{TUser}"/> once, in the background, after the host has started.</summary>
internal sealed class SparkUserBackfillHostedService<TUser>(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<SparkUserBackfillHostedService<TUser>> logger) : BackgroundService
    where TUser : SparkUser
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (services.GetService<Microsoft.Extensions.Options.IOptions<Configuration.SparkAuthenticationOptions>>()?.Value
                .BackfillUsersOnStartup == false)
            return;

        // After ApplicationStarted: never compete with startup, and never run in a host that is
        // only executing a build command and stops before it starts serving.
        var started = new TaskCompletionSource();
        using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        using (stoppingToken.Register(() => started.TrySetCanceled(stoppingToken)))
        {
            try
            {
                await started.Task;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        try
        {
            await services.GetRequiredService<SparkUserBackfill<TUser>>().RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Spark user backfill failed; it runs again on the next start.");
        }
    }
}
