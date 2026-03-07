using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Abstractions.Builder;

public interface ISparkBuilder
{
    IServiceCollection Services { get; }
    IConfiguration? Configuration { get; }
    SparkModuleRegistry Registry { get; }
}
