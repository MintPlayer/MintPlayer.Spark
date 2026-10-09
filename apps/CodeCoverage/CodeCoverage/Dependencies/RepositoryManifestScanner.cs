using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Dependencies;

public interface IRepositoryManifestScanner
{
    /// <summary>
    /// Re-reads the manifests of <paramref name="repositoryId"/>'s default branch into its
    /// <see cref="RepositoryManifest"/> and saves. Never throws for a forge or parse failure: the
    /// failure is recorded as <see cref="RepositoryManifest.ScanError"/> instead.
    /// </summary>
    Task<EManifestScanOutcome> ScanAsync(string repositoryId, CancellationToken cancellationToken = default);
}

/// <summary>What one scan did, for logs and tests.</summary>
public enum EManifestScanOutcome
{
    /// <summary>Unknown, disconnected or archived repository, or no default branch: nothing to read.</summary>
    Skipped,

    /// <summary>The tree had not changed since the last successful scan; nothing was fetched or written.</summary>
    Unchanged,

    /// <summary>Manifests were read and saved.</summary>
    Scanned,

    /// <summary>The scan could not complete; the reason is on the manifest.</summary>
    Failed,
}

/// <summary>
/// Reads one repository's manifests: list the default branch's tree, keep the manifest-like paths,
/// fetch those files at the listed commit, parse, save (dependency-updates PRD §6.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded so it finishes well inside the messaging claim (5 minutes).</b> At most
/// <see cref="MaxManifestFiles"/> files, none above <see cref="MaxFileBytes"/>, at most
/// <see cref="MaxParallelFetches"/> in flight, and a hard <see cref="ScanBudget"/> after which the
/// scan records a timeout instead of running into a second claim.
/// </para>
/// <para>
/// <b>An unchanged tree stops after the listing</b>, without fetching a file or writing the
/// document — which is what makes the nightly scan of every repository cheap. The tree sha is only
/// stored after a scan that read every file, so a partial read is retried by the next trigger
/// rather than remembered as complete.
/// </para>
/// </remarks>
[Register(typeof(IRepositoryManifestScanner), ServiceLifetime.Scoped)]
public partial class RepositoryManifestScanner : IRepositoryManifestScanner
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver forges;
    [Inject] private readonly ILogger<RepositoryManifestScanner> logger;

    public const int MaxManifestFiles = 400;
    public const long MaxFileBytes = 512 * 1024;
    public const int MaxParallelFetches = 4;
    public static readonly TimeSpan ScanBudget = TimeSpan.FromMinutes(3);

    public async Task<EManifestScanOutcome> ScanAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        var repository = await session.LoadAsync<Repository>(repositoryId, cancellationToken);
        if (repository is null
            || repository.Connection == RepositoryConnection.Disconnected
            || repository.Archived
            || string.IsNullOrEmpty(repository.DefaultBranch))
        {
            logger.LogDebug("Manifest scan of {RepositoryId} skipped: unknown, disconnected, archived or without a default branch", repositoryId);
            return EManifestScanOutcome.Skipped;
        }

        var manifestId = RepositoryManifest.ForRepository(repository.Id!);
        var manifest = await session.LoadAsync<RepositoryManifest>(manifestId, cancellationToken);
        if (manifest is null)
        {
            manifest = new RepositoryManifest { Repository = repository.Id };
            await session.StoreAsync(manifest, manifestId, cancellationToken);
        }
        manifest.OwnerKey = repository.OwnerKey;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ScanBudget);

        EManifestScanOutcome outcome;
        try
        {
            outcome = await ScanTreeAsync(repository, manifest, budget.Token);
            if (outcome == EManifestScanOutcome.Unchanged) return outcome;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Fail(manifest, $"The scan did not finish within {ScanBudget.TotalMinutes:0} minutes.");
            outcome = EManifestScanOutcome.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One repository's failure is recorded on its own document and goes no further: a throw
            // here would make the messaging host retry a scan that will fail the same way, on a
            // queue every pull-request comment shares.
            logger.LogWarning(ex, "Manifest scan of {FullName} failed", repository.FullName);
            Fail(manifest, "The scan failed unexpectedly.");
            outcome = EManifestScanOutcome.Failed;
        }

        manifest.ScannedAt = DateTime.UtcNow;
        await session.SaveChangesAsync(cancellationToken);
        return outcome;
    }

    private async Task<EManifestScanOutcome> ScanTreeAsync(Repository repository, RepositoryManifest manifest, CancellationToken cancellationToken)
    {
        var forge = forges.For(repository);
        var tree = await forge.GetTreeAsync(repository, repository.DefaultBranch!, cancellationToken);
        if (tree is null)
        {
            Fail(manifest, "The repository's files could not be listed. Is the app still installed on it?");
            return EManifestScanOutcome.Failed;
        }

        if (tree.TreeSha == manifest.TreeSha
            && manifest.ScanError is null
            && manifest.ParserVersion == RepositoryManifest.CurrentParserVersion)
            return EManifestScanOutcome.Unchanged;

        var candidates = tree.Entries
            .Where(e => e.Size <= MaxFileBytes && ManifestFiles.IsManifest(e.Path))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();
        var truncated = tree.Truncated || candidates.Count > MaxManifestFiles;
        if (candidates.Count > MaxManifestFiles) candidates = candidates.Take(MaxManifestFiles).ToList();

        // ⚠️ The forge client resolved (and memoized) its credential through this scope's session
        // while listing the tree, so the parallel reads below do not touch the session — which is
        // not thread-safe. Nothing else in this loop may use it.
        var contents = new string?[candidates.Count];
        using (var throttle = new SemaphoreSlim(MaxParallelFetches))
        {
            await Task.WhenAll(candidates.Select(async (entry, index) =>
            {
                await throttle.WaitAsync(cancellationToken);
                try
                {
                    contents[index] = await forge.GetFileContentAsync(repository, tree.CommitSha, entry.Path, cancellationToken);
                }
                finally
                {
                    throttle.Release();
                }
            }));
        }

        var produces = new List<ManifestPackage>();
        var consumes = new List<ManifestDependency>();
        var parseErrors = new List<string>();
        var unreadable = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (contents[i] is not { } content)
            {
                unreadable++;
                continue;
            }

            var result = ManifestFiles.Parse(candidates[i].Path, content);
            produces.AddRange(result.Produces);
            consumes.AddRange(result.Consumes);
            if (result.Error is not null) parseErrors.Add($"{candidates[i].Path}: {result.Error}");
        }

        manifest.Produces = produces;
        manifest.Consumes = consumes;
        manifest.ParseErrors = parseErrors;
        manifest.Truncated = truncated;
        manifest.ParserVersion = RepositoryManifest.CurrentParserVersion;

        if (unreadable > 0)
        {
            // Keep what was read, but do not remember the tree: the next trigger reads it again.
            manifest.TreeSha = null;
            manifest.ScanError = $"{unreadable} of {candidates.Count} manifest files could not be read.";
            return EManifestScanOutcome.Failed;
        }

        manifest.TreeSha = tree.TreeSha;
        manifest.ScanError = null;
        logger.LogInformation("Scanned {Count} manifests of {FullName}: {Produces} produced, {Consumes} consumed",
            candidates.Count, repository.FullName, produces.Count, consumes.Count);
        return EManifestScanOutcome.Scanned;
    }

    private static void Fail(RepositoryManifest manifest, string reason)
    {
        manifest.ScanError = reason;
        manifest.TreeSha = null;
    }
}
