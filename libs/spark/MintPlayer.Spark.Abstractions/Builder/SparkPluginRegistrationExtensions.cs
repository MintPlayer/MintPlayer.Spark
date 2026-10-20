using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Abstractions.Builder;

/// <summary>
/// An options class bound from one configuration section (<c>Spark:Notifications</c>,
/// <c>Spark:Dashboards</c>, …). The section path is part of the type, so registration, validation
/// messages and documentation all name the same one.
/// </summary>
public interface ISparkConfigurationSection
{
    /// <summary>The configuration path the options bind from, e.g. <c>Spark:Notifications</c>.</summary>
    static abstract string SectionName { get; }
}

/// <summary>
/// The registration conventions every Spark plugin follows (X20 item 8), so a package's
/// <c>AddXxx()</c> is a few lines instead of a hand-copied block: options bound lazily from a
/// configuration section, a registration that is safe to call more than once, and provider slots that
/// hold exactly one implementation.
/// </summary>
/// <remarks>
/// Runtime helpers rather than a source generator: each one replaces three to eight lines, and a
/// generator would add a build step to every plugin for no further gain (commerce PRD X20).
/// </remarks>
public static class SparkPluginRegistrationExtensions
{
    /// <summary>
    /// Binds <typeparamref name="TOptions"/> from <paramref name="sectionPath"/> of the host's
    /// <see cref="IConfiguration"/>, resolved when the options are first read. Code that configures the
    /// same options afterwards (<c>services.Configure&lt;TOptions&gt;(…)</c>) runs later and wins.
    /// </summary>
    /// <remarks>
    /// Not <c>BindConfiguration</c>: that requires an <see cref="IConfiguration"/> in the container,
    /// and <c>AddSpark(configure)</c> is also used over a bare <see cref="ServiceCollection"/> (unit
    /// tests, tooling) that has none. A missing configuration binds nothing and leaves the defaults.
    /// </remarks>
    public static IServiceCollection AddSparkConfigurationSection<TOptions>(this IServiceCollection services, string sectionPath)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);

        services.AddOptions<TOptions>();
        services.AddSingleton<IConfigureOptions<TOptions>>(sp => new ConfigureNamedOptions<TOptions>(
            Options.DefaultName,
            options => sp.GetService<IConfiguration>()?.GetSection(sectionPath).Bind(options)));
        return services;
    }

    /// <summary>
    /// Binds <typeparamref name="TOptions"/> from its own <see cref="ISparkConfigurationSection.SectionName"/>,
    /// as <see cref="AddSparkConfigurationSection{TOptions}(IServiceCollection, string)"/> does.
    /// </summary>
    public static IServiceCollection AddSparkConfigurationSection<TOptions>(this IServiceCollection services)
        where TOptions : class, ISparkConfigurationSection
        => services.AddSparkConfigurationSection<TOptions>(TOptions.SectionName);

    /// <summary>
    /// Registers the marker type <typeparamref name="TMarker"/> the first time, and answers whether this
    /// call was the first. A plugin's <c>AddXxx()</c> returns early on <see langword="false"/>, so calling
    /// it twice (the host and a bundle, or two packages that both need it) registers everything once.
    /// </summary>
    /// <example>
    /// <code>
    /// if (!builder.Services.TryAddSparkMarker&lt;NotificationsMarker&gt;())
    ///     return builder;
    /// </code>
    /// </example>
    public static bool TryAddSparkMarker<TMarker>(this IServiceCollection services)
        where TMarker : class, new()
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(d => d.ServiceType == typeof(TMarker)))
            return false;

        services.AddSingleton(new TMarker());
        return true;
    }

    /// <summary>
    /// The singleton instance of <typeparamref name="T"/> already in <paramref name="services"/>, or a new
    /// one from <paramref name="create"/>, registered — the find-or-create shape of a plugin whose every
    /// call adds to one shared catalog. <paramref name="created"/> tells the first call, which wires the
    /// rest of the plugin, from the later ones.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> is registered, but not as an instance (a factory or a type), so there is
    /// no catalog to add to.
    /// </exception>
    public static T GetOrAddSparkSingleton<T>(this IServiceCollection services, Func<T> create, out bool created)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(create);

        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(T));
        if (existing is not null)
        {
            created = false;
            return existing.ImplementationInstance as T
                ?? throw new InvalidOperationException(
                    $"{typeof(T).Name} is registered, but not as a singleton instance, so a second registration call has nothing to add to.");
        }

        var instance = create();
        services.AddSingleton(instance);
        created = true;
        return instance;
    }

    /// <summary>
    /// Makes <typeparamref name="TImplementation"/> the one implementation of the provider slot
    /// <typeparamref name="TService"/>: every earlier registration of the slot is removed first.
    /// </summary>
    /// <remarks>
    /// A slot is a service the framework resolves exactly one of (the group-membership provider, the mail
    /// transport, an SMS sender). <c>AddScoped</c> on its own would leave both registrations in the
    /// container, and which one runs would then depend on registration order; <c>Replace</c> removes only
    /// the first match. The last call wins, deterministically.
    /// </remarks>
    public static IServiceCollection UseSparkProvider<TService, TImplementation>(this IServiceCollection services, ServiceLifetime lifetime)
        where TService : class
        where TImplementation : class, TService
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<TService>();
        services.Add(ServiceDescriptor.Describe(typeof(TService), typeof(TImplementation), lifetime));
        return services;
    }

    /// <summary>
    /// Makes the instance <paramref name="factory"/> builds the one implementation of the provider slot
    /// <typeparamref name="TService"/>, as <see cref="UseSparkProvider{TService, TImplementation}"/> does.
    /// </summary>
    public static IServiceCollection UseSparkProvider<TService>(this IServiceCollection services, Func<IServiceProvider, TService> factory, ServiceLifetime lifetime)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);

        services.RemoveAll<TService>();
        services.Add(ServiceDescriptor.Describe(typeof(TService), factory, lifetime));
        return services;
    }
}
