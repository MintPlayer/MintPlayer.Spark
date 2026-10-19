using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Dashboards.Host;
using MintPlayer.Spark.Dashboards.Providers;
using MintPlayer.Spark.Dashboards.Services;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Dashboards;

/// <summary>
/// Administrator dashboards (<c>docs/guide-dashboards.md</c>; D11–D14, D29):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddAuthentication&lt;SparkUser&gt;();
///     spark.AddDashboards(dashboards => dashboards
///         .Add("OperatorDashboard", d => d.Provider&lt;OperatorFigures&gt;().DatabaseStatistics()));
/// });
/// // App_Data/Model/OperatorDashboard.json: "live": true, no clrType, the attributes and their renderers
/// // public sealed class OperatorDashboardActions(IServiceProvider services) : SparkDashboardActions(services);
/// // App_Data/security.json: "bindings": { "dashboards:administrators": [ "Administrators" ] },
/// //   and { "key": "operator-dashboard", "resource": "Read/OperatorDashboard", "groupId": "dashboards:administrators" }
/// </code>
/// </summary>
public static class SparkDashboardsExtensions
{
    /// <summary>The package's own dashboard: the RavenDB statistics, with the history of its size and document count.</summary>
    public const string DatabaseStatistics = "DatabaseStatistics";

    /// <summary>
    /// Registers the dashboards package: the shipped <c>DatabaseStatistics</c> dashboard, the live notifier, the read
    /// cache and the history sampler, then the application's dashboards from <paramref name="configure"/>. Call it
    /// again (from another library or module) to declare more.
    /// </summary>
    /// <exception cref="ArgumentException">A dashboard or history name is not a plain identifier.</exception>
    public static ISparkBuilder AddDashboards(this ISparkBuilder builder, Action<SparkDashboardsBuilder>? configure = null)
    {
        var services = builder.Services;
        var catalog = services.GetOrAddSparkSingleton(() => new DashboardCatalog(), out var created);
        if (created)
        {
            services.AddSparkConfigurationSection<SparkDashboardsOptions>();

            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<SparkCpuUsage>();
            services.TryAddSingleton<DashboardStorage>();
            services.TryAddSingleton<DashboardReader>();
            services.TryAddSingleton<DashboardSamplerLease>();
            services.TryAddSingleton<DashboardSampler>();
            services.AddHostedService(sp => sp.GetRequiredService<DashboardSampler>());
            services.AddHostedService<DashboardNotifier>();
            builder.Registry.AddMiddleware(app => DashboardStartup.Verify(app.ApplicationServices, catalog));

            new SparkDashboardsBuilder(catalog).Add(DatabaseStatistics, d => d
                .DatabaseStatistics()
                .History("DatabaseSize", "DocumentCount"));
        }

        configure?.Invoke(new SparkDashboardsBuilder(catalog));
        return builder;
    }

    /// <summary>As <see cref="AddDashboards(ISparkBuilder, Action{SparkDashboardsBuilder}?)"/>, with options in code (applied after <c>Spark:Dashboards</c>).</summary>
    public static ISparkBuilder AddDashboards(this ISparkBuilder builder, Action<SparkDashboardsOptions> options, Action<SparkDashboardsBuilder>? configure = null)
    {
        builder.AddDashboards(configure);
        builder.Services.Configure(options);
        return builder;
    }
}

/// <summary>What <c>UseSpark()</c> checks: every declared dashboard can actually be served.</summary>
internal static class DashboardStartup
{
    public static void Verify(IServiceProvider services, DashboardCatalog catalog)
    {
        var options = services.GetRequiredService<IOptions<SparkDashboardsOptions>>().Value;
        var problems = new List<string>();
        foreach (var (name, value) in new[]
                 {
                     (nameof(options.RefreshInterval), options.RefreshInterval), (nameof(options.SampleInterval), options.SampleInterval),
                     (nameof(options.RawRetention), options.RawRetention), (nameof(options.RollupInterval), options.RollupInterval),
                     (nameof(options.RollupRetention), options.RollupRetention), (nameof(options.HistoryWindow), options.HistoryWindow),
                 })
        {
            if (value <= TimeSpan.Zero)
                problems.Add($"{SparkDashboardsOptions.SectionName}:{name} must be positive.");
        }
        if (options.CacheDuration < TimeSpan.Zero)
            problems.Add($"{SparkDashboardsOptions.SectionName}:CacheDuration must not be negative.");
        if (options.HistoryPoints < 1)
            problems.Add($"{SparkDashboardsOptions.SectionName}:HistoryPoints must be at least 1.");
        if (options.RollupInterval >= options.RollupRetention)
            problems.Add($"{SparkDashboardsOptions.SectionName}:RollupRetention must be longer than RollupInterval.");

        var modelLoader = services.GetRequiredService<IModelLoader>();
        using var scope = services.CreateScope();
        var actionsResolver = scope.ServiceProvider.GetRequiredService<IActionsResolver>();
        foreach (var dashboard in catalog.All)
        {
            var type = modelLoader.GetEntityTypeByName(dashboard.Name);
            if (type is null)
            {
                problems.Add($"Dashboard '{dashboard.Name}' has no model type: add App_Data/Model/{dashboard.Name}.json with \"live\": true and no clrType.");
                continue;
            }
            if (!string.IsNullOrEmpty(type.ClrType))
                problems.Add($"Dashboard '{dashboard.Name}': its model type has a clrType; a dashboard is a virtual type (remove \"clrType\").");
            if (!type.Live)
                problems.Add($"Dashboard '{dashboard.Name}': its model type is not live; add \"live\": true so viewers follow it over /spark/live.");
            if (actionsResolver.ResolveByEntityName(dashboard.Name) is not SparkDashboardActions)
                problems.Add($"Dashboard '{dashboard.Name}' has no actions class: add 'public sealed class {dashboard.Name}Actions(IServiceProvider services) : SparkDashboardActions(services);'.");
        }

        if (problems.Count > 0)
            throw new InvalidOperationException("The dashboards cannot be served:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }
}
