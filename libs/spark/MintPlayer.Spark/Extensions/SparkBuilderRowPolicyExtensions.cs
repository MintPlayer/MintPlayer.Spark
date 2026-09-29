using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Extensions;

/// <summary>Registration for row policies (#460, item 1).</summary>
public static class SparkBuilderRowPolicyExtensions
{
    /// <summary>
    /// Adds a row policy — an <see cref="IRowFilterPolicy"/> or <see cref="IRowCheckPolicy"/> — that
    /// row security ANDs into the rule of every entity type it <see cref="IRowPolicy.AppliesTo"/>.
    /// Scoped. Adding the same policy type twice is a no-op; the order policies are added in does not
    /// change any answer (they AND).
    /// </summary>
    public static ISparkBuilder AddSparkRowPolicy<TPolicy>(this ISparkBuilder builder)
        where TPolicy : class, IRowPolicy
    {
        builder.Services.AddSparkRowPolicy<TPolicy>();
        return builder;
    }

    /// <inheritdoc cref="AddSparkRowPolicy{TPolicy}(ISparkBuilder)" />
    /// <remarks>For add-on packages that register through <see cref="IServiceCollection"/>.</remarks>
    public static IServiceCollection AddSparkRowPolicy<TPolicy>(this IServiceCollection services)
        where TPolicy : class, IRowPolicy
    {
        if (services.Any(d => d.ServiceType == typeof(RowPolicyRegistration) && d.ImplementationInstance is RowPolicyRegistration { Type: var t } && t == typeof(TPolicy)))
            return services;

        services.TryAddScoped<TPolicy>();
        services.AddSingleton(new RowPolicyRegistration(typeof(TPolicy)));
        services.AddScoped<IRowPolicy>(sp => sp.GetRequiredService<TPolicy>());
        return services;
    }
}

/// <summary>Marks a policy type as added, so a second add is a no-op.</summary>
internal sealed record RowPolicyRegistration(Type Type);
