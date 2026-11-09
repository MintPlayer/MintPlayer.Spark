using MintPlayer.Spark.Abstractions.Storage;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark;

/// <summary>Bridges RavenDB's session types onto Spark's storage contract.</summary>
public static class RavenSparkSessionExtensions
{
    /// <summary>
    /// <paramref name="session"/> as an <see cref="ISparkSession"/> over the same unit of work. Disposing the result
    /// leaves <paramref name="session"/> open; its owner disposes it.
    /// </summary>
    public static ISparkSession AsSparkSession(this IAsyncDocumentSession session)
        => new Storage.Raven.RavenSparkSession(session);

    /// <summary><paramref name="store"/> as an <see cref="ISparkStore"/>.</summary>
    public static ISparkStore AsSparkStore(this Raven.Client.Documents.IDocumentStore store)
        => new Storage.Raven.RavenSparkStore(store);

    /// <summary>The RavenDB session behind <paramref name="session"/>; throws when another provider serves it.</summary>
    public static IAsyncDocumentSession AsRavenSession(this ISparkSession session)
        => session.Native as IAsyncDocumentSession
            ?? throw new NotSupportedException(
                $"This needs a RavenDB session, but the store's provider is '{session.Native.GetType().Name}'.");
}
