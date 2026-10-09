using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// A Spark builder for tests that register Identity through the internal
/// <c>services.AddSparkAuthentication&lt;SparkUser&gt;()</c> rather than <c>spark.AddAuthentication</c>:
/// it records the user type the way <c>AddAuthentication</c> does, so the provider presets accept it.
/// </summary>
internal static class TestSparkAuth
{
    internal static ISparkBuilder Builder(IServiceCollection services, IConfiguration? configuration = null)
    {
        var spark = new SparkBuilder(services, configuration);
        spark.Registry.IdentityUserType = typeof(SparkUser);
        return spark;
    }
}
