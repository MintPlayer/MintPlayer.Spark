using Microsoft.Extensions.Logging;

namespace MintPlayer.Spark.Logging.Services;

internal class LoggerScopeProvider(ILoggerFactory loggerFactory, LogWriter writer, ILogger<LogWriter> writerLogger, string categoryName)
    : ILoggerScopeProvider
{
    /// <summary>The category of the non-generic provider's loggers.</summary>
    internal const string DefaultCategory = "MintPlayer.Spark.Logging.Log";

    public ILoggerScope CreateScope() => Open(LogIds.New(), null, null);

    public ILoggerScope CreateScope(string key, string? relatedPersistentObjectId = null, DateTime? expiresAtUtc = null)
        => Open(LogIds.ForKey(key), relatedPersistentObjectId, expiresAtUtc);

    public async Task LogAsync(string key, LogLevel level, string message, Exception? exception = null, string? relatedPersistentObjectId = null, DateTime? expiresAtUtc = null, CancellationToken cancellationToken = default)
    {
        await using var scope = CreateScope(key, relatedPersistentObjectId, expiresAtUtc);
#pragma warning disable CA2254 // the message is the caller's text, not a template
        scope.Logger.Log(level, exception, "{Message}", message);
#pragma warning restore CA2254
    }

    private LoggerScope Open(string id, string? relatedPersistentObjectId, DateTime? expiresAtUtc)
    {
        var logger = loggerFactory.CreateLogger(categoryName);
        var state = new LogScopeState(id, relatedPersistentObjectId, expiresAtUtc);
        return new LoggerScope(state, logger.BeginScope(state), logger, writer, writerLogger);
    }
}

internal sealed class LoggerScopeProvider<TCategoryName>(ILoggerFactory loggerFactory, LogWriter writer, ILogger<LogWriter> writerLogger)
    : LoggerScopeProvider(loggerFactory, writer, writerLogger, (typeof(TCategoryName).FullName ?? typeof(TCategoryName).Name).Replace('+', '.')),
      ILoggerScopeProvider<TCategoryName>;

internal sealed class LoggerScope(LogScopeState state, IDisposable? handle, ILogger logger, LogWriter writer, ILogger<LogWriter> writerLogger)
    : ILoggerScope
{
    private int disposed;

    public string Id => state.Id;

    public ILogger Logger => logger;

    /// <remarks>
    /// ⚠️ Deliberately not <c>async</c>: the pop restores the caller's AsyncLocal scope stack, and a change made inside
    /// an async method is undone when it returns. Popping in there left the closed scope current for the caller, so
    /// the outer scope's next entries were dropped (#660 S2, <c>An_entry_goes_to_the_innermost_scope_only</c>).
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return ValueTask.CompletedTask;

        // Pop first: whatever is logged from here on, the save's own trouble included, is not this log's.
        handle?.Dispose();
        var entries = state.Close();
        return entries.Count == 0 ? ValueTask.CompletedTask : SaveAsync(entries);
    }

    private async ValueTask SaveAsync(IReadOnlyList<Models.LogEntry> entries)
    {
        try
        {
            await writer.AppendAsync(state, entries);
        }
        catch (Exception ex)
        {
            // A log that cannot be saved must not fail the work it describes.
            writerLogger.LogError(ex, "Could not save {Count} entries to log {LogId}", entries.Count, state.Id);
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
