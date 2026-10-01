using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Contributions;

/// <summary>Registration for contributions.</summary>
public static class SparkContributionsExtensions
{
    /// <summary>
    /// Enables per-user contributions for every <see cref="ContributionAttribute"/> property in the app.
    /// </summary>
    /// <remarks>
    /// ⚠️ Preview skeleton (contributions M3): this registers <see cref="IContributions"/> only, and
    /// its operations throw <see cref="NotSupportedException"/>. The interceptor, the generated-type
    /// registration and the rebuild land in M5. Grant <c>RevertContribution/T</c> per generated
    /// contribution type in <c>security.json</c> (see <see cref="ContributionRights"/>).
    /// </remarks>
    public static ISparkBuilder AddContributions(this ISparkBuilder builder)
    {
        builder.Services.TryAddScoped<IContributions, SparkContributions>();
        return builder;
    }
}

/// <summary>Placeholder until the M5 runtime: refuses loudly rather than reporting a rebuild it did not do.</summary>
internal sealed class SparkContributions : IContributions
{
    public Task RebuildCurrentAsync(string targetId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("MintPlayer.Spark.Contributions is a preview skeleton: RebuildCurrentAsync lands with the runtime (contributions M5).");
}
