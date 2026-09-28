using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.SoftDelete.Endpoints;

namespace MintPlayer.Spark.SoftDelete;

/// <summary>Registration for soft deletion.</summary>
public static class SparkSoftDeleteExtensions
{
    private const string ConfigurationSection = "Spark:SoftDelete";

    /// <summary>
    /// Makes every <see cref="ISoftDeletable"/> entity soft-deletable: a delete sets the fields instead
    /// of removing the document, deleted rows disappear from every read path, and
    /// <c>POST /spark/po/restore</c> / <c>POST /spark/po/purge</c> are mapped. Binds
    /// <c>Spark:SoftDelete</c>, then applies <paramref name="configure"/> (code wins).
    /// </summary>
    /// <remarks>
    /// Grant the new rights per type in <c>security.json</c>: <c>Restore/T</c>, <c>Purge/T</c>,
    /// <c>ViewDeleted/T</c> (see <see cref="SoftDeleteRights"/>). Without them nobody can restore,
    /// purge or see the recycle bin — deleting still works under <c>Delete/T</c>.
    /// </remarks>
    public static ISparkBuilder AddSoftDelete(this ISparkBuilder builder, Action<SparkSoftDeleteOptions>? configure = null)
    {
        var section = builder.Configuration?.GetSection(ConfigurationSection);
        builder.Services.AddOptions<SparkSoftDeleteOptions>().Configure(options =>
        {
            section?.Bind(options);
            configure?.Invoke(options);
        });

        builder.Services.AddSparkRowPolicy<SoftDeleteRowPolicy>();
        builder.Services.AddPersistentObjectInterceptor<SoftDeleteInterceptor>();
        builder.Services.TryAddScoped<SoftDeleteRequestState>();
        builder.Services.TryAddScoped<ISparkSoftDelete, SparkSoftDelete>();
        builder.Services.TryAddSingleton<ISoftDeleteRevisions, RavenSoftDeleteRevisions>();

        builder.Registry.AddMiddleware(app => SoftDeleteStartupCheck.Run(app.ApplicationServices));
        // The per-library method the Endpoints generator emits for this assembly (AssemblyInfo.cs).
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkSoftDeleteEndpoints());

        return builder;
    }

    /// <summary>Adds an <see cref="ISoftDeleteObserver"/>. Scoped, multi-registered; adding the same type twice is a no-op.</summary>
    public static ISparkBuilder AddSoftDeleteObserver<TObserver>(this ISparkBuilder builder)
        where TObserver : class, ISoftDeleteObserver
    {
        builder.Services.AddSoftDeleteObserver<TObserver>();
        return builder;
    }

    /// <inheritdoc cref="AddSoftDeleteObserver{TObserver}(ISparkBuilder)" />
    public static IServiceCollection AddSoftDeleteObserver<TObserver>(this IServiceCollection services)
        where TObserver : class, ISoftDeleteObserver
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISoftDeleteObserver, TObserver>());
        return services;
    }
}
