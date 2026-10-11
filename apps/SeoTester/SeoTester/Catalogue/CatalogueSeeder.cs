using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using SeoTester.Entities;

namespace SeoTester.Catalogue;

/// <summary>
/// Seeds the check catalogue from the shipped <c>checks.json</c> once the host has started (<c>docs/seo_analyzer_PRD.md</c>
/// §3.1, D32), the settings package's seeder pattern:
/// <list type="bullet">
/// <item>a shipped check with no document is added;</item>
/// <item>a document nobody edited since it was seeded (its content still hashes to <see cref="CheckDefinition.SeededHash"/>)
/// follows a changed shipped text;</item>
/// <item>an administrator's edit, or an entry an administrator created, is never touched;</item>
/// <item>a document whose check is no longer shipped is left alone (an entry without an implementation shows as unavailable).</item>
/// </list>
/// Idempotent and safe on several instances: writes are optimistic, so an administrator's save in between makes the
/// pass retry from the fresh document instead of overwriting it.
/// </summary>
internal sealed class CatalogueSeeder(IDocumentStore store, ILogger<CatalogueSeeder> logger) : IHostedLifecycleService
{
    private const int Attempts = 5;

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var (added, refreshed) = await SeedAsync(ShippedCatalogue.All, cancellationToken);
                if (added + refreshed > 0)
                    logger.LogInformation("Check catalogue: {Added} added, {Refreshed} refreshed from the shipped catalogue.", added, refreshed);
                return;
            }
            catch (ConcurrencyException) when (attempt < Attempts)
            {
                // An administrator saved an entry (or another instance seeded) in between; read again.
            }
        }
    }

    /// <summary>One pass over <paramref name="shipped"/>: the number of entries added and refreshed.</summary>
    internal async Task<(int Added, int Refreshed)> SeedAsync(IReadOnlyList<ShippedCheck> shipped, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;

        int added = 0, refreshed = 0;
        foreach (var chunk in shipped.Chunk(128))
        {
            var existing = await session.LoadAsync<CheckDefinition>(chunk.Select(c => CheckDefinition.IdFor(c.Id)), cancellationToken);
            foreach (var check in chunk)
            {
                var id = CheckDefinition.IdFor(check.Id);
                if (existing.TryGetValue(id, out var definition) && definition is not null)
                {
                    if (!CheckDefinitionMapping.IsUnedited(definition) || definition.SeededHash == SeedHashOf(check))
                        continue;
                    CheckDefinitionMapping.Apply(check, definition);
                    refreshed++;
                }
                else
                {
                    var created = new CheckDefinition();
                    CheckDefinitionMapping.Apply(check, created);
                    // An empty change vector: the write fails if the document appeared in the meantime.
                    await session.StoreAsync(created, string.Empty, id, cancellationToken);
                    added++;
                }
            }
        }

        if (added + refreshed > 0)
            await session.SaveChangesAsync(cancellationToken);
        return (added, refreshed);
    }

    /// <summary>The hash a document seeded from <paramref name="check"/> carries.</summary>
    internal static string SeedHashOf(ShippedCheck check)
    {
        var probe = new CheckDefinition();
        CheckDefinitionMapping.Apply(check, probe);
        return probe.SeededHash!;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
