using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Settings.Models;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Settings.Services;

/// <summary>
/// Writes one <c>Settings/{Key}</c> document per declared setting once the host has started
/// (<c>docs/seo_analyzer_PRD.md</c> X9), so the administrator finds every setting in the list without
/// typing a key. It refreshes <see cref="Setting.Type"/>, <see cref="Setting.Default"/> and
/// <see cref="Setting.Description"/> from the code and <b>never touches <see cref="Setting.Value"/></b>.
/// </summary>
/// <remarks>
/// <para>
/// <c>StartedAsync</c>, not <c>StartAsync</c>: the request pipeline (<c>UseSpark()</c>, which prepares the
/// database) is built by the web host's own hosted service, which starts after this one.
/// </para>
/// <para>
/// Idempotent and safe on several instances: the write is optimistic, so an administrator's save between
/// this read and this write makes the write fail and retry from the fresh document instead of
/// overwriting the value. A document whose key is no longer declared is left alone; reads ignore it.
/// </para>
/// </remarks>
internal sealed class SettingsSeeder(
    IDocumentStore store,
    SparkSettingsCatalog catalog,
    ISparkSettings settings,
    ILogger<SettingsSeeder> logger) : IHostedLifecycleService
{
    private const int Attempts = 5;

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        if (catalog.All.Count == 0)
            return;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var written = await SeedAsync(cancellationToken);
                if (written > 0)
                {
                    logger.LogInformation("Seeded or refreshed {Count} setting document(s).", written);
                    settings.Invalidate();
                }
                return;
            }
            catch (ConcurrencyException) when (attempt < Attempts)
            {
                // Someone saved a setting (or another instance seeded) in between; read again.
            }
        }
    }

    /// <summary>One pass; the number of documents created or refreshed.</summary>
    internal async Task<int> SeedAsync(CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;

        var declared = catalog.All.ToList();
        var existing = await session.LoadAsync<Setting>(declared.Select(s => Setting.IdFor(s.Key)), cancellationToken);
        var written = 0;
        foreach (var declaration in declared)
        {
            var id = Setting.IdFor(declaration.Key);
            var type = SparkSettingValues.TypeName(declaration.ValueType);
            if (existing.TryGetValue(id, out var setting) && setting is not null)
            {
                if (setting.Key == declaration.Key && setting.Type == type && setting.Default == declaration.DefaultText && setting.Description == declaration.Description)
                    continue;
                setting.Key = declaration.Key;
                setting.Type = type;
                setting.Default = declaration.DefaultText;
                setting.Description = declaration.Description;
            }
            else
            {
                // An empty change vector: the write fails if the document appeared in the meantime.
                await session.StoreAsync(new Setting
                {
                    Id = id,
                    Key = declaration.Key,
                    Type = type,
                    Default = declaration.DefaultText,
                    Description = declaration.Description,
                }, string.Empty, id, cancellationToken);
            }
            written++;
        }

        if (written > 0)
            await session.SaveChangesAsync(cancellationToken);
        return written;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
