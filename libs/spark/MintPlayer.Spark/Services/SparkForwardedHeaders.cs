using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Extensions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Spark's forwarded-headers wiring (#460, D15): options from <c>Spark:ForwardedHeaders</c>, and
/// <c>UseForwardedHeaders()</c> at the very front of the pipeline.
/// </summary>
/// <remarks>
/// An <see cref="IStartupFilter"/> rather than a step inside <c>UseSpark()</c>, because
/// <c>UseSpark()</c> runs after <c>UseRouting()</c> — and <c>UseHttpsRedirection()</c>,
/// <c>UseStaticFiles()</c> and every rate limiter must already see the forwarded scheme and
/// address. Behind a TLS-terminating proxy an HTTPS redirect that ran first would see
/// <c>http</c> and redirect forever. A startup filter wraps the application's whole
/// <c>Configure</c>, so it runs before anything the application adds.
/// </remarks>
internal static class SparkForwardedHeaders
{
    /// <summary>The trust list used when configuration names none.</summary>
    internal static readonly IReadOnlyList<System.Net.IPNetwork> DefaultNetworks =
    [
        System.Net.IPNetwork.Parse("127.0.0.0/8"),
        System.Net.IPNetwork.Parse("::1/128"),
        System.Net.IPNetwork.Parse("10.0.0.0/8"),
        System.Net.IPNetwork.Parse("172.16.0.0/12"),
        System.Net.IPNetwork.Parse("192.168.0.0/16"),
        System.Net.IPNetwork.Parse("fc00::/7"),
    ];

    public static IServiceCollection AddSparkForwardedHeaders(this IServiceCollection services)
    {
        services.AddSparkConfigurationSection<SparkForwardedHeadersOptions>(SparkForwardedHeadersOptions.SectionName);
        services.AddSingleton<IConfigureOptions<ForwardedHeadersOptions>, ConfigureForwardedHeaders>();
        services.AddTransient<IStartupFilter, StartupFilter>();
        return services;
    }

    /// <summary>
    /// Translates Spark's options onto ASP.NET Core's. Registered as an
    /// <see cref="IConfigureOptions{TOptions}"/> so an application's own
    /// <c>Configure&lt;ForwardedHeadersOptions&gt;</c> still runs after it — and is then checked
    /// by <see cref="Validate"/> like everything else.
    /// </summary>
    private sealed class ConfigureForwardedHeaders(IOptions<SparkForwardedHeadersOptions> spark, IServiceProvider services)
        : IConfigureOptions<ForwardedHeadersOptions>
    {
        public void Configure(ForwardedHeadersOptions options)
            => Apply(spark.Value, services.GetService<IConfiguration>()?["AllowedHosts"], options);
    }

    /// <summary>The pure translation, separated so it can be tested without a host.</summary>
    internal static void Apply(SparkForwardedHeadersOptions spark, string? allowedHosts, ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // ASP.NET Core's own defaults (127.0.0.0/8 and ::1) are replaced, never merged, so the
        // effective list is exactly the one logged at startup.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        if (spark.KnownNetworks is null && spark.KnownProxies is null)
        {
            foreach (var network in DefaultNetworks)
                options.KnownIPNetworks.Add(network);
        }
        else
        {
            foreach (var network in spark.KnownNetworks ?? [])
                options.KnownIPNetworks.Add(ParseNetwork(network));

            foreach (var proxy in spark.KnownProxies ?? [])
                options.KnownProxies.Add(ParseAddress(proxy));
        }

        // Never null: an unlimited ForwardLimit reads every entry a client wrote.
        options.ForwardLimit = spark.ProxyHops;

        if (spark.ForwardHost)
        {
            options.ForwardedHeaders |= ForwardedHeaders.XForwardedHost;
            options.AllowedHosts = SplitHosts(allowedHosts);
        }
    }

