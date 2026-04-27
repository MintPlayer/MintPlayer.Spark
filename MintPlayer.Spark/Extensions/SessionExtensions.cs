using Microsoft.Extensions.Logging;
using Raven.Client.Documents.Session;
using System.Runtime.CompilerServices;

namespace MintPlayer.Spark;

/// <summary>
/// Extension methods for RavenDB session types.
/// </summary>
public static class SessionExtensions
{
    /// <summary>
    /// Temporarily disables the per-session request budget for the duration of the returned
    /// scope. On dispose, the original <c>MaxNumberOfRequestsPerSession</c> is restored.
    /// If <paramref name="logger"/> is supplied and the scope performs more than
    /// <paramref name="expectedMaximumRequests"/> (default: the session's pre-scope max),
    /// a warning is logged.
    /// </summary>
    /// <remarks>
    /// Restoring on dispose is important for request-scoped sessions: a heavy custom query
    /// must not silently elevate the budget for the remainder of the request.
    /// </remarks>
    public static IDisposable IgnoreMaxRequests(
        this IAsyncDocumentSession session,
        int? expectedMaximumRequests = null,
        ILogger? logger = null,
        [CallerMemberName] string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new MaxRequestsScope(session.Advanced, expectedMaximumRequests, logger, scope);
    }

    /// <inheritdoc cref="IgnoreMaxRequests(IAsyncDocumentSession, int?, ILogger?, string?)"/>
    public static IDisposable IgnoreMaxRequests(
        this IDocumentSession session,
        int? expectedMaximumRequests = null,
        ILogger? logger = null,
        [CallerMemberName] string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new MaxRequestsScope(session.Advanced, expectedMaximumRequests, logger, scope);
    }

    private sealed class MaxRequestsScope : IDisposable
    {
        private readonly IAdvancedDocumentSessionOperations advanced;
        private readonly int originalMax;
        private readonly int baselineRequests;
        private readonly int allowedRequests;
        private readonly ILogger? logger;
        private readonly string? scope;
        private bool disposed;

        public MaxRequestsScope(
            IAdvancedDocumentSessionOperations advanced,
            int? expectedMaximumRequests,
            ILogger? logger,
            string? scope)
        {
            this.advanced = advanced;
            this.logger = logger;
            this.scope = scope;

            originalMax = advanced.MaxNumberOfRequestsPerSession;
            baselineRequests = advanced.NumberOfRequests;

            // If the caller supplied an expected ceiling, use it; otherwise fall back to the
            // session's prior limit. If that limit was already int.MaxValue (someone nested
            // IgnoreMaxRequests scopes, or a global override is in place) reach for a sane
            // warning floor of 30 — the Raven default — so the warning still has signal.
            allowedRequests = expectedMaximumRequests
                ?? (originalMax == int.MaxValue ? 30 : originalMax);

            advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            advanced.MaxNumberOfRequestsPerSession = originalMax;

            if (logger is null) return;

            var used = advanced.NumberOfRequests - baselineRequests;
            if (used > allowedRequests)
            {
                logger.LogWarning(
                    "[IgnoreMaxRequests] {Scope} performed {Used} requests, expected at most {Allowed}",
                    scope ?? "(unknown)",
                    used,
                    allowedRequests);
            }
        }
    }
}
