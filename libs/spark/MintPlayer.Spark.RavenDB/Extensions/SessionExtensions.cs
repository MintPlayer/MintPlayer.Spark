using Microsoft.Extensions.Logging;
using Raven.Client.Documents.Session;
using System.Runtime.CompilerServices;

namespace MintPlayer.Spark;

/// <summary>
/// <see cref="SparkSessionExtensions.IgnoreMaxRequests"/> for code that still holds a RavenDB session.
/// </summary>
public static class SessionExtensions
{
    /// <inheritdoc cref="SparkSessionExtensions.IgnoreMaxRequests"/>
    public static IDisposable IgnoreMaxRequests(
        this IAsyncDocumentSession session,
        int? expectedMaximumRequests = null,
        ILogger? logger = null,
        [CallerMemberName] string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var advanced = session.Advanced;
        return new SparkSessionExtensions.MaxRequestsScope(() => advanced.MaxNumberOfRequestsPerSession, max => advanced.MaxNumberOfRequestsPerSession = max, () => advanced.NumberOfRequests, expectedMaximumRequests, logger, scope);
    }

    /// <inheritdoc cref="SparkSessionExtensions.IgnoreMaxRequests"/>
    public static IDisposable IgnoreMaxRequests(
        this IDocumentSession session,
        int? expectedMaximumRequests = null,
        ILogger? logger = null,
        [CallerMemberName] string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var advanced = session.Advanced;
        return new SparkSessionExtensions.MaxRequestsScope(() => advanced.MaxNumberOfRequestsPerSession, max => advanced.MaxNumberOfRequestsPerSession = max, () => advanced.NumberOfRequests, expectedMaximumRequests, logger, scope);
    }
}
