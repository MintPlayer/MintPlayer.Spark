using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Messaging;

/// <summary>
/// Registers lane declarations. Used by applications through <c>AddMessaging</c>, and directly by
/// framework packages that own a lane of their own.
/// </summary>
/// <remarks>
/// A lane declaration is an ordinary service registration, which is what makes registration order
/// irrelevant: a package may add its lane before or after <c>AddMessaging</c> and it is picked up
/// either way. The previous approach — reaching into <see cref="IServiceCollection"/> for an
/// already-constructed registry — worked only in one order and failed silently in the other.
/// </remarks>
public static class SparkLaneExtensions
{
    /// <summary>Declares one or more lanes with a delegate.</summary>
    public static IServiceCollection AddSparkLane(this IServiceCollection services, Action<ILaneBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // Enumerable, not TryAdd: several packages each contribute their own lanes, and every
        // registration must be honoured rather than the first one winning.
        services.AddSingleton<ILaneConfigurator>(new DelegateLaneConfigurator((lanes, _) => configure(lanes)));
        return services;
    }

    /// <summary>Declares one or more lanes with access to the resolved container.</summary>
    public static IServiceCollection AddSparkLane(
        this IServiceCollection services, Action<ILaneBuilder, IServiceProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton<ILaneConfigurator>(
            provider => new DelegateLaneConfigurator(configure, provider));
        return services;
    }

    /// <summary>
    /// Declares lanes with a class the container constructs, so it may inject its dependencies
    /// rather than locating them.
    /// </summary>
    public static IServiceCollection AddSparkLane<TConfigurator>(this IServiceCollection services)
        where TConfigurator : class, ILaneConfigurator
    {
        services.AddSingleton<ILaneConfigurator, TConfigurator>();
        return services;
    }

    private sealed class DelegateLaneConfigurator(
        Action<ILaneBuilder, IServiceProvider> configure,
        IServiceProvider? provider = null) : ILaneConfigurator
    {
        public void Configure(ILaneBuilder lanes) => configure(lanes, provider!);
    }
}
