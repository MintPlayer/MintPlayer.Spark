using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Storage;

namespace MintPlayer.Spark.Services;

public interface ISparkContextResolver
{
    SparkContext? ResolveContext(ISparkSession session);
}

[Register(typeof(ISparkContextResolver), ServiceLifetime.Scoped)]
internal partial class SparkContextResolver : ISparkContextResolver
{
    [Inject] private readonly IServiceProvider serviceProvider;

    public SparkContext? ResolveContext(ISparkSession session)
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
