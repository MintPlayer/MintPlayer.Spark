using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.ReferenceData.Import;

/// <summary>One import run (PRD §4.5 step 5): what ran, what it changed, and why it stopped. The admin pages list them.</summary>
public sealed class RefImportRun
{
    public string? Id { get; set; }

    /// <summary>What started it: <c>schedule:be-refnis</c>, <c>admin</c>.</summary>
    public string Trigger { get; set; } = string.Empty;

    public string Status { get; set; } = RefImportStatus.Running;

    /// <summary>The source versions read: <c>be-refnis@20250101</c>.</summary>
    public List<string> Sources { get; set; } = [];

    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }

    public int Added { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }

    /// <summary>Stored documents the sources no longer list: kept (retire, never delete) and counted here.</summary>
    public int Absent { get; set; }

    public string? Release { get; set; }

    public List<string> Conflicts { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string? Message { get; set; }
}

public static class RefImportStatus
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string NoChange = "no-change";
    public const string Refused = "refused";
    public const string Failed = "failed";
}

/// <summary>An instance-local ledger line (Q-R5): an id first minted here, kept until an adapter release ships it.</summary>
public sealed class RefLedgerEntry
{
    public string? Id { get; set; }
    public string Identity { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

/// <summary>Queues an import (RD1 step 3). Deduplicated by <see cref="Key"/> through <c>BroadcastOnceAsync</c>.</summary>
[MessageQueue(RefDataImportMessage.Queue)]
public sealed class RefDataImportMessage
{
    public const string Queue = "refdata-import";

    public string Trigger { get; set; } = string.Empty;

    /// <summary><c>{source}/{version}</c> for a scheduled change, <c>admin/{ticks}</c> for "Import now".</summary>
    public string Key { get; set; } = string.Empty;
}

/// <summary>
/// Registers the per-source schedules. The job class is a private nested generic so Spark's discovery generator, which
/// registers every non-private cron job it finds, does not try to register the open generic (it would emit the type
/// parameter as a type name).
/// </summary>
internal static class RefDataSchedules
{
    public static void AddSourceCheck<TSource>(MintPlayer.Spark.Abstractions.Builder.ISparkBuilder spark, string schedule, string name)
        where TSource : class, IReferenceDataSource
        => spark.AddCron(cron => cron.AddJob<SourceCheckJob<TSource>>(schedule, name));

    /// <summary>
    /// The per-source schedule (RD1 step 1): the job only checks and queues. A changed source records its new version
    /// and queues one import; an unchanged one records the check ("no change").
    /// </summary>
    private sealed class SourceCheckJob<TSource>(
        TSource source,
        IDocumentStore store,
        IHttpClientFactory httpClients,
        IMessageBus bus,
        TimeProvider time) : ISparkCronJob
        where TSource : class, IReferenceDataSource
    {
        /// <summary>Unused: the schedule comes from the source (<c>Spark:ReferenceData:Sources:{id}:Schedule</c>).</summary>
        public static string CronSchedule => "0 0 4 * * *";

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var session = store.OpenAsyncSession();
            var stateId = SourceState.IdFor(source.Id);
            var state = await session.LoadAsync<SourceState>(stateId, cancellationToken) ?? new SourceState { Id = stateId, SourceId = source.Id };
            var version = await source.CheckAsync(state.Version is null ? null : state, httpClients.CreateClient(RefDownloads.HttpClientName), cancellationToken);
            state.CheckedAtUtc = time.GetUtcNow();
            if (version is not null)
            {
                state.Version = version.Version;
                state.Url = version.Url;
                state.ETag = version.ETag;
                state.LastModified = version.LastModified;
                state.Sha256 = version.Sha256;
            }
            await session.StoreAsync(state, stateId, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            if (version is not null)
                await bus.BroadcastOnceAsync(new RefDataImportMessage { Trigger = "schedule:" + source.Id, Key = $"{source.Id}/{version.Version}" }, $"{source.Id}/{version.Version}", cancellationToken);
        }
    }
}

/// <summary>
/// Runs an import (PRD §4.5 steps 4–10): fetches the current version of every registered source, builds and derives the
/// whole dataset through the ledgers, refuses it on a ledger conflict or an implausible retirement, writes what changed,
/// and records a release. Never inside a migration. A rerun of the same versions writes nothing (RS2).
/// </summary>
/// <remarks>
/// Resuming from a partition cursor (<see cref="ICheckpointRecipient{TMessage}"/>) is not built: a run is idempotent
/// and is rerun whole after a restart. The first load writes through sessions in chunks, not BulkInsert.
/// </remarks>
internal sealed class RefDataImportRecipient(
    IServiceProvider services,
    IDocumentStore store,
    IHttpClientFactory httpClients,
    IOptions<SparkReferenceDataOptions> options,
    IHostEnvironment environment,
    TimeProvider time,
    ILogger<RefDataImportRecipient> logger) : IRecipient<RefDataImportMessage>
{
    public async Task HandleAsync(RefDataImportMessage message, CancellationToken cancellationToken = default)
    {
        var builder = services.GetRequiredService<RefDatasetBuilder>();
        var run = new RefImportRun
        {
            Id = $"refdata/importruns/{time.GetUtcNow():yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..48],
            Trigger = message.Trigger,
            StartedAtUtc = time.GetUtcNow(),
        };
        await SaveRunAsync(run, cancellationToken);

        var work = Directory.CreateTempSubdirectory("spark-refdata-");
        try
        {
            var http = httpClients.CreateClient(RefDownloads.HttpClientName);
            var inputs = new Dictionary<string, SourceInput>(StringComparer.Ordinal);
            foreach (var source in builder.Sources)
            {
                var version = await source.CheckAsync(null, http, cancellationToken);
                if (version is null) continue;
                var folder = work.CreateSubdirectory(source.Id);
                inputs[source.Id] = await source.FetchAsync(version, http, folder, cancellationToken);
                run.Sources.Add($"{source.Id}@{version.Version}");
            }

            List<LedgerEntry> local;
            using (var session = store.OpenAsyncSession())
                local = [.. (await session.Query<RefLedgerEntry>().Take(int.MaxValue).ToListAsync(cancellationToken))
                    .Select(e => new LedgerEntry(e.Identity, e.DocumentId, e.Source))];

            RefDataBuild build;
            try
            {
                build = await builder.BuildAsync(inputs, local, cancellationToken);
            }
            catch (LedgerConflictException ex)
            {
                run.Status = RefImportStatus.Refused;
                run.Conflicts = [.. ex.Conflicts.Select(c => c.ToString())];
                run.Message = "The id ledger refused the import; nothing was written (§4.3).";
                return;
            }
            run.Warnings = [.. build.Warnings.Take(200)];

            var writer = new RefDataWriter(store, options.Value, environment, time);
            await writer.WriteAsync(build, run, cancellationToken);
            if (build.Ledger.Added.Count > 0)
            {
                using var session = store.OpenAsyncSession();
                foreach (var entry in build.Ledger.Added)
                    await session.StoreAsync(new RefLedgerEntry { Identity = entry.Identity, DocumentId = entry.Id, Source = entry.Source },
                        "refdata/ledger/" + entry.Identity.Replace('/', '_'), cancellationToken);
                await session.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Status = RefImportStatus.Failed;
            run.Message = ex.Message;
            logger.LogError(ex, "Reference-data import {Run} failed.", run.Id);
            throw;
        }
        finally
        {
            run.FinishedAtUtc = time.GetUtcNow();
            await SaveRunAsync(run, CancellationToken.None);
            try { work.Delete(recursive: true); } catch (IOException) { }
        }
    }

    private async Task SaveRunAsync(RefImportRun run, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(run, run.Id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Writes a derived build (PRD §4.5 steps 7, 8 and 10): a document whose hash matches the stored one is skipped; a
/// changed one is stamped with the new release; a stored document the build lacks is kept and counted (retire, never
/// delete), and a build that would leave more than <see cref="ReferenceDataReleaseOptions.MaxRetiredShare"/> of a
/// collection absent is refused as a broken download. A run that changed anything records a release and writes its files.
/// </summary>
internal sealed class RefDataWriter(IDocumentStore store, SparkReferenceDataOptions options, IHostEnvironment environment, TimeProvider time)
{
    public async Task WriteAsync(RefDataBuild build, RefImportRun run, CancellationToken cancellationToken)
    {
        var documents = build.Documents().ToList();
        var release = await NextReleaseAsync(cancellationToken);

        using (var session = store.OpenAsyncSession())
        {
            foreach (var collection in ReleaseFormat.Collections)
            {
                var stored = await session.Advanced.AsyncRawQuery<StoredHash>($"from {MintPlayer.Spark.Abstractions.RqlIdentifier.Collection(collection.Name)} select id() as Id, Hash")
                    .Take(int.MaxValue).ToListAsync(cancellationToken);
                var built = documents.Where(d => d.GetType() == collection.Type).Select(ReleaseWriter.IdOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var absent = stored.Count(s => !built.Contains(s.Id));
                if (stored.Count > 50 && absent > stored.Count * options.Releases.MaxRetiredShare)
                {
                    run.Status = RefImportStatus.Refused;
                    run.Message = $"{absent} of {stored.Count} {collection.Name} would be absent: refused as a broken download (§4.5 step 8).";
                    return;
                }
                run.Absent += absent;
            }
        }

        var changed = new List<object>();
        foreach (var chunk in documents.Chunk(256))
        {
            using var session = store.OpenAsyncSession();
            session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
            foreach (var group in chunk.GroupBy(d => d.GetType()))
            {
                var ids = group.Select(ReleaseWriter.IdOf).ToList();
                var existing = await session.Advanced.AsyncRawQuery<StoredStamp>(
                        $"from {MintPlayer.Spark.Abstractions.RqlIdentifier.Collection(ReleaseFormat.CollectionOf(group.Key).Name)} where id() in ($ids) select id() as Id, Hash, Release")
                    .AddParameter("ids", ids).Take(int.MaxValue).ToListAsync(cancellationToken);
                var byId = existing.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
                foreach (var document in group)
                {
                    var id = ReleaseWriter.IdOf(document);
                    var hash = ReleaseWriter.HashOf(document);
                    var stamp = byId.TryGetValue(id, out var old) ? old : null;
                    if (stamp?.Hash == hash)
                    {
                        run.Unchanged++;
                        document.GetType().GetProperty("Release")!.SetValue(document, stamp.Release);
                        continue;
                    }
                    document.GetType().GetProperty("Release")!.SetValue(document, new ReleaseStamp { Introduced = stamp?.Release?.Introduced ?? release, Changed = release });
                    await session.StoreAsync(document, id, cancellationToken);
                    changed.Add(document);
                    if (stamp is null) run.Added++; else run.Updated++;
                }
            }
            await session.SaveChangesAsync(cancellationToken);
        }

        if (changed.Count == 0)
        {
            run.Status = RefImportStatus.NoChange;
            return;
        }

        // The release (step 10): the instance's full state as canonical files, from the build (it holds every id).
        var manifest = new ReleaseManifest
        {
            Release = release,
            Previous = await LatestReleaseAsync(cancellationToken),
            Builder = "MintPlayer.Spark.ReferenceData " + typeof(RefDataWriter).Assembly.GetName().Version,
            KeyVersion = RefSearchKeys.KeyVersion,
            Sources = [.. build.Sources.Values],
            Changes = changed.GroupBy(d => RefIds.CountryOf(ReleaseWriter.IdOf(d)) ?? "_").ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            NextEffectiveDate = NextEffectiveDate(build, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)),
        };
        var directory = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Releases.Directory, release));
        var manifestBytes = await ReleaseWriter.WriteAsync(new DirectoryReleaseFileStore(directory), manifest, documents, cancellationToken);

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new RefRelease
            {
                Release = release,
                Previous = manifest.Previous,
                CreatedAtUtc = time.GetUtcNow(),
                ManifestSha256 = ReleaseCanonical.Sha256(manifestBytes),
                Counts = manifest.Files.GroupBy(f => f.Collection).ToDictionary(g => g.Key, g => g.Sum(f => f.Count), StringComparer.Ordinal),
            }, RefIds.Releases + "/" + release, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
        run.Release = release;
        run.Status = RefImportStatus.Succeeded;
    }

    /// <summary><c>{yyyy}.{MM}.{n}</c>: one release per changing run (Q-R17), numbered within the month.</summary>
    private async Task<string> NextReleaseAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var prefix = now.ToString("yyyy.MM.", CultureInfo.InvariantCulture);
        using var session = store.OpenAsyncSession();
        var count = await session.Advanced.AsyncDocumentQuery<RefRelease>()
            .WhereStartsWith(nameof(RefRelease.Release), prefix)
            .CountAsync(cancellationToken);
        return prefix + count.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<string?> LatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        return (await session.Query<RefRelease>().OrderByDescending(r => r.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken))?.Release;
    }

    private static DateOnly? NextEffectiveDate(RefDataBuild build, DateOnly today)
        => build.Places.Values.SelectMany(p => p.Periods).SelectMany(p => new[] { p.From, p.To })
            .Where(d => d is { } day && day > today).Min();

    private sealed class StoredHash
    {
        public string Id { get; set; } = string.Empty;
        public string? Hash { get; set; }
    }

    private sealed class StoredStamp
    {
        public string Id { get; set; } = string.Empty;
        public string? Hash { get; set; }
        public ReleaseStamp? Release { get; set; }
    }
}
