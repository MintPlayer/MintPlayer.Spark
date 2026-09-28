using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Extensions;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Spark's Data Protection wiring (#460, D5): always <c>AddDataProtection()</c>, with the key ring
/// persisted where <c>Spark:DataProtection</c> says.
/// </summary>
/// <remarks>
/// Both pieces are <see cref="IConfigureOptions{TOptions}"/> registrations, so an application that
/// still calls <c>AddDataProtection().SetApplicationName(…)</c> or <c>PersistKeysTo…</c> after
/// <c>AddSpark</c> overrides Spark rather than racing it. The refusal to start without persisted
/// keys is <see cref="Validate"/>, called from <c>UseSpark()</c> so it fails at startup instead of
/// on the first request that touches a cookie.
/// </remarks>
internal static class SparkDataProtection
{
    public static IServiceCollection AddSparkDataProtection(this IServiceCollection services)
    {
        services.AddSparkConfigurationSection<SparkDataProtectionOptions>(SparkDataProtectionOptions.SectionName);
        services.AddDataProtection();
        services.AddSingleton<IConfigureOptions<DataProtectionOptions>, ConfigureApplicationName>();
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>, ConfigureKeyRing>();
        return services;
    }

    /// <summary>The discriminator used when <see cref="SparkDataProtectionOptions.ApplicationName"/> is unset.</summary>
    internal static string DefaultApplicationName()
        => Assembly.GetEntryAssembly()?.GetName().Name ?? "MintPlayer.Spark";

    /// <summary>
    /// Refuses a non-Development host whose key ring would not survive a redeploy, and one that
    /// names two places for it.
    /// </summary>
    internal static void Validate(SparkDataProtectionOptions options, IHostEnvironment environment)
    {
        var hasPath = !string.IsNullOrWhiteSpace(options.KeysPath);

        if (hasPath && options.Storage != SparkDataProtectionStorage.None)
        {
            throw new InvalidOperationException(
                $"Spark:DataProtection sets both KeysPath ('{options.KeysPath}') and Storage "
                + $"({options.Storage}). The key ring lives in exactly one place; remove one of them.");
        }

        if (hasPath || options.Storage != SparkDataProtectionStorage.None || environment.IsDevelopment())
            return;

        throw new InvalidOperationException(
            $"The Data Protection key ring is not persisted (environment '{environment.EnvironmentName}'). "
            + "Every authentication cookie and antiforgery token is encrypted with it, so a key ring that "
            + "does not survive a redeploy signs every user out on every redeploy. Set one of:\n"
            + "  Spark:DataProtection:Storage = RavenDb   (keys stored in the application's database), or\n"
            + "  Spark:DataProtection:KeysPath = <folder> (⚠️ must be a durable, mounted volume — a folder "
            + "inside the container is lost on redeploy exactly like the default).\n"
            + "Also set Spark:DataProtection:ApplicationName if the entry assembly name may change.");
    }

    /// <summary>The Development fallback: a per-user folder, one per application name.</summary>
    internal static string DevelopmentKeysPath(string applicationName)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
            root = Path.GetTempPath();

        var safeName = string.Concat(applicationName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(root, "MintPlayer.Spark", "DataProtection-Keys", safeName);
    }

    private sealed class ConfigureApplicationName(IOptions<SparkDataProtectionOptions> spark) : IConfigureOptions<DataProtectionOptions>
    {
        public void Configure(DataProtectionOptions options)
            => options.ApplicationDiscriminator = ResolveApplicationName(spark.Value);
    }

    private static string ResolveApplicationName(SparkDataProtectionOptions options)
        => string.IsNullOrWhiteSpace(options.ApplicationName) ? DefaultApplicationName() : options.ApplicationName;

    /// <summary>
    /// Chooses the repository. The document store is resolved here, when the key manager first
    /// asks for its options — never at registration time, which is before the store exists.
    /// </summary>
    private sealed class ConfigureKeyRing(
        IOptions<SparkDataProtectionOptions> spark,
        ILoggerFactory loggerFactory,
        IServiceProvider services) : IConfigureOptions<KeyManagementOptions>
    {
        public void Configure(KeyManagementOptions options)
        {
            var value = spark.Value;
            // Optional: a bare ServiceCollection (unit tests, tooling) has no host environment.
            var environment = services.GetService<IHostEnvironment>();

            if (value.Storage == SparkDataProtectionStorage.RavenDb)
            {
                options.XmlRepository = new RavenDataProtectionKeyRepository(services.GetRequiredService<IDocumentStore>());
                return;
            }

            // A relative KeysPath is relative to the content root, like every other App_Data path.
            var path = !string.IsNullOrWhiteSpace(value.KeysPath)
                ? Path.Combine(environment?.ContentRootPath ?? string.Empty, value.KeysPath)
                : environment?.IsDevelopment() == true
                    ? DevelopmentKeysPath(ResolveApplicationName(value))
                    : null;

            // Unset outside Development: UseSpark() refuses to start, so leaving ASP.NET Core's
            // default here only matters to a host that never calls it.
            if (path is null)
                return;

            options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(path), loggerFactory);
        }
    }
}
