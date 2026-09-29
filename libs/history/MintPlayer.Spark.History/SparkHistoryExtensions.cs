using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.History.Endpoints;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.History;

/// <summary>Registration for revision history.</summary>
public static class SparkHistoryExtensions
{
    private const string ConfigurationSection = "Spark:History";

    /// <summary>
    /// Revision history: at startup each model type's <c>revisions</c> block is merged into the
    /// database's revisions configuration; <see cref="IAuditable"/> entities are stamped on every write;
    /// <c>POST /spark/po/revisions</c>, <c>/spark/po/revision</c> and <c>/spark/po/revert</c> are
    /// mapped; <see cref="ISparkRevisionObserver"/>s are told about every write. Binds
    /// <c>Spark:History</c>, then applies <paramref name="configure"/> (code wins).
    /// </summary>
    /// <remarks>
    /// Grant per type in <c>security.json</c>: <c>History/T</c> to read revisions, <c>Revert/T</c>
    /// (with <c>Edit/T</c>) to revert (see <see cref="HistoryRights"/>).
    /// </remarks>
    public static ISparkBuilder AddHistory(this ISparkBuilder builder, Action<SparkHistoryOptions>? configure = null)
    {
        var section = builder.Configuration?.GetSection(ConfigurationSection);
        builder.Services.AddOptions<SparkHistoryOptions>().Configure(options =>
        {
            section?.Bind(options);
            configure?.Invoke(options);
        });
        // Revision limits: configuration beats code (D14, M16) — re-applied after every Configure.
        if (section is not null)
            builder.Services.PostConfigure<SparkHistoryOptions>(options => ApplyRevisionConfiguration(section, options));

        builder.Services.AddPersistentObjectInterceptor<HistoryInterceptor>();
        builder.Services.TryAddScoped<HistoryRequestState>();
        builder.Services.TryAddScoped<ISparkHistory, SparkHistory>();

        builder.Registry.AddMiddleware(app =>
        {
            var services = app.ApplicationServices;
            var options = services.GetRequiredService<IOptions<SparkHistoryOptions>>().Value;
            if (!options.ConfigureRevisions)
                return;
            RevisionsConfigurator.Apply(
                services.GetRequiredService<IDocumentStore>(),
                services.GetRequiredService<IModelLoader>(),
                options,
                services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RevisionsConfigurator).FullName!));
        });
        // The per-library method the Endpoints generator emits for this assembly (AssemblyInfo.cs).
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkHistoryEndpoints());

        return builder;
    }

    /// <summary>
    /// Layers <c>Spark:History:Revisions</c> and <c>Spark:History:Types:{type}</c> over whatever code set,
    /// property by property (unset configuration keys leave the code value alone).
    /// </summary>
    internal static void ApplyRevisionConfiguration(IConfigurationSection historySection, SparkHistoryOptions options)
    {
        historySection.GetSection(nameof(SparkHistoryOptions.Revisions)).Bind(options.Revisions);
        foreach (var typeSection in historySection.GetSection(nameof(SparkHistoryOptions.Types)).GetChildren())
        {
            if (!options.Types.TryGetValue(typeSection.Key, out var type))
                options.Types[typeSection.Key] = type = new SparkRevisionTypeOptions();
            typeSection.Bind(type);
        }
    }

    /// <summary>Adds an <see cref="ISparkRevisionObserver"/>. Scoped, multi-registered; adding the same type twice is a no-op.</summary>
    public static ISparkBuilder AddRevisionObserver<TObserver>(this ISparkBuilder builder)
        where TObserver : class, ISparkRevisionObserver
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkRevisionObserver, TObserver>());
        return builder;
    }

    /// <summary>Sets the <see cref="IHistoryUserNameResolver"/> revision lists use (scoped; replaces an earlier one).</summary>
    public static ISparkBuilder AddHistoryUserNameResolver<TResolver>(this ISparkBuilder builder)
        where TResolver : class, IHistoryUserNameResolver
    {
        builder.Services.Replace(ServiceDescriptor.Scoped<IHistoryUserNameResolver, TResolver>());
        return builder;
    }
}
