using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Configuration;

namespace MintPlayer.Spark;

public class SparkBuilder : ISparkBuilder
{
    public IServiceCollection Services { get; }
    public IConfiguration? Configuration { get; }
    public SparkModuleRegistry Registry { get; } = new();
    public SparkOptions Options { get; } = new();

    public SparkBuilder(IServiceCollection services, IConfiguration? configuration = null)
    {
        Services = services;
        Configuration = configuration;
    }
}
