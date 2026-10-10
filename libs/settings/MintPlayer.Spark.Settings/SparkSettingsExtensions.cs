using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Settings.Interceptors;
using MintPlayer.Spark.Settings.Services;

namespace MintPlayer.Spark.Settings;

/// <summary>
/// Admin-changeable settings (<c>docs/seo_analyzer_PRD.md</c> X9):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddAuthentication&lt;SparkUser&gt;();
///     spark.AddSettings(typeof(SeoSettings));
/// });
/// // App_Data/security.json: "bindings": { "settings:administrators": [ "Administrators" ] }
/// var maxPages = await settings.GetAsync(SeoSettings.MaxPages);
/// </code>
/// </summary>
public static class SparkSettingsExtensions
{
    /// <summary>
    /// Declares the settings held as public static <see cref="SparkSetting{T}"/> fields or properties of
    /// <paramref name="declaringTypes"/>, and registers <see cref="ISparkSettings"/>, the startup seeder of
    /// their <c>Settings/{Key}</c> documents and the save validation. Call it again (from another library
    /// or module) to declare more.
    /// </summary>
    /// <exception cref="InvalidOperationException">A type declares no setting, a key is malformed, or one key is declared twice differently.</exception>
    public static ISparkBuilder AddSettings(this ISparkBuilder builder, params Type[] declaringTypes)
    {
        var services = builder.Services;
        var catalog = services.FirstOrDefault(d => d.ServiceType == typeof(SparkSettingsCatalog))?.ImplementationInstance as SparkSettingsCatalog;
        if (catalog is null)
        {
            catalog = new SparkSettingsCatalog();
            services.AddSingleton(catalog);

            services.AddOptions<SparkSettingsOptions>();
            services.AddSingleton<IConfigureOptions<SparkSettingsOptions>>(sp => new ConfigureNamedOptions<SparkSettingsOptions>(
                Options.DefaultName,
                options => sp.GetService<IConfiguration>()?.GetSection(SparkSettingsOptions.SectionName).Bind(options)));

            services.TryAddSingleton<ISparkSettings, SparkSettings>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SettingsSeeder>());
            builder.AddInterceptor<SettingInterceptors>();
        }

        foreach (var type in declaringTypes)
            catalog.AddFrom(type);
        return builder;
    }

    /// <summary>
    /// Declares the per-user quotas held as public static <see cref="Quotas.SparkQuota"/> fields or properties of
    /// <paramref name="declaringTypes"/> (X3), with their limit and window settings, and registers
    /// <see cref="Quotas.ISparkQuotas"/> and the gate that applies <c>[SparkQuota]</c> to custom actions. Startup
    /// refuses a <c>[SparkQuota]</c> naming a quota nobody declared.
    /// </summary>
    public static ISparkBuilder AddQuotas(this ISparkBuilder builder, params Type[] declaringTypes)
    {
        var services = builder.Services;
        builder.AddSettings([]);
        var settingsCatalog = (SparkSettingsCatalog)services.First(d => d.ServiceType == typeof(SparkSettingsCatalog)).ImplementationInstance!;
        var quotaCatalog = services.FirstOrDefault(d => d.ServiceType == typeof(Quotas.SparkQuotaCatalog))?.ImplementationInstance as Quotas.SparkQuotaCatalog;
        if (quotaCatalog is null)
        {
            quotaCatalog = new Quotas.SparkQuotaCatalog();
            services.AddSingleton(quotaCatalog);
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<Quotas.ISparkQuotas, Quotas.SparkQuotas>();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<MintPlayer.Spark.Abstractions.Actions.ISparkCustomActionGate, Quotas.SparkQuotaGate>());
            // Usage documents carry @expires (their last use + the window), so the database must expire documents.
            builder.Registry.RequireDocumentExpiration("Settings quotas");
            builder.Registry.AddMiddleware(_ => Quotas.SparkQuotaStartup.VerifyAttributes(quotaCatalog));
        }

        foreach (var type in declaringTypes)
        {
            var before = quotaCatalog.All.ToList();
            quotaCatalog.AddFrom(type);
            foreach (var quota in quotaCatalog.All.Except(before))
            {
                settingsCatalog.Add(quota.Limit);
                settingsCatalog.Add(quota.Window);
            }
        }
        return builder;
    }

    /// <summary>As <see cref="AddSettings(ISparkBuilder, Type[])"/>, with options in code (applied after <c>Spark:Settings</c>).</summary>
    public static ISparkBuilder AddSettings(this ISparkBuilder builder, Action<SparkSettingsOptions> configure, params Type[] declaringTypes)
    {
        builder.AddSettings(declaringTypes);
        builder.Services.Configure(configure);
        return builder;
    }
}
