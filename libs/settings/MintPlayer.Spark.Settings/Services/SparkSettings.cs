using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Settings.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Settings.Services;

/// <summary>
/// <see cref="ISparkSettings"/>: stored value, else configuration, else default, over a snapshot of
/// the <c>Settings</c> collection loaded by id prefix (no index, so a value saved a moment ago is never
/// missed because an index is stale).
/// </summary>
/// <remarks>
/// A plain service rather than an <c>IConfigurationProvider</c> (research X9 §2): a provider would let a
/// settings administrator override any configuration key (connection strings, OAuth secrets) and would
/// read configuration-only values until the store existed. Only declared keys are read here.
/// </remarks>
internal sealed class SparkSettings(
    IDocumentStore store,
    SparkSettingsCatalog catalog,
    IOptions<SparkSettingsOptions> options,
    ILogger<SparkSettings> logger,
    IConfiguration? configuration = null,
    TimeProvider? timeProvider = null) : ISparkSettings
{
    private const int PageSize = 1024;
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private volatile Snapshot? snapshot;

    private sealed record Snapshot(DateTimeOffset LoadedAt, IReadOnlyDictionary<string, string> Values);

    public async ValueTask<T> GetAsync<T>(SparkSetting<T> setting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (catalog.Find(setting.Key) is null)
            throw new InvalidOperationException(
                $"The setting '{setting.Key}' is not declared. Add the class that declares it with spark.AddSettings(typeof(…)).");

        var values = (await CurrentAsync(cancellationToken)).Values;
        if (values.TryGetValue(setting.Key, out var stored))
        {
            if (SparkSettingValues.TryParse<T>(stored, out var value))
                return value;
            logger.LogWarning("The stored value of setting {Key} does not parse as {Type}; configuration or the default applies.", setting.Key, typeof(T).Name);
        }

        if (configuration?[setting.Key] is { } configured)
        {
            if (SparkSettingValues.TryParse<T>(configured, out var value))
                return value;
            logger.LogWarning("The configured value of setting {Key} does not parse as {Type}; the default applies.", setting.Key, typeof(T).Name);
        }

        return setting.Default;
    }

    public void Invalidate() => snapshot = null;

    private async ValueTask<Snapshot> CurrentAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (snapshot is { } current && now - current.LoadedAt < options.Value.CacheDuration)
            return current;

        // No lock: two concurrent loads read the same documents, and the later one wins harmlessly.
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var session = store.OpenAsyncSession();
        for (var start = 0; ; start += PageSize)
        {
            var page = await session.Advanced.LoadStartingWithAsync<Setting>(Setting.IdFor(string.Empty), start: start, pageSize: PageSize, token: cancellationToken);
            var count = 0;
            foreach (var setting in page)
            {
                count++;
                // Empty means "not overridden", the same as no document.
                if (!string.IsNullOrWhiteSpace(setting.Value) && !string.IsNullOrEmpty(setting.Key))
                    values[setting.Key] = setting.Value;
            }
            if (count < PageSize)
                break;
        }

        var loaded = new Snapshot(now, values);
        snapshot = loaded;
        return loaded;
    }
}
