using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Logging.Models;

namespace MintPlayer.Spark.Logging.Services;

/// <summary>
/// The <c>Document</c> logging provider (#660 G7). It writes nothing itself: it adds each entry to the innermost open
/// log scope of the async flow, and the scope saves them when it closes. Outside a log scope it ignores the entry.
/// Filter it like any provider, under <c>Logging:Document:LogLevel</c>.
/// </summary>
[ProviderAlias("Document")]
internal sealed class DocumentLoggerProvider(TimeProvider time) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider? scopes;
    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new DocumentLogger(this);

    public void Dispose() { }

    /// <summary>The innermost log scope of the current flow. <c>ForEachScope</c> walks outer to inner, so the last one wins.</summary>
    private LogScopeState? Innermost()
    {
        var box = new Found();
        scopes?.ForEachScope(static (scope, found) =>
        {
            if (scope is LogScopeState state)
                found.Value = state;
        }, box);
        return box.Value;
    }

    private sealed class Found
    {
        public LogScopeState? Value;
    }

    private sealed class DocumentLogger(DocumentLoggerProvider provider) : ILogger
    {
        // Scopes go through the factory's external scope provider, which SetScopeProvider handed us.
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            if (provider.Innermost() is not { } target)
                return;

            target.TryAdd(new LogEntry
            {
                At = provider.UtcNow,
                Level = logLevel.ToString(),
                Text = formatter(state, exception),
                Exception = exception?.ToString(),
            });
        }
    }
}
