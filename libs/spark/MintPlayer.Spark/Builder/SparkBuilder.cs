using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Configuration;

namespace MintPlayer.Spark;

public partial class SparkBuilder : ISparkBuilder
{
    [Inject] public IServiceCollection Services { get; }
    [Inject] public IConfiguration? Configuration { get; }
    public SparkModuleRegistry Registry { get; } = new();
    public SparkOptions Options { get; } = new();
}
