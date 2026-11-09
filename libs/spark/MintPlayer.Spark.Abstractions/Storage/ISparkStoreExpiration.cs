namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// The store's half of document expiration (<c>docs/seo_analyzer_PRD.md</c> X1, #551 D18), the
/// <see cref="SparkStoreCapabilities.Expiration"/> capability. Core checks the model and decides whether the database
/// needs expiring; the provider stamps what it stores and runs the sweep.
/// </summary>
/// <remarks>
/// Every Spark read hides an expired row whether or not the store ever deletes it (the expiration row policy), so a
/// provider without this capability still never shows one: its expired rows only stay on disk.
/// </remarks>
public interface ISparkStoreExpiration
{
    /// <summary>
    /// Mirrors <see cref="IExpiring.ExpiresAtUtc"/> into the store's own expiry on every store of an
    /// <see cref="IExpiring"/> entity. A new document stored without a value gets <c>now + defaultExpiresAfter(type)</c>
    /// when that returns one. Dispose to remove the hook: the store can outlive the host that installed it.
    /// </summary>
    IDisposable InstallStamper(Func<Type, TimeSpan?> defaultExpiresAfter, TimeProvider time);

    /// <summary>Turns the store's sweep of expired documents on, every <paramref name="deleteFrequency"/>.</summary>
    void EnableSweep(TimeSpan deleteFrequency);
}
