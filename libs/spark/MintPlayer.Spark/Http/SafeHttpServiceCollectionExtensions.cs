using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Http;

/// <summary>
/// Registers SSRF-safe named <see cref="HttpClient"/>s (<c>docs/seo_analyzer_PRD.md</c> X2). Use one for
/// every request to a URL a user or an administrator typed: a client's JWKS or logout URI, a site to
/// audit, a webhook target.
/// <code>
/// services.AddSafeHttpClient("MyApp.Fetcher", configuration.GetSection("MyApp:Fetcher"), o => o.MaxRedirects = 2);
/// var client = httpClientFactory.CreateClient("MyApp.Fetcher");
/// </code>
/// What the client refuses is described on <see cref="SafeHttpPolicy"/> and <see cref="SafeHttpMessageHandler"/>;
/// a refusal is a <see cref="SafeHttpBlockedException"/>.
/// </summary>
public static class SafeHttpServiceCollectionExtensions
{
    /// <summary>
    /// Registers the named client <paramref name="name"/>. Its <see cref="SafeHttpOptions"/> are the named
    /// options of the same name: <paramref name="configuration"/> is bound first, <paramref name="configure"/>
    /// runs second, and an earlier <c>services.Configure&lt;SafeHttpOptions&gt;(name, …)</c> supplies defaults
    /// beneath both. Calling it again for the same name adds configuration, not a second set of handlers.
    /// </summary>
    public static IHttpClientBuilder AddSafeHttpClient(
        this IServiceCollection services,
        string name,
        IConfiguration? configuration = null,
        Action<SafeHttpOptions>? configure = null)
    {
        services.TryAddSingleton<ISafeHttpDnsResolver, SystemDnsResolver>();
        var options = services.AddOptions<SafeHttpOptions>(name);
        if (configuration is not null)
            options.Bind(configuration);
        if (configure is not null)
            options.Configure(configure);

        var builder = services.AddHttpClient(name);
        if (services.Any(d => d.ServiceType == typeof(SafeHttpClientRegistration) && d.ImplementationInstance is SafeHttpClientRegistration r && r.Name == name))
            return builder;
        services.AddSingleton(new SafeHttpClientRegistration(name));

        return builder
            .ConfigureHttpClient((sp, client) => client.Timeout = OptionsOf(sp, name).Timeout)
            .ConfigurePrimaryHttpMessageHandler(sp => CreatePrimaryHandler(OptionsOf(sp, name), sp.GetRequiredService<ISafeHttpDnsResolver>()))
            .AddHttpMessageHandler(sp =>
            {
                var o = OptionsOf(sp, name);
                return new SafeHttpMessageHandler(new SafeHttpPolicy(o), o);
            });
    }

    private static SafeHttpOptions OptionsOf(IServiceProvider services, string name)
        => services.GetRequiredService<IOptionsMonitor<SafeHttpOptions>>().Get(name);

    /// <summary>
    /// The primary handler: connects only through <see cref="SafeHttpConnector"/>, and never through a
    /// proxy (a proxy would make the connect callback vet the proxy instead of the target). No cookie
    /// jar shared across requests, no automatic redirects or decompression: the outer handler does both
    /// under its own checks and caps.
    /// </summary>
    internal static SocketsHttpHandler CreatePrimaryHandler(SafeHttpOptions options, ISafeHttpDnsResolver resolver)
    {
        var connector = new SafeHttpConnector(new SafeHttpPolicy(options), resolver);
        return new SocketsHttpHandler
        {
            ConnectCallback = connector.ConnectAsync,
            ConnectTimeout = options.ConnectTimeout,
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
    }

    /// <summary>Marks a client name as already wired, so a second registration does not stack handlers.</summary>
    private sealed record SafeHttpClientRegistration(string Name);
}
