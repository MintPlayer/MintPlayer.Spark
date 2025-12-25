using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Services;

public interface ISparkContextResolver
{
    SparkContext? ResolveContext(IAsyncDocumentSession session);
}

[Register(typeof(ISparkContextResolver), ServiceLifetime.Scoped, "AddSparkServices")]
internal partial class SparkContextResolver : ISparkContextResolver
{
    [Inject] private readonly IServiceProvider serviceProvider;

    public SparkContext? ResolveContext(IAsyncDocumentSession session)
    {
        // Try to resolve any registered SparkContext implementation
        var sparkContext = serviceProvider.GetService<SparkContext>();

        if (sparkContext != null)
        {
            sparkContext.Session = session;
        }

        return sparkContext;
    }
}
