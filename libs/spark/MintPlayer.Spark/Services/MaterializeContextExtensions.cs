using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Abstractions.Interceptors;

/// <summary>Typed access to what <see cref="MaterializeContext"/> carries as <see cref="object"/>.</summary>
public static class MaterializeContextExtensions
{
    /// <summary>The session <see cref="MaterializeContext.Entity"/> was loaded in (contributions F1).</summary>
    public static IAsyncDocumentSession GetSession(this MaterializeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (IAsyncDocumentSession)context.Session;
    }
}
