using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Extensions;

namespace MintPlayer.Spark.Contributions;

/// <summary>Registration for contributions.</summary>
public static class SparkContributionsExtensions
{
    /// <summary>
    /// Enables per-user contributions for every <see cref="ContributionAttribute"/> property in the app:
    /// the interceptor that hydrates, diffs and writes them, <see cref="IContributions"/>, and the
    /// startup shape check that rebuilds current documents written in an older shape.
    /// </summary>
    /// <param name="builder">The Spark builder.</param>
    /// <param name="declaringAssemblies">
    /// Assemblies that declare <see cref="ContributionAttribute"/> properties, when the defaults below
    /// do not reach them (a test host, whose entry assembly is the test runner). Usually empty.
    /// </param>
    /// <remarks>
    /// <para>
    /// The generated declarations register themselves from a module initializer, which runs at the first
    /// use of their assembly's code. Covered without arguments: the entry assembly and those of its
    /// direct references that reference <c>MintPlayer.Spark.Contributions.Abstractions</c> (now), and
    /// the assembly of every model entity type (at startup, and when the pipeline first meets the type).
    /// Nothing is scanned by reflection.
    /// </para>
    /// <para>
    /// Grant <c>RevertContribution/T</c> per generated contribution type in <c>security.json</c> (see
    /// <see cref="ContributionRights"/>); the verb itself lands with the moderation screens (M5b).
    /// </para>
    /// </remarks>
    public static ISparkBuilder AddContributions(this ISparkBuilder builder, params Assembly[] declaringAssemblies)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var assembly in declaringAssemblies)
            ContributionCatalog.EnsureInitialized(assembly);
        ContributionCatalog.EnsureEntryAssembliesInitialized();

        builder.Services.TryAddSingleton<ContributionCatalog>();
        builder.Services.TryAddScoped<IContributions, SparkContributions>();
        builder.AddPersistentObjectInterceptor<ContributionsInterceptor>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ContributionShapeCheck>());
        return builder;
    }
}
