using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Optional rate-limiter wiring for Spark-powered apps. Security audit finding L-3 keeps
/// the framework itself out of rate-limiting policy — apps opt in. This extension hangs off
/// <see cref="ISparkBuilder"/> so it composes with the rest of the Spark builder surface:
/// <code>
/// builder.Services.AddSpark(spark => spark.AddRateLimiter());
/// </code>
/// or via <c>SparkFullOptions.RateLimiter</c> for AddSparkFull consumers.
///
/// The limiter keeps two budgets per request (commerce PRD X10): one per caller — signed-in user, else visitor
/// cookie, else network address — and a higher ceiling per network address shared by every caller behind it.
/// An address is an IPv4 address or an IPv6 /64. It is scoped to
/// <see cref="SparkRateLimiterOptions.PathPrefixes"/> — by default <c>/spark</c> and <c>/connect</c>,
/// so static assets and app-specific routes remain unthrottled. Over-limit requests are rejected
/// with HTTP 429.
/// </summary>
public static class SparkBuilderRateLimiterExtensions
{
    private const string ConfigurationSection = "Spark:RateLimiter";

    /// <summary>
    /// Registers a fixed-window rate limiter for Spark endpoints. Calling with no configurator
    /// uses the documented defaults (<see cref="SparkRateLimiterOptions"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement.</b> The middleware is registered at
    /// <see cref="SparkMiddlewareStage.BeforeAuthentication"/>, so it runs immediately after the app's
    /// <c>UseRouting()</c> and <em>ahead of</em> <c>UseAuthentication()</c>. A flood is therefore
    /// rejected before any credential is validated — which matters most for an authenticated ingest
    /// endpoint whose authentication costs a database lookup, where a limiter placed behind
    /// authentication only protects the app from load it has already absorbed the expensive part of.
    /// Routing has already run at that point, so endpoint-attached <c>[EnableRateLimiting]</c> /
    /// <c>[DisableRateLimiting]</c> policies still resolve normally.
    /// </para>
    /// <para>
    /// <b>One exception, by necessity.</b> A request that presents a credential (an <c>Authorization</c>
    /// header, a client certificate, an authentication cookie) is one caller whose identity is known only
    /// after authentication. Ahead of it, such a request spends only the address ceiling
    /// (<see cref="SparkRateLimiterOptions.PermitLimitPerAddress"/>); its caller budget is taken at
    /// <see cref="SparkMiddlewareStage.AfterSpark"/>, keyed on the user it turned out to be. A request with no
    /// credential spends both budgets ahead of authentication.
    /// </para>
    /// <para>
    /// <b>Do not also call <c>app.UseRateLimiter()</c>.</b> This is not a style preference — it
    /// silently halves the configured budget. <c>UseRateLimiter</c> has no idempotence marker (unlike
    /// <c>UseRouting</c>, it does not detect a previous registration and return early), and
    /// <c>RateLimitingMiddleware</c> records nothing on the request to say it already ran. Two
    /// registrations therefore means <b>every request acquires two leases from the same partition</b>,
    /// so an app configured for 150 requests per window gets 75, with no error and no log entry. The
    /// only symptom is 429s arriving about twice as often as expected, which reads as a bad traffic
    /// estimate rather than a duplicated middleware.
    /// </para>
    /// <para>
    /// Spark cannot detect this and warn: a manual <c>UseRateLimiter()</c> is a call on the caller's
    /// own <see cref="IApplicationBuilder"/>, invisible at startup, and the middleware leaves no
    /// runtime trace to compare against. To meter additional routes, add them to
    /// <see cref="SparkRateLimiterOptions.PathPrefixes"/>; for a different budget on one route, attach
    /// a named ASP.NET policy to that endpoint. Both compose with this limiter; a second
    /// <c>UseRateLimiter()</c> does not.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <see cref="SparkRateLimiterOptions.PathPrefixes"/> names no usable prefix — it is empty, holds
    /// only blank entries, or holds only a bare <c>"/"</c>. A limiter that meters nothing is a security
    /// control that silently does nothing, so this fails at startup rather than at the first flood.
    /// The <c>"/"</c> case reports itself distinctly: it is a request to meter <em>everything</em>, not
    /// an empty configuration, and is refused for that reason rather than treated as absent.
    /// </exception>
    public static ISparkBuilder AddRateLimiter(
        this ISparkBuilder builder,
        Action<SparkRateLimiterOptions>? configure = null)
    {
        // Configuration first, then code — the order AddReplication and AddMessaging already use, so
        // an operator can retune a budget per environment without a redeploy.
        //
        // ⚠ Code therefore WINS. An app that sets PermitLimit in its configurator cannot be
        // overridden from appsettings, and the override fails silently rather than loudly. The E2E
        // test host depends on this: Fleet passes `_ => { }`, which sets nothing, so the configured
        // PermitLimit survives. Adding an assignment to that lambda would switch the E2E suite back
        // to the default budget with no test announcing it.
        //
        // Superseding issue_265_PRD.md D5, which deferred rather than forbade this: "string[] is the
        // shape that would bind cleanly if that changes later."
        var section = builder.Configuration?.GetSection(ConfigurationSection);

        var options = new SparkRateLimiterOptions();
        section?.Bind(options);
        configure?.Invoke(options);

        // Normalized once here rather than per request: the prefixes cannot change after startup,
        // and StartsWithSegments wants a leading slash and no trailing one. Making the caller know
        // that would be a trap, not an interface.
        var prefixes = NormalizePrefixes(options.PathPrefixes);

        var perAddress = options.PermitLimitPerAddress ?? (int)Math.Min(int.MaxValue, options.PermitLimit * 10L);
        if (options.PermitLimit < 1 || perAddress < options.PermitLimit)
            throw new ArgumentException(
                $"{nameof(SparkRateLimiterOptions)}.{nameof(SparkRateLimiterOptions.PermitLimit)} must be at least 1, and "
                + $"{nameof(SparkRateLimiterOptions.PermitLimitPerAddress)} at least {nameof(SparkRateLimiterOptions.PermitLimit)}: "
                + "the address ceiling is shared by every caller behind one address.",
                nameof(SparkRateLimiterOptions.PermitLimitPerAddress));

        FixedWindowRateLimiterOptions Budget(int permits) => new()
        {
            PermitLimit = permits,
            Window = options.Window,
            QueueLimit = 0,
        };

        // Two budgets per request (commerce PRD X10), chained, so a request needs a permit from both:
        //   * the CALLER's — per signed-in user, else per visitor cookie, else per address — so a hundred
        //     fans behind one carrier NAT are not one caller;
        //   * the ADDRESS ceiling (PermitLimitPerAddress), shared by every caller behind one address, which
        //     bounds what one address can do with many visitor cookies or accounts.
        // An address is an IPv4 address or an IPv6 /64 (F5): one IPv6 host owns a whole /64 and rotates the low
        // half by itself, so the exact address was a fresh budget for the asking.
        builder.Services.AddRateLimiter(rl =>
        {
            rl.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                    SparkRateLimitIdentity.Of(httpContext) is { Metered: true, CallerKey: { } callerKey }
                        ? RateLimitPartition.GetFixedWindowLimiter("caller|" + callerKey, _ => Budget(options.PermitLimit))
                        : RateLimitPartition.GetNoLimiter("no-limit")),
                PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                    SparkRateLimitIdentity.Of(httpContext) is { Metered: true } identity
                        ? RateLimitPartition.GetFixedWindowLimiter("address|" + identity.AddressKey, _ => Budget(perAddress))
                        : RateLimitPartition.GetNoLimiter("no-limit")));
            rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        // Ahead of authentication: a flood is refused before any credential is validated (see the remarks).
        builder.Registry.AddMiddleware(
            app =>
            {
                app.Use(SparkRateLimitIdentity.Classify(prefixes));
                app.UseRateLimiter();
            },
            SparkMiddlewareStage.BeforeAuthentication);

