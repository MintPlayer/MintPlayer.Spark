using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Files.Staging;
using MintPlayer.Spark.Files.Storage;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Files.Scanning;

/// <summary>
/// Deferred scanning (D6): a commit leaves the upload <c>Committed</c> and <c>Pending</c> and queues its token here; the
/// worker scans it and records the verdict. A save can claim the token only once it is <c>Clean</c>, so nothing is served
/// unscanned. A periodic sweep picks up what a restart dropped from the queue and retries <c>ScanFailed</c>.
/// </summary>
[Register(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
internal sealed partial class DeferredFileScanner
{
    [Inject] private readonly FileScanRunner runner;
    [Inject] private readonly ISparkStore store;
    [Inject] private readonly IFileStore fileStore;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly ILogger<DeferredFileScanner> logger;

    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<string> queued = new(StringComparer.Ordinal);

    internal ChannelReader<string> Reader => queue.Reader;

    /// <summary>Queues a token once: a token already waiting is not queued twice.</summary>
    public void Enqueue(string token)
    {
        lock (queued)
        {
            if (!queued.Add(token))
                return;
        }
        queue.Writer.TryWrite(token);
    }

    /// <summary>Scans one queued upload, if it still waits for a verdict. Returns the status it ends with (null: nothing to do).</summary>
    public async Task<SparkFileScanStatus?> ScanAsync(string token, CancellationToken cancellationToken)
    {
        lock (queued)
            queued.Remove(token);
        if (runner.Scanner is not { } scanner)
            return null;

        using var session = store.OpenSession();
        session.OptimisticConcurrency = true;
        var upload = await session.LoadAsync<SparkFileUpload>(SparkFileUpload.IdFor(token).Value, cancellationToken);
        if (upload is null || !WaitsForScan(upload) || IsExpired(upload))
            return null;

        bool delete;
        var entityType = modelLoader.GetEntityType(upload.EntityTypeId);
        var attribute = entityType is null ? null : Services.SparkFileAttributePath.Resolve(modelLoader, entityType, upload.Attribute)?.Attribute;
        if (entityType is null || attribute is null)
        {
            upload.State = SparkFileUploadState.Refused;
            upload.Error = "The attribute no longer exists.";
            delete = true;
        }
        else
        {
            var context = new FileScanContext(entityType, attribute, upload.UserId, upload.FileName,
                upload.ContentType ?? "application/octet-stream", upload.Length, upload.Sha256 ?? string.Empty);
            var result = await runner.ScanAsync(scanner, upload, context, cancellationToken);
            delete = runner.Apply(upload, result);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (SparkStoreConcurrencyException)
        {
            // Someone else moved the upload on (another worker, an expiry); their state stands.
            return null;
        }
        if (delete)
            foreach (var key in fileStore.StagedKeys(upload))
                await fileStore.DeleteAsync(key, CancellationToken.None);
        return upload.ScanStatus;
    }

    /// <summary>Queues every upload that still waits for a verdict. Streams the staging collection: a collection scan, no index.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        using var session = store.OpenSession();
        await foreach (var row in session.StreamAsync(session.Query<SparkFileUpload>(), cancellationToken))
        {
            var upload = row.Document;
            if (upload is not null && WaitsForScan(upload) && !IsExpired(upload))
            {
                Enqueue(upload.Token);
                count++;
            }
        }
        if (count > 0)
            logger.LogInformation("The deferred scan sweep queued {Count} upload(s).", count);
        return count;
    }

    internal static bool WaitsForScan(SparkFileUpload upload)
        => upload.State == SparkFileUploadState.Committed && upload.ScanStatus is SparkFileScanStatus.Pending or SparkFileScanStatus.ScanFailed;

    private bool IsExpired(SparkFileUpload upload) => upload.ExpiresAtUtc is { } expires && expires <= clock.GetUtcNow().UtcDateTime;
}

/// <summary>Drains <see cref="DeferredFileScanner"/>'s queue and sweeps every <see cref="SparkFilesOptions.ScanSweepInterval"/>; idles unless deferred with a scanner.</summary>
internal sealed partial class DeferredFileScanService : BackgroundService
{
    [Inject] private readonly DeferredFileScanner scanner;
    [Inject] private readonly FileScanRunner runner;
    [Inject] private readonly IOptions<SparkFilesOptions> options;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly ILogger<DeferredFileScanService> logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.ScanMode != SparkFileScanMode.Deferred || runner.Scanner is null)
            return Task.CompletedTask;
        return Task.WhenAll(DrainAsync(stoppingToken), SweepAsync(stoppingToken));
    }

    private async Task DrainAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var token in scanner.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await scanner.ScanAsync(token, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The deferred scan of upload {Token} failed; the sweep retries it.", token);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(options.Value.ScanSweepInterval, clock);
            do
            {
                try
                {
                    await scanner.SweepAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The deferred scan sweep failed; it retries next interval.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
