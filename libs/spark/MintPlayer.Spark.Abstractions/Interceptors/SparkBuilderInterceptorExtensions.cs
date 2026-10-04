using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Abstractions.Interceptors;

/// <summary>Registration for persistence interceptors (#482).</summary>
public static class SparkBuilderInterceptorExtensions
{
    /// <summary>The phase interfaces an interceptor can implement; the interceptor is forwarded once per interface it implements.</summary>
    private static readonly Type[] Phases =
    [
        typeof(IBeforeSave), typeof(IAfterSave), typeof(IBeforeDelete), typeof(IAfterDelete),
        typeof(IDeleteReplacement), typeof(IAfterMaterialize), typeof(IAfterLoad), typeof(INaturalIdCollision),
        typeof(IAfterSaveCommitted), typeof(IAfterDeleteCommitted),
    ];

    /// <summary>
    /// Adds a persistence interceptor: scoped, forwarded to every phase interface it implements, so one
    /// instance per request serves all its phases. Adding the same interceptor type twice is a no-op.
    /// </summary>
    public static ISparkBuilder AddInterceptor<TInterceptor>(this ISparkBuilder builder)
        where TInterceptor : class, ISparkInterceptor
    {
        builder.Services.AddSparkInterceptor<TInterceptor>();
        return builder;
    }

    /// <inheritdoc cref="AddInterceptor{TInterceptor}(ISparkBuilder)" />
    /// <remarks>For add-on packages that register through <see cref="IServiceCollection"/>.</remarks>
    public static IServiceCollection AddSparkInterceptor<TInterceptor>(this IServiceCollection services)
        where TInterceptor : class, ISparkInterceptor
        => services.AddSparkInterceptor(typeof(TInterceptor));

    /// <inheritdoc cref="AddInterceptor{TInterceptor}(ISparkBuilder)" />
    public static IServiceCollection AddSparkInterceptor(this IServiceCollection services, Type interceptorType)
    {
        if (!typeof(ISparkInterceptor).IsAssignableFrom(interceptorType) || interceptorType.IsAbstract || interceptorType.IsInterface)
            throw new ArgumentException($"'{interceptorType.FullName}' is not a concrete {nameof(ISparkInterceptor)}.", nameof(interceptorType));

        if (services.Any(d => d.ServiceType == typeof(InterceptorRegistration) && d.ImplementationInstance is InterceptorRegistration { Type: var t } && t == interceptorType))
            return services;

        services.TryAddScoped(interceptorType);
        services.AddSingleton(new InterceptorRegistration(interceptorType));
        foreach (var phase in Phases)
        {
            if (phase.IsAssignableFrom(interceptorType))
                services.AddScoped(phase, sp => sp.GetRequiredService(interceptorType));
        }
        return services;
    }
}

/// <summary>Marks an interceptor type as added, so a second add is a no-op.</summary>
internal sealed record InterceptorRegistration(Type Type);
