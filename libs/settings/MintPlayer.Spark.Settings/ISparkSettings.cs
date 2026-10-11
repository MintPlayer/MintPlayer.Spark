namespace MintPlayer.Spark.Settings;

/// <summary>
/// Reads declared settings (<c>docs/seo_analyzer_PRD.md</c> X9): the administrator's stored value, else
/// <c>IConfiguration[Key]</c>, else the code default. Stored values come from a snapshot of every
/// <c>Setting</c> document, refreshed after <see cref="SparkSettingsOptions.CacheDuration"/> (30 s) and at
/// once on the instance that saved a change; another instance sees it within the cache duration.
/// </summary>
/// <remarks>
/// A read never throws for bad data: a stored or configured value that no longer parses as the declared
/// type (the type changed in code) is skipped with a warning and the next source applies.
/// </remarks>
public interface ISparkSettings
{
    /// <summary>The current value of <paramref name="setting"/>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="setting"/> was not declared through <c>AddSettings</c>.</exception>
    ValueTask<T> GetAsync<T>(SparkSetting<T> setting, CancellationToken cancellationToken = default);

    /// <summary>Drops the snapshot, so the next read loads the stored values again.</summary>
    void Invalidate();
}