        // A request that presents a credential is the one caller budget that cannot be told apart before
        // authentication: its user is known only once the credential is validated. Its caller budget is taken
        // here instead, after authentication, keyed on the user it turned out to be (or on the visitor or the
        // address when the credential did not authenticate). The address ceiling above already applied to it,
        // so a junk credential buys at most the ceiling's worth of authentication work.
        var callerLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                "caller|" + Visitor.SparkVisitorFeature.For(httpContext).IdentityKey,
                _ => Budget(options.PermitLimit)));
        builder.Registry.AddMiddleware(
            app => app.Use(async (context, next) =>
            {
                if (SparkRateLimitIdentity.Of(context) is { Metered: true, CallerKey: null }
                    && context.GetEndpoint()?.Metadata.GetMetadata<DisableRateLimitingAttribute>() is null)
                {
                    using var lease = callerLimiter.AttemptAcquire(context);
                    if (!lease.IsAcquired)
                    {
                        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                        return;
                    }
                    await next(context);
                    return;
                }
                await next(context);
            }),
            SparkMiddlewareStage.AfterSpark);

        return builder;
    }

    internal static bool MatchesAnyPrefix(PathString path, PathString[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (path.StartsWithSegments(prefix))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Accepts prefixes in whatever shape the caller wrote them — <c>"api/browse"</c>,
    /// <c>"/api/browse"</c>, <c>"/api/browse/"</c> — and yields the single form
    /// <see cref="PathString.StartsWithSegments(PathString)"/> matches on.
    /// </summary>
    private static PathString[] NormalizePrefixes(string[]? configured)
    {
        var normalized = new List<PathString>();
        var sawRoot = false;

        foreach (var raw in configured ?? [])
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            trimmed = trimmed.TrimEnd('/');
            if (trimmed.Length == 0)
            {
                // A bare "/" is not "no prefix" and must not be reported as one: it is a request to
                // meter every request in the app. Refused rather than honoured, and refused with its
                // own message — telling someone who wrote exactly one prefix that they named none is
                // a worse error than the one they made.
                sawRoot = true;
                continue;
            }

            if (trimmed[0] != '/')
                trimmed = "/" + trimmed;

            var candidate = new PathString(trimmed);
            if (!normalized.Contains(candidate))
                normalized.Add(candidate);
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException(
                sawRoot
                    ? "\"/\" is not accepted as a rate-limiter path prefix: it would meter every "
                      + "request, including static assets and SPA bundles, which starves browser "
                      + "asset loads rather than protecting an endpoint. Name the prefixes to meter "
                      + $"instead — e.g. {nameof(SparkRateLimiterOptions.PathPrefixes)} = "
                      + "[\"/api\"] for an API-only app."
                    : $"{nameof(SparkRateLimiterOptions)}."
                      + $"{nameof(SparkRateLimiterOptions.PathPrefixes)} must name at least one path "
                      + "prefix. A rate limiter scoped to no paths meters no requests, which would "
                      + "leave the app unprotected with nothing to indicate it. Leave the property at "
                      + "its default (\"/spark\", \"/connect\") or list the prefixes to meter.",
                nameof(SparkRateLimiterOptions.PathPrefixes));
        }

        return [.. normalized];
    }
}
