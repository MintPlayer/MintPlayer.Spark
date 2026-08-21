using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Opt-in CSRF protection for the endpoints an application writes itself — controllers, minimal-API
/// handlers, Razor Pages — rather than only the ones Spark maps.
/// <code>
/// builder.Services.AddSpark(spark => spark.AddAntiforgeryProtection(a =>
/// {
///     a.PathPrefixes = ["/spark", "/connect", "/api"];
///     a.RequireAntiforgery = true;
/// }));
/// </code>
/// </summary>
public static class SparkBuilderAntiforgeryExtensions
{
    /// <summary>
    /// Configures Spark's antiforgery gate. See <see cref="SparkAntiforgeryOptions"/> for what each
    /// setting does and why the default is off for now.
    /// <para>
    /// Safe to call more than once; the last configurator wins on each property, because a single
    /// options instance is registered and mutated rather than replaced.
    /// </para>
    /// </summary>
    public static ISparkBuilder AddAntiforgeryProtection(
        this ISparkBuilder builder,
        Action<SparkAntiforgeryOptions>? configure = null)
    {
        var options = GetOrAddOptions(builder.Services);
        configure?.Invoke(options);
        return builder;
    }

    /// <summary>
    /// The options instance <c>UseSpark()</c>'s gate reads. Registered on first use so the gate can
    /// always resolve one, and shared across calls so two configurators compose instead of the
    /// second silently discarding the first.
    /// </summary>
    internal static SparkAntiforgeryOptions GetOrAddOptions(IServiceCollection services)
    {
        var existing = services
            .FirstOrDefault(d => d.ServiceType == typeof(SparkAntiforgeryOptions))?
            .ImplementationInstance as SparkAntiforgeryOptions;

        if (existing is not null)
            return existing;

        var options = new SparkAntiforgeryOptions();
        services.AddSingleton(options);
        return options;
    }

    /// <summary>
    /// Accepts prefixes in whatever shape the caller wrote them — <c>"api"</c>, <c>"/api"</c>,
    /// <c>"/api/"</c> — and yields the single form
    /// <see cref="PathString.StartsWithSegments(PathString)"/> matches on. A bare <c>"/"</c> means
    /// every request and is kept as such: unlike the rate limiter, metering everything here costs a
    /// header comparison rather than a shared budget, so an API-only app may legitimately want it.
    /// </summary>
    internal static PathString[] NormalizePrefixes(string[]? configured)
    {
        var normalized = new List<PathString>();

        foreach (var raw in configured ?? [])
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            trimmed = trimmed.TrimEnd('/');
            if (trimmed.Length == 0)
                return [PathString.Empty];   // "/" — everything

            if (trimmed[0] != '/')
                trimmed = "/" + trimmed;

            var candidate = new PathString(trimmed);
            if (!normalized.Contains(candidate))
                normalized.Add(candidate);
        }

        return [.. normalized];
    }

    internal static bool MatchesAnyPrefix(PathString path, PathString[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (!prefix.HasValue || path.StartsWithSegments(prefix))
                return true;
        }

        return false;
    }
}
