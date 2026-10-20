using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.ReferenceData.Api;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources;
using Raven.Client.Documents;
using Raven.Client.Documents.BulkInsert;
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

    /// <summary>The write pass's cursor: chunks of the build written so far (a resumed run skips them).</summary>
    public int ChunksWritten { get; set; }

    /// <summary>How many times the run resumed from its checkpoint after a restart or a failure.</summary>
    public int Resumed { get; set; }

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

/// <summary>The import's checkpoint (§4.5 step 5): the run, its work folder, the release it writes, and the write cursor.</summary>
internal sealed record RefImportCheckpoint(string Run, string Work, string? Release, int Chunk)
{
    public string Serialize() => JsonSerializer.Serialize(this);

    public static RefImportCheckpoint? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonSerializer.Deserialize<RefImportCheckpoint>(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Runs an import (PRD §4.5 steps 4–10) of every <b>enabled</b> source (<c>Spark:ReferenceData:Sources:{id}:Enabled</c>,
/// K7: the host chooses its countries; an instance may register many adapters and import a few): fetches each current
/// version, builds and derives the dataset through the ledgers, refuses it on a ledger conflict or an implausible
/// retirement, writes what changed, and records a release of the instance's whole state. Never inside a migration. A
/// rerun of the same versions writes nothing (RS2).
/// <para>
/// <b>Checkpointed</b> (<see cref="ICheckpointRecipient{TMessage}"/>): downloads land in a durable work folder with a
/// marker per source, and the write pass records its chunk cursor. A run that stops (a restart, a failure the queue
/// retries) resumes: the same run document, the files it already fetched, the same release id, and the chunks after
/// the cursor. The build itself is deterministic and is recomputed in memory.
/// </para>
/// </summary>
internal sealed class RefDataImportRecipient(
    IServiceProvider services,
    IDocumentStore store,
    IHttpClientFactory httpClients,
    IOptions<SparkReferenceDataOptions> options,
    IHostEnvironment environment,
    IMessageCheckpoint checkpoint,
    TimeProvider time,
    ILogger<RefDataImportRecipient> logger) : ICheckpointRecipient<RefDataImportMessage>
{
    public Task HandleAsync(RefDataImportMessage message, CancellationToken cancellationToken = default)
        => RunAsync(message, null, cancellationToken);

    public Task HandleAsync(RefDataImportMessage message, string checkpointText, CancellationToken cancellationToken = default)
        => RunAsync(message, RefImportCheckpoint.Parse(checkpointText), cancellationToken);

    private async Task RunAsync(RefDataImportMessage message, RefImportCheckpoint? resume, CancellationToken cancellationToken)
    {
        var builder = services.GetRequiredService<RefDatasetBuilder>();
        RefImportRun? run = null;
        if (resume is not null)
        {
            using var session = store.OpenAsyncSession();
            run = await session.LoadAsync<RefImportRun>(resume.Run, cancellationToken);
        }
        if (run is null)
        {
            resume = null;
            run = new RefImportRun
            {
                Id = $"refdata/importruns/{time.GetUtcNow():yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..48],
                Trigger = message.Trigger,
                StartedAtUtc = time.GetUtcNow(),
            };
        }
        else
        {
            run.Resumed++;
            run.Status = RefImportStatus.Running;
            run.Message = $"Resumed from the checkpoint after {run.ChunksWritten} written chunks.";
        }
        await SaveRunAsync(run, cancellationToken);

        var workRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.Import.WorkDirectory));
        var workName = resume?.Work ?? RefZip.SafeName(run.Id!.Replace('/', '-'), "run");
        var work = Directory.CreateDirectory(Path.Combine(workRoot, workName));
        var state = new RefImportCheckpoint(run.Id!, workName, resume?.Release, resume?.Chunk ?? 0);
        await checkpoint.SaveAsync(state.Serialize(), cancellationToken);

        var succeeded = false;
        try
        {
            var enabled = builder.Sources.Where(s => options.Value.Sources.TryGetValue(s.Id, out var o) && o.Enabled).ToList();
            if (enabled.Count == 0)
            {
                run.Status = RefImportStatus.NoChange;
                run.Message = "No source is enabled: set Spark:ReferenceData:Sources:{id}:Enabled for the sources this instance imports.";
                succeeded = true;
                return;
            }

            var http = httpClients.CreateClient(RefDownloads.HttpClientName);
            var inputs = new Dictionary<string, SourceInput>(StringComparer.Ordinal);
            run.Sources.Clear();
            foreach (var source in enabled)
            {
                var input = await FetchedAsync(source, work, cancellationToken);
                if (input is null)
                {
                    var version = await source.CheckAsync(null, http, cancellationToken);
                    if (version is null) continue;
                    var folder = Directory.CreateDirectory(Path.Combine(work.FullName, RefZip.SafeName(source.Id, "source")));
                    input = await source.FetchAsync(version, http, folder, cancellationToken);
                    await MarkFetchedAsync(input, folder, cancellationToken);
                }
                inputs[source.Id] = input;
                run.Sources.Add($"{source.Id}@{input.Version}");
            }
            await SaveRunAsync(run, cancellationToken);

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
                succeeded = true;
                return;
            }
            run.Warnings = [.. build.Warnings.Take(200)];

            var writer = new RefDataWriter(store, options.Value, environment, time, services.GetService<IReleaseSigner>());
            await writer.WriteAsync(build, run, state, async next =>
            {
                state = next;
                await checkpoint.SaveAsync(next.Serialize(), cancellationToken);
                await SaveRunAsync(run, cancellationToken);
            }, cancellationToken);

            if (build.Ledger.Added.Count > 0 && run.Status != RefImportStatus.Refused)
            {
                using var session = store.OpenAsyncSession();
                foreach (var entry in build.Ledger.Added)
                    await session.StoreAsync(new RefLedgerEntry { Identity = entry.Identity, DocumentId = entry.Id, Source = entry.Source },
                        "refdata/ledger/" + entry.Identity.Replace('/', '_'), cancellationToken);
                await session.SaveChangesAsync(cancellationToken);
            }
            services.GetService<RefDataCurrentRelease>()?.Invalidate();
            succeeded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Status = RefImportStatus.Failed;
            run.Message = ex.Message + " The run keeps its downloads and resumes from its checkpoint when the queue retries it.";
            logger.LogError(ex, "Reference-data import {Run} failed.", run.Id);
            throw;
        }
        finally
        {
            run.FinishedAtUtc = succeeded ? time.GetUtcNow() : null;
            await SaveRunAsync(run, CancellationToken.None);
            // Downloads are kept until the run succeeds or is refused: a resumed run reuses them.
            if (succeeded)
                try { work.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The input a stopped run already fetched for <paramref name="source"/>, from its marker; <see langword="null"/> when none.</summary>
    private static async Task<SourceInput?> FetchedAsync(IReferenceDataSource source, DirectoryInfo work, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(work.FullName, RefZip.SafeName(source.Id, "source"));
        var marker = Path.Combine(folder, FetchedMarker.FileName);
        if (!File.Exists(marker)) return null;
        var fetched = JsonSerializer.Deserialize<FetchedMarker>(await File.ReadAllTextAsync(marker, cancellationToken));
        if (fetched is null || fetched.Files.Any(f => !File.Exists(Path.Combine(folder, f)))) return null;
        return new SourceInput
        {
            SourceId = source.Id,
            Version = fetched.Version,
            Url = fetched.Url,
            Sha256 = fetched.Sha256,
            Files = [.. fetched.Files.Select(f => new SourceFile(f, () => File.OpenRead(Path.Combine(folder, f))))],
        };
    }

    /// <summary>Records a completed fetch, so a resumed run reuses it. Files outside the source's folder are not reused.</summary>
    private static async Task MarkFetchedAsync(SourceInput input, DirectoryInfo folder, CancellationToken cancellationToken)
    {
        var names = input.Files.Select(f => f.Name).Where(n => File.Exists(Path.Combine(folder.FullName, n))).ToList();
        if (names.Count != input.Files.Count) return;
        var marker = new FetchedMarker(input.Version, input.Url, input.Sha256, names);
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, FetchedMarker.FileName), JsonSerializer.Serialize(marker), cancellationToken);
    }

    private async Task SaveRunAsync(RefImportRun run, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(run, run.Id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    private sealed record FetchedMarker(string Version, string? Url, string? Sha256, List<string> Files)
    {
        public const string FileName = "fetched.refdata.json";
    }
}

/// <summary>
/// Writes a derived build (PRD §4.5 steps 7, 8 and 10):
/// <list type="bullet">
/// <item>a collection that is still empty is loaded through <b>BulkInsert</b> with explicit ids and
/// <c>SkipOverwriteIfUnchanged</c> (the first load); afterwards documents are written in chunks, and a document whose
/// hash matches the stored one is skipped;</item>
/// <item>a stored document of the build's countries that the build lacks is kept and counted (retire, never delete); a
/// build that would leave more than <see cref="ReferenceDataReleaseOptions.MaxRetiredShare"/> of a collection absent is
/// refused as a broken download;</item>
/// <item>a run that changed anything records a release of the instance's <b>whole</b> state (the build plus every stored
/// document of other countries and sources), with deltas from the last <see cref="ReferenceDataReleaseOptions.DeltaDepth"/>
/// releases and, when signing is on, <c>manifest.sig</c>; <c>refdata/state</c> is written last.</item>
/// </list>
/// </summary>
internal sealed class RefDataWriter(IDocumentStore store, SparkReferenceDataOptions options, IHostEnvironment environment, TimeProvider time, IReleaseSigner? signer)
{
    private const int ChunkSize = 256;

    public async Task WriteAsync(RefDataBuild build, RefImportRun run, RefImportCheckpoint state, Func<RefImportCheckpoint, Task> saveCheckpoint, CancellationToken cancellationToken)
    {
        var documents = build.Documents().ToList();
        var release = state.Release ?? await NextReleaseAsync(cancellationToken);
        run.Release = release;
        if (state.Release is null)
            await saveCheckpoint(state = state with { Release = release });

        // Step 8: the retire check, over the countries this build writes only (other countries are other sources' runs).
        var countries = documents.Select(ReleaseWriter.CountryOfDocument).ToHashSet(StringComparer.Ordinal);
        var counts = await ReleaseApplier.CollectionCountsAsync(store, cancellationToken);
        run.Absent = 0;
        using (var session = store.OpenAsyncSession())
        {
            foreach (var collection in ReleaseFormat.Collections)
            {
                if (counts.GetValueOrDefault(collection.Name) == 0) continue;
                var stored = (await session.Advanced.AsyncRawQuery<StoredStamp>($"from {MintPlayer.Spark.Abstractions.RqlIdentifier.Collection(collection.Name)} select id() as Id, Hash, Country")
                    .Take(int.MaxValue).ToListAsync(cancellationToken))
                    .Where(s => countries.Contains(RefIds.CountryOf(s.Id) ?? (s.Country is { Length: > 0 } c ? c.ToLowerInvariant() : "_")))
                    .ToList();
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

        // Step 7: first loads through BulkInsert, then chunked writes after the cursor.
        var changed = 0;
        var chunkIndex = 0;
        foreach (var group in documents.GroupBy(d => d.GetType()).OrderBy(g => ReleaseFormat.Collections.ToList().FindIndex(c => c.Type == g.Key)))
        {
            var collection = ReleaseFormat.CollectionOf(group.Key).Name;
            if (counts.GetValueOrDefault(collection) == 0)
            {
                if (chunkIndex++ < state.Chunk) continue;
                await using (var bulk = store.BulkInsert(new BulkInsertOptions { SkipOverwriteIfUnchanged = true }, cancellationToken))
                {
                    foreach (var document in group)
                    {
                        Stamp(document, new ReleaseStamp { Introduced = release, Changed = release });
                        await bulk.StoreAsync(document, ReleaseWriter.IdOf(document));
                        run.Added++;
                        changed++;
                    }
                }
                run.ChunksWritten = chunkIndex;
                await saveCheckpoint(state = state with { Chunk = chunkIndex });
                continue;
            }

            foreach (var chunk in group.Chunk(ChunkSize))
            {
                if (chunkIndex++ < state.Chunk) continue;
                using var session = store.OpenAsyncSession();
                session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
                var ids = chunk.Select(ReleaseWriter.IdOf).ToList();
                var existing = await session.Advanced.AsyncRawQuery<StoredStamp>(
                        $"from {MintPlayer.Spark.Abstractions.RqlIdentifier.Collection(collection)} where id() in ($ids) select id() as Id, Hash, Release")
                    .AddParameter("ids", ids).Take(int.MaxValue).ToListAsync(cancellationToken);
                var byId = existing.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
                foreach (var document in chunk)
                {
                    var id = ReleaseWriter.IdOf(document);
                    var hash = ReleaseWriter.HashOf(document);
                    var stamp = byId.TryGetValue(id, out var old) ? old : null;
                    if (stamp?.Hash == hash)
                    {
                        run.Unchanged++;
                        continue;
                    }
                    Stamp(document, new ReleaseStamp { Introduced = stamp?.Release?.Introduced ?? release, Changed = release });
                    await session.StoreAsync(document, id, cancellationToken);
                    changed++;
                    if (stamp is null) run.Added++; else run.Updated++;
                }
                await session.SaveChangesAsync(cancellationToken);
                run.ChunksWritten = chunkIndex;
                await saveCheckpoint(state = state with { Chunk = chunkIndex });
            }
        }

        // A resumed run counts the chunks it wrote before the stop through the documents stamped with its release.
        var whole = await StoredDocumentsAsync(cancellationToken);
        if (changed == 0 && !whole.Any(d => ChangedOf(d) == release))
        {
            run.Status = RefImportStatus.NoChange;
            return;
        }

        // Step 10: the release, the instance's whole state as canonical files, with deltas and an optional signature.
        var history = await ReleasesAsync(cancellationToken);
        var deltas = new List<(string From, IEnumerable<object> Documents)>();
        for (var i = 0; i < Math.Min(options.Releases.DeltaDepth, history.Count); i++)
        {
            var newer = history.Take(i).Select(r => r.Release).Append(release).ToHashSet(StringComparer.Ordinal);
            deltas.Add((history[i].Release, whole.Where(d => ChangedOf(d) is { } c && newer.Contains(c)).ToList()));
        }
        var manifest = new ReleaseManifest
        {
            Release = release,
            Previous = history.FirstOrDefault()?.Release,
            Builder = "MintPlayer.Spark.ReferenceData " + typeof(RefDataWriter).Assembly.GetName().Version,
            KeyVersion = RefSearchKeys.KeyVersion,
            Sources = [.. build.Sources.Values],
            Changes = whole.Where(d => ChangedOf(d) == release).GroupBy(ReleaseWriter.CountryOfDocument).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            NextEffectiveDate = NextEffectiveDate(whole.OfType<RefPlace>(), DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)),
        };
        var directory = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Releases.Directory, release));
        var manifestBytes = await ReleaseWriter.WriteAsync(new DirectoryReleaseFileStore(directory), manifest, whole, cancellationToken, deltas,
            options.Releases.Sign && signer is not null ? signer.Sign : null);

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

        // refdata/state last (§4.7): what /api/v1/releases/latest and every ETag now name.
        var digests = await ReleaseApplier.LocalDigestsAsync(store, ReleaseFormat.Collections.Select(c => c.Name), cancellationToken);
        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new RefDataState { Release = release, Digests = digests, AppliedAtUtc = time.GetUtcNow() }, RefDataState.DocumentId, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
        run.Status = RefImportStatus.Succeeded;
    }

    /// <summary>Every stored document of the release collections, streamed per collection (the release's whole state).</summary>
    private async Task<List<object>> StoredDocumentsAsync(CancellationToken cancellationToken)
    {
        var all = new List<object>();
        foreach (var collection in ReleaseFormat.Collections)
        {
            var method = typeof(RefDataWriter).GetMethod(nameof(StreamAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(collection.Type);
            await (Task)method.Invoke(this, [all, cancellationToken])!;
        }
        return all;
    }

    private async Task StreamAsync<T>(List<object> into, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        await using var stream = await session.Advanced.StreamAsync(session.Query<T>(), cancellationToken);
        while (await stream.MoveNextAsync())
            into.Add(stream.Current.Document!);
    }

    private static void Stamp(object document, ReleaseStamp stamp) => document.GetType().GetProperty("Release")!.SetValue(document, stamp);

    private static string? ChangedOf(object document) => (document.GetType().GetProperty("Release")?.GetValue(document) as ReleaseStamp)?.Changed;

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

    /// <summary>The recorded releases, newest first.</summary>
    private async Task<List<RefRelease>> ReleasesAsync(CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        return await session.Query<RefRelease>().OrderByDescending(r => r.CreatedAtUtc).Take(Math.Max(1, options.Releases.DeltaDepth)).ToListAsync(cancellationToken);
    }

    private static DateOnly? NextEffectiveDate(IEnumerable<RefPlace> places, DateOnly today)
        => places.SelectMany(p => p.Periods).SelectMany(p => new[] { p.From, p.To })
            .Where(d => d is { } day && day > today).Min();

    private sealed class StoredStamp
    {
        public string Id { get; set; } = string.Empty;
        public string? Hash { get; set; }
        public string? Country { get; set; }
        public ReleaseStamp? Release { get; set; }
    }
}
