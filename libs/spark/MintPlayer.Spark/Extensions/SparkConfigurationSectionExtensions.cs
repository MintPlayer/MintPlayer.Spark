using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Extensions;

internal static class SparkConfigurationSectionExtensions
{
    /// <summary>
    /// Binds <typeparamref name="TOptions"/> from <paramref name="sectionPath"/> of the host's
    /// <see cref="IConfiguration"/>, resolved when the options are first read.
    /// </summary>
    /// <remarks>
    /// Not <c>BindConfiguration</c>: that requires an <see cref="IConfiguration"/> in the container,
    /// and <c>AddSpark(configure)</c> is also used over a bare <see cref="ServiceCollection"/> (unit
    /// tests, tooling) that has none. A missing configuration binds nothing and leaves the defaults.
    /// </remarks>
    public static IServiceCollection AddSparkConfigurationSection<TOptions>(this IServiceCollection services, string sectionPath)
        where TOptions : class
    {
        services.AddOptions<TOptions>();
        services.AddSingleton<IConfigureOptions<TOptions>>(sp => new ConfigureNamedOptions<TOptions>(
            Options.DefaultName,
            options => sp.GetService<IConfiguration>()?.GetSection(sectionPath).Bind(options)));
        return services;
    }
}
