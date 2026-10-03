using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Abstractions.Interceptors;

/// <summary>Typed access to what <see cref="SparkHookContext"/> carries as <see cref="object"/>.</summary>
public static class SparkHookContextExtensions
{
    /// <summary>The session the write commits through, or for a materialize the session the entity was loaded in.</summary>
    public static IAsyncDocumentSession GetSession(this SparkHookContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (IAsyncDocumentSession)context.Session;
    }
}
