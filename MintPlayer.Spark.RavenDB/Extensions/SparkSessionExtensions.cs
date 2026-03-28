using MintPlayer.Spark.Storage;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.RavenDB.Extensions;

/// <summary>
/// Extension methods for accessing RavenDB-specific functionality from an <see cref="ISparkSession"/>.
/// Use these when you need full RavenDB power (type-safe index queries, advanced session operations).
/// </summary>
public static class SparkSessionExtensions
{
    /// <summary>
    /// Queries using a RavenDB index with compile-time type safety.
    /// Returns <see cref="IRavenQueryable{T}"/> for full RavenDB LINQ support.
    /// </summary>
    public static IRavenQueryable<T> RavenQuery<T, TIndex>(this ISparkSession session)
        where T : class
        where TIndex : AbstractIndexCreationTask, new()
    {
        var ravenSession = GetRavenSession(session);
        return ravenSession.Query<T, TIndex>();
    }

    /// <summary>
    /// Gets the underlying RavenDB <see cref="IAsyncDocumentSession"/> for advanced operations.
    /// </summary>
    public static IAsyncDocumentSession GetRavenSession(this ISparkSession session)
    {
        if (session is RavenDbSparkSession ravenSession)
        {
            return ravenSession.InnerSession;
        }

        throw new InvalidOperationException(
            $"Cannot get RavenDB session from {session.GetType().Name}. " +
            "This extension method requires a RavenDB-backed ISparkSession.");
    }
}
