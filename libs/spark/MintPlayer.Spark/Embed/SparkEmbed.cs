using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions.Builder;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Embed;

/// <summary>
/// Embed mode (commerce PRD X23, GB8): the framing policy per route, the origin rules and the startup check.
/// </summary>
/// <remarks>
/// <para>
/// The framing header is set by an <see cref="IStartupFilter"/> that wraps the whole pipeline, as page meta does
/// (<see cref="PageMeta.SparkPageMetaInjection"/>): the SPA's <c>index.html</c> is written by the static-file middleware
/// or the dev-server proxy, outside <c>UseSpark()</c>.
/// </para>
/// <para>
/// <c>frame-ancestors</c> (CSP Level 2) is the standard control; <c>X-Frame-Options</c> cannot list origins, so it is
/// removed from an embed route's response, where the CSP takes over.
/// </para>
/// </remarks>
internal static partial class SparkEmbed
{
    internal static IServiceCollection AddSparkEmbed(IServiceCollection services)
    {
        services.AddSparkConfigurationSection<SparkEmbedOptions>();
        services.AddTransient<IStartupFilter, StartupFilter>();
        services.AddScoped<SparkEmbedHandoverService>();
        return services;
    }

    /// <summary>Refuses malformed origins and prefixes at startup (<c>UseSpark()</c>).</summary>
    internal static void ValidateOptions(IServiceProvider services)
    {
        var options = services.GetService<IOptions<SparkEmbedOptions>>()?.Value;
        if (options is not { Enabled: true })
            return;
        var errors = Validate(options);
        if (errors.Count > 0)
            throw new InvalidOperationException("Spark:Embed is invalid: " + string.Join("; ", errors) + " (commerce PRD X23).");
    }

    internal static List<string> Validate(SparkEmbedOptions options)
    {
        var errors = new List<string>();
        foreach (var origin in options.AllowedOrigins)
            if (!IsValidOrigin(origin))
                errors.Add($"'{origin}' is not an origin (https://host[:port], https://*.host[:port], or http://localhost[:port])");
        foreach (var prefix in options.PathPrefixes)
            if (!PathPrefix().IsMatch(prefix ?? ""))
                errors.Add($"'{prefix}' is not a path prefix (/segment[/segment…])");
        if (options.PathPrefixes.Count == 0)
            errors.Add("no path prefix: nothing could be embedded");
        return errors;
    }

    internal static bool IsValidOrigin(string? origin)
        => origin is not null && (HttpsOrigin().IsMatch(origin) || LocalhostOrigin().IsMatch(origin));

    /// <summary>
    /// Whether <paramref name="origin"/> (as a browser sends it: <c>scheme://host[:port]</c>) is one of
    /// <paramref name="allowed"/>. A wildcard entry matches subdomains only, not the bare domain (as in CSP).
    /// </summary>
    internal static bool IsAllowedOrigin(string? origin, IEnumerable<string> allowed)
    {
        if (origin is null || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query))
            return false;
        foreach (var entry in allowed)
        {
            if (!IsValidOrigin(entry))
                continue;
            var wildcard = entry.Contains("://*.", StringComparison.Ordinal);
            var pattern = new Uri(wildcard ? entry.Replace("://*.", "://wildcard.", StringComparison.Ordinal) : entry);
            if (!string.Equals(uri.Scheme, pattern.Scheme, StringComparison.OrdinalIgnoreCase) || uri.Port != pattern.Port)
                continue;
            if (wildcard)
            {
                var suffix = "." + pattern.Host["wildcard.".Length..];
                if (uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && uri.Host.Length > suffix.Length)
                    return true;
            }
            else if (string.Equals(uri.Host, pattern.Host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The <c>frame-ancestors</c> directive for <paramref name="path"/>, or <see langword="null"/> to leave the response alone.</summary>
    internal static string? FrameAncestors(SparkEmbedOptions options, PathString path)
    {
        if (!options.Enabled)
            return null;
        if (IsEmbedPath(options, path))
        {
            var sources = options.AllowedOrigins.Where(IsValidOrigin).Select(o => o.ToLowerInvariant());
            return "frame-ancestors " + string.Join(' ', sources.Prepend("'self'"));
        }
        return options.DenyFramingElsewhere ? "frame-ancestors 'self'" : null;
    }

    internal static bool IsEmbedPath(SparkEmbedOptions options, PathString path)
        => options.PathPrefixes.Any(p => PathPrefix().IsMatch(p ?? "") && path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>The middleware: adds the directive to HTML responses when they start.</summary>
    internal static Task Invoke(HttpContext context, Func<Task> next)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<SparkEmbedOptions>>().Value;
        if (options.Enabled)
        {
            context.Response.OnStarting(() =>
            {
                var response = context.Response;
                if (response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true
                    && FrameAncestors(options, context.Request.Path) is { } directive)
                {
                    // Appended, never replacing the app's own CSP: several policies are all enforced.
                    response.Headers.Append("Content-Security-Policy", directive);
                    if (IsEmbedPath(options, context.Request.Path))
                        response.Headers.Remove("X-Frame-Options");
                }
                return Task.CompletedTask;
            });
        }
        return next();
    }

    [GeneratedRegex(@"^https://(\*\.)?([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)*[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(:\d{1,5})?$", RegexOptions.IgnoreCase)]
    private static partial Regex HttpsOrigin();

    [GeneratedRegex(@"^http://(localhost|127\.0\.0\.1)(:\d{1,5})?$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalhostOrigin();

    [GeneratedRegex(@"^(/[A-Za-z0-9._~-]+)+$")]
    private static partial Regex PathPrefix();

    private sealed class StartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) => Invoke(context, () => nextMiddleware(context)));
            next(app);
        };
    }
}
