using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Abstractions.Interceptors;

/// <summary>Typed access to what <see cref="SparkInterceptorContext"/> carries as <see cref="object"/>.</summary>
public static class SparkInterceptorContextExtensions
{
    /// <summary>The session the write commits through, or for a materialize the session the entity was loaded in.</summary>
    public static IAsyncDocumentSession GetSession(this SparkInterceptorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (IAsyncDocumentSession)context.Session;
    }
}
