using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;

namespace MintPlayer.Spark.Extensions;

/// <summary>Registration for persistent-object interceptors (#460, item 1).</summary>
public static class SparkBuilderInterceptorExtensions
{
    /// <summary>
    /// Adds a persistent-object interceptor. Scoped; before-hooks run in the order interceptors are
    /// added, after-hooks in reverse. Adding the same interceptor type twice is a no-op.
    /// </summary>
    public static ISparkBuilder AddPersistentObjectInterceptor<TInterceptor>(this ISparkBuilder builder)
        where TInterceptor : class, IPersistentObjectInterceptor
    {
        builder.Services.AddPersistentObjectInterceptor<TInterceptor>();
        return builder;
    }

    /// <inheritdoc cref="AddPersistentObjectInterceptor{TInterceptor}(ISparkBuilder)" />
    /// <remarks>For add-on packages that register through <see cref="IServiceCollection"/>.</remarks>
    public static IServiceCollection AddPersistentObjectInterceptor<TInterceptor>(this IServiceCollection services)
        where TInterceptor : class, IPersistentObjectInterceptor
    {
        if (services.Any(d => d.ServiceType == typeof(InterceptorRegistration) && d.ImplementationInstance is InterceptorRegistration { Type: var t } && t == typeof(TInterceptor)))
            return services;

        services.TryAddScoped<TInterceptor>();
        services.AddSingleton(new InterceptorRegistration(typeof(TInterceptor)));
        services.AddScoped<IPersistentObjectInterceptor>(sp => sp.GetRequiredService<TInterceptor>());
        return services;
    }
}

/// <summary>Marks an interceptor type as added, so a second add is a no-op.</summary>
internal sealed record InterceptorRegistration(Type Type);
