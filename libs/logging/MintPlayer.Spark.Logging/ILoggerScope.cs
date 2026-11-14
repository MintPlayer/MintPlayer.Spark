using Microsoft.Extensions.Logging;

namespace MintPlayer.Spark.Logging;

/// <summary>
/// Opens log scopes. Everything logged through any <see cref="ILogger"/> in the same async flow while a scope is open is
/// also kept in that scope's <c>Log</c> document, written in one save when the scope is disposed.
/// <code>
/// await using (var scope = logScopes.CreateScope())
///     scope.Logger.LogInformation("Merging {Number}", number);   // console, Seq, … and the Log document
/// </code>
/// </summary>
public interface ILoggerScopeProvider
{
    /// <summary>A new log, <c>Logs/{guid}</c>.</summary>
    ILoggerScope CreateScope();

    /// <summary>
    /// The log <c>Logs/{key}</c>, created on the first save and appended to by every later scope with the same key.
    /// </summary>
    /// <param name="key">
    /// A feature prefix and an id, such as <c>commands/github/123/456</c>: letters, digits, <c>- _ . :</c> and at least
    /// one <c>/</c>, at most 200 characters. Anything else throws <see cref="ArgumentException"/>.
    /// </param>
    /// <param name="relatedPersistentObjectId">The record the log belongs to; it is readable exactly when that record is.</param>
    /// <param name="expiresAtUtc">When the log expires; null keeps what it has.</param>
    ILoggerScope CreateScope(string key, string? relatedPersistentObjectId = null, DateTime? expiresAtUtc = null);

    /// <summary>Writes one entry to a log in its own scope: the one-line form of <see cref="CreateScope(string, string?, DateTime?)"/>.</summary>
    Task LogAsync(string key, LogLevel level, string message, Exception? exception = null, string? relatedPersistentObjectId = null, DateTime? expiresAtUtc = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Opens log scopes whose <see cref="ILoggerScope.Logger"/> has the category <typeparamref name="TCategoryName"/>, as
/// <c>ILogger&lt;T&gt;</c> does. The non-generic provider's category is <c>MintPlayer.Spark.Logging.Log</c>.
/// </summary>
public interface ILoggerScopeProvider<out TCategoryName> : ILoggerScopeProvider;

/// <summary>An open log scope. Dispose it to close the scope and save its entries.</summary>
public interface ILoggerScope : IAsyncDisposable, IDisposable
{
    /// <summary>The <c>Log</c> document's id.</summary>
    string Id { get; }

    /// <summary>The application's own <see cref="ILogger"/>: one call reaches every configured sink and this log.</summary>
    ILogger Logger { get; }
}
