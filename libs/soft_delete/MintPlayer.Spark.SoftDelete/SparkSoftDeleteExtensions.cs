using MintPlayer.Spark.Abstractions.Interceptors;
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
    [global::MintPlayer.Spark.Abstractions.Storage.SparkRequiresStore("Spark.SoftDelete", global::MintPlayer.Spark.Abstractions.Storage.SparkStoreCapabilities.Revisions)]
    public static ISparkBuilder AddSoftDelete(this ISparkBuilder builder, Action<SparkSoftDeleteOptions>? configure = null)
    {
        // #551 D13, #536 S8: a purge deletes the row's revisions through the store's ISparkRevisions, on any store that
        // keeps them. The raw-delete guard and the index checks are RavenDB's alone (they need its client), and run only there.
        global::MintPlayer.Spark.Abstractions.Storage.SparkStoreRequirementExtensions.RequireStore(builder, "Spark.SoftDelete", global::MintPlayer.Spark.Abstractions.Storage.SparkStoreCapabilities.Revisions);

        var section = builder.Configuration?.GetSection(ConfigurationSection);
        builder.Services.AddOptions<SparkSoftDeleteOptions>().Configure(options =>
        {
            section?.Bind(options);
            configure?.Invoke(options);
        });

        builder.Services.AddSparkRowPolicy<SoftDeleteRowPolicy>();
        builder.Services.AddSparkInterceptor<SoftDeleteInterceptor>();
        builder.Services.TryAddScoped<SoftDeleteRequestState>();
        builder.Services.TryAddScoped<ISparkSoftDelete, SparkSoftDelete>();
        builder.Services.TryAddSingleton<ISoftDeleteRevisions, SoftDeleteRevisions>();

        builder.Registry.AddMiddleware(app => SoftDeleteStartupCheck.Run(app.ApplicationServices));
        // The per-library method the Endpoints generator emits for this assembly (AssemblyInfo.cs).
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkSoftDeleteEndpoints());

        return builder;
    }
}
