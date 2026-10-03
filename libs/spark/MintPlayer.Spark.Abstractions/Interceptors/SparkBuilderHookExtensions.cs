using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Abstractions.Interceptors;

/// <summary>Registration for persistence hooks (#482).</summary>
public static class SparkBuilderHookExtensions
{
    /// <summary>The phase interfaces a hook can implement; the hook is forwarded once per interface it implements.</summary>
    private static readonly Type[] Phases =
    [
        typeof(IBeforeSave), typeof(IAfterSave), typeof(IBeforeDelete), typeof(IAfterDelete),
        typeof(IDeleteReplacement), typeof(IAfterMaterialize), typeof(IAfterLoad), typeof(INaturalIdCollision),
        typeof(IAfterSaveCommitted), typeof(IAfterDeleteCommitted),
    ];

    /// <summary>
    /// Adds a persistence hook: scoped, forwarded to every phase interface it implements, so one
    /// instance per request serves all its phases. Adding the same hook type twice is a no-op.
    /// </summary>
    public static ISparkBuilder AddHook<THook>(this ISparkBuilder builder)
        where THook : class, ISparkHook
    {
        builder.Services.AddSparkHook<THook>();
        return builder;
    }

    /// <inheritdoc cref="AddHook{THook}(ISparkBuilder)" />
    /// <remarks>For add-on packages that register through <see cref="IServiceCollection"/>.</remarks>
    public static IServiceCollection AddSparkHook<THook>(this IServiceCollection services)
        where THook : class, ISparkHook
        => services.AddSparkHook(typeof(THook));

    /// <inheritdoc cref="AddHook{THook}(ISparkBuilder)" />
    public static IServiceCollection AddSparkHook(this IServiceCollection services, Type hookType)
    {
        if (!typeof(ISparkHook).IsAssignableFrom(hookType) || hookType.IsAbstract || hookType.IsInterface)
            throw new ArgumentException($"'{hookType.FullName}' is not a concrete {nameof(ISparkHook)}.", nameof(hookType));

        if (services.Any(d => d.ServiceType == typeof(HookRegistration) && d.ImplementationInstance is HookRegistration { Type: var t } && t == hookType))
            return services;

        services.TryAddScoped(hookType);
        services.AddSingleton(new HookRegistration(hookType));
        foreach (var phase in Phases)
        {
            if (phase.IsAssignableFrom(hookType))
                services.AddScoped(phase, sp => sp.GetRequiredService(hookType));
        }
        return services;
    }
}

/// <summary>Marks a hook type as added, so a second add is a no-op.</summary>
internal sealed record HookRegistration(Type Type);
