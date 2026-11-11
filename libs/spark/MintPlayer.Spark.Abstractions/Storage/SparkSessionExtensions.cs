using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions.Storage;
using System.Runtime.CompilerServices;

namespace MintPlayer.Spark;

/// <summary>
/// Extension methods for <see cref="ISparkSession"/>, for every store provider.
/// </summary>
public static class SparkSessionExtensions
{
    /// <summary>
    /// Temporarily disables the per-session request budget for the duration of the returned
    /// scope. On dispose, the original <see cref="ISparkSession.MaxNumberOfRequests"/> is restored.
    /// If <paramref name="logger"/> is supplied and the scope performs more than
    /// <paramref name="expectedMaximumRequests"/> (default: the session's pre-scope max),
    /// a warning is logged.
    /// </summary>
    /// <remarks>
    /// Restoring on dispose is important for request-scoped sessions: a heavy custom query
    /// must not silently elevate the budget for the remainder of the request.
    /// </remarks>
    public static IDisposable IgnoreMaxRequests(
        this ISparkSession session,
        int? expectedMaximumRequests = null,
        ILogger? logger = null,
        [CallerMemberName] string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new MaxRequestsScope(() => session.MaxNumberOfRequests, max => session.MaxNumberOfRequests = max, () => session.NumberOfRequests, expectedMaximumRequests, logger, scope);
    }

    /// <summary>The scope <c>IgnoreMaxRequests</c> returns, over any session's request counters; providers build their own overloads on it.</summary>
    public sealed class MaxRequestsScope : IDisposable
    {
        private readonly Action<int> setMax;
        private readonly Func<int> requests;
        private readonly int originalMax;
        private readonly int baselineRequests;
        private readonly int allowedRequests;
        private readonly ILogger? logger;
        private readonly string? scope;
        private bool disposed;

        public MaxRequestsScope(
            Func<int> getMax,
            Action<int> setMax,
            Func<int> requests,
            int? expectedMaximumRequests,
            ILogger? logger,
            string? scope)
        {
            this.setMax = setMax;
            this.requests = requests;
            this.logger = logger;
            this.scope = scope;

            originalMax = getMax();
            baselineRequests = requests();

            // If the caller supplied an expected ceiling, use it; otherwise fall back to the
            // session's prior limit. If that limit was already int.MaxValue (someone nested
            // IgnoreMaxRequests scopes, or a global override is in place) reach for a sane
            // warning floor of 30 — the Raven default — so the warning still has signal.
            allowedRequests = expectedMaximumRequests
                ?? (originalMax == int.MaxValue ? 30 : originalMax);

            setMax(int.MaxValue);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            setMax(originalMax);

            if (logger is null) return;

            var used = requests() - baselineRequests;
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