    /// <summary>
    /// Refuses a configuration that would let a caller choose its own address or host. Throws
    /// <see cref="InvalidOperationException"/> — a startup error, not a request-time one.
    /// </summary>
    internal static void Validate(
        SparkForwardedHeadersOptions spark, ForwardedHeadersOptions effective, string? allowedHosts, IHostEnvironment environment)
    {
        if (spark.ProxyHops < 1)
        {
            throw new InvalidOperationException(
                $"Spark:ForwardedHeaders:ProxyHops is {spark.ProxyHops}. It is the number of proxies in "
                + "front of this application and must be at least 1; set it to the real hop count.");
        }

        if (effective.ForwardLimit is null)
        {
            throw new InvalidOperationException(
                "ForwardedHeadersOptions.ForwardLimit was set to null after Spark configured it. An "
                + "unlimited limit reads every X-Forwarded-For entry a client wrote, so the caller "
                + "chooses its own address. Set Spark:ForwardedHeaders:ProxyHops instead.");
        }

        if (effective.KnownIPNetworks.Count == 0 && effective.KnownProxies.Count == 0 && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "The forwarded-headers configuration trusts every sender: KnownNetworks and KnownProxies "
                + "are both empty, which ASP.NET Core treats as 'no validation', so X-Forwarded-For is "
                + "taken from whoever sends it and every rate limiter partitions on an address the caller "
                + "chose. Name the proxies in Spark:ForwardedHeaders:KnownNetworks / KnownProxies, or "
                + "leave both unset for the private-range default. (Clearing the lists in "
                + "Configure<ForwardedHeadersOptions> has the same effect — remove that code.)");
        }

        if ((effective.ForwardedHeaders & ForwardedHeaders.XForwardedHost) != 0)
        {
            var hosts = SplitHosts(allowedHosts);
            if (hosts.Count == 0 || hosts.Contains("*"))
            {
                throw new InvalidOperationException(
                    "X-Forwarded-Host is honoured (Spark:ForwardedHeaders:ForwardHost), but AllowedHosts "
                    + $"is '{allowedHosts ?? "(unset)"}'. A forwarded host feeds every absolute URL the "
                    + "application builds, so a caller could choose the host in a password-reset link. "
                    + "Set AllowedHosts to the host names this application serves (separated by ';'), or "
                    + "turn ForwardHost off — most proxies pass the original Host header through anyway.");
            }
        }
    }

    /// <summary>The effective trust list, as logged at startup.</summary>
    internal static string DescribeTrust(ForwardedHeadersOptions effective)
    {
        var entries = effective.KnownIPNetworks.Select(n => n.ToString())
            .Concat(effective.KnownProxies.Select(p => p.ToString()))
            .ToList();

        return entries.Count == 0 ? "EVERY sender" : string.Join(", ", entries);
    }

    private static List<string> SplitHosts(string? allowedHosts)
        => (allowedHosts ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static System.Net.IPNetwork ParseNetwork(string value)
        => System.Net.IPNetwork.TryParse(value, out var network)
            ? network
            : throw new InvalidOperationException(
                $"Spark:ForwardedHeaders:KnownNetworks contains '{value}', which is not a network in CIDR "
                + "form (for example '10.0.0.0/8').");

    private static IPAddress ParseAddress(string value)
        => IPAddress.TryParse(value, out var address)
            ? address
            : throw new InvalidOperationException(
                $"Spark:ForwardedHeaders:KnownProxies contains '{value}', which is not an IP address.");

    private sealed class StartupFilter(
        IOptions<SparkForwardedHeadersOptions> spark,
        IOptions<ForwardedHeadersOptions> effective,
        IHostEnvironment environment,
        IServiceProvider services,
        ILogger<SparkForwardedHeadersOptions> logger) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            var options = effective.Value;
            Validate(spark.Value, options, services.GetService<IConfiguration>()?["AllowedHosts"], environment);

            if (options.KnownIPNetworks.Count == 0 && options.KnownProxies.Count == 0)
            {
                logger.LogWarning(
                    "Forwarded headers are accepted from EVERY sender (Development only — this refuses to start elsewhere).");
            }
            else
            {
                logger.LogInformation(
                    "Forwarded headers ({Headers}) are trusted from {Trust}, {Hops} hop(s).",
                    options.ForwardedHeaders, DescribeTrust(options), options.ForwardLimit);
            }

            app.UseForwardedHeaders();
            next(app);
        };
    }
}
