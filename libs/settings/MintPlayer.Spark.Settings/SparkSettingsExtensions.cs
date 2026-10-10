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

    /// <summary>As <see cref="AddSettings(ISparkBuilder, Type[])"/>, with options in code (applied after <c>Spark:Settings</c>).</summary>
    public static ISparkBuilder AddSettings(this ISparkBuilder builder, Action<SparkSettingsOptions> configure, params Type[] declaringTypes)
    {
        builder.AddSettings(declaringTypes);
        builder.Services.Configure(configure);
        return builder;
    }
}
