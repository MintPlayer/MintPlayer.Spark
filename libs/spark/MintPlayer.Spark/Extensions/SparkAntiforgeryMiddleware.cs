using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions.Authentication;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Spark's antiforgery (CSRF) gate — the piece of <c>UseSpark()</c> that decides whether a mutating
/// request must present a valid antiforgery token.
/// <para>
/// It runs <em>before</em> the built-in <c>UseAntiforgery()</c> so it can call
/// <see cref="IAntiforgery.ValidateRequestAsync"/> before <c>FormFeature</c>'s "unvalidated" guard is
/// set, and it records the outcome in <see cref="IAntiforgeryValidationFeature"/> so the built-in
/// middleware and <c>EndpointMiddleware</c> both treat the request as already checked. The built-in
/// middleware was narrowed in 8.0.1 to validate form-content bodies only, so a JSON API is not
/// protected by it at all — closing that gap is why this exists.
/// </para>
/// </summary>
internal static class SparkAntiforgeryMiddleware
{
    /// <summary>
    /// Registers the gate. Reads <see cref="SparkAntiforgeryOptions"/> once, at pipeline-build time:
    /// the options cannot change after startup, and normalizing prefixes per request would be pure
    /// waste.
    /// </summary>
    public static IApplicationBuilder UseSparkAntiforgery(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetService<SparkAntiforgeryOptions>()
            ?? new SparkAntiforgeryOptions();
        var prefixes = SparkBuilderAntiforgeryExtensions.NormalizePrefixes(options.PathPrefixes);
        var logger = app.ApplicationServices.GetService<ILoggerFactory>()
            ?.CreateLogger("MintPlayer.Spark.Antiforgery");

        return app.Use(async (context, next) =>
        {
            var metadata = context.GetEndpoint()?.Metadata.GetMetadata<IAntiforgeryMetadata>();

            // Explicit metadata wins in BOTH directions; only its absence consults the default. That
            // keeps DisableAntiforgery() an escape hatch rather than a suggestion, and leaves every
            // endpoint that already opted in behaving exactly as it did.
            var requiresValidation = metadata switch
            {
                { RequiresValidation: true } => true,
                { RequiresValidation: false } => false,

                // The ambient-credential test belongs to THIS branch only. An endpoint that asked
                // for the check explicitly keeps getting it even from an anonymous caller —
                // /spark/auth/login is exactly that shape, and login CSRF is a real attack.
                null => options.RequireAntiforgery
                        && SparkBuilderAntiforgeryExtensions.MatchesAnyPrefix(context.Request.Path, prefixes)
                        && HasAmbientCredential(context),
            };

            if (requiresValidation
                && IsMutatingMethod(context.Request.Method)
                && !IsNonAmbientCredential(context))
            {
                var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
                try
                {
                    await antiforgery.ValidateRequestAsync(context);
                    context.Features.Set<IAntiforgeryValidationFeature>(
                        new SparkAntiforgeryValidationFeature(isValid: true));
                }
                catch (AntiforgeryValidationException ex)
                {
                    context.Features.Set<IAntiforgeryValidationFeature>(
                        new SparkAntiforgeryValidationFeature(isValid: false, error: ex));

                    // Warning-only is the migration path onto the inverted default: an app sees its
                    // whole affected surface in one deploy instead of one production 400 at a time.
                    // It never covers an endpoint that asked for the check explicitly — that request
                    // was already being rejected before this option existed, and letting it through
                    // would be a regression dressed up as a migration aid.
                    if (options.WarnOnly && metadata is null)
                    {
                        logger?.LogWarning(
                            "Antiforgery validation would have rejected {Method} {Path}, but "
                            + "SparkAntiforgeryOptions.WarnOnly is on so it was allowed. The caller "
                            + "sent no valid antiforgery token; a browser client must echo the "
                            + "XSRF-TOKEN cookie in the X-XSRF-TOKEN header.",
                            context.Request.Method, context.Request.Path);
                    }
                    else
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        return;
                    }
                }
            }

            await next(context);
        });
    }

    private static bool IsMutatingMethod(string method) =>
        HttpMethods.IsPost(method)
        || HttpMethods.IsPut(method)
        || HttpMethods.IsDelete(method)
        || HttpMethods.IsPatch(method);

    /// <summary>
    /// True when the request was authenticated by a credential the browser does not attach on its
    /// own — a bearer token, a client certificate, an API key.
    /// <para>
    /// CSRF is an attack on <i>ambient</i> authority: it works because a cross-site page can make
    /// the browser replay a cookie it is holding. A caller that had to construct its own
    /// <c>Authorization</c> header, or complete a TLS handshake with a private key, cannot be made
    /// to do either by a third-party page. Demanding an antiforgery token of such a caller protects
    /// nothing and makes external POSTs impossible — a CI job has no <c>XSRF-TOKEN</c> cookie to
    /// echo, so it got a bare 400 with no body (F8).
    /// </para>
    /// <para>
    /// The decision reads the scheme that actually produced <c>HttpContext.User</c>, not the shape
    /// of the request. That distinction is the security property: were this keyed on "did the caller
    /// send an <c>Authorization</c> header", an attacker could disable the check on a
    /// cookie-authenticated victim by attaching a junk header. A junk header authenticates nothing,
    /// so no scheme records itself here and the gate still runs.
    /// </para>
    /// </summary>
    private static bool IsNonAmbientCredential(HttpContext context)
        => context.Features.Get<ISparkAuthenticatedSchemeFeature>() is { Scheme.IsAmbient: false };

    /// <summary>
    /// True when the request carries a credential the browser attaches on its own — the only kind
    /// CSRF can abuse.
    /// <para>
    /// This is <em>not</em> the complement of <see cref="IsNonAmbientCredential"/>: a request with no
    /// credential at all is neither. Demanding a token of a genuinely anonymous <c>POST</c> — a
    /// webhook receiver, a public contact form — protects nothing while breaking every non-browser
    /// caller, so the inverted default asks for ambient authority positively.
    /// </para>
    /// <para>
    /// Read from the authenticated principal rather than from a cookie header. A cookie the app
    /// never authenticated with confers no authority, and keying on the header would let an attacker
    /// turn the check off by attaching junk.
    /// </para>
    /// </summary>
    private static bool HasAmbientCredential(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return false;

        // A scheme that recorded itself is authoritative about its own shape. Nothing recorded means
        // the principal came from somewhere Spark did not wire — ASP.NET Core Identity's own cookie
        // handler, say — and cookies are overwhelmingly the common such case, so treat it as ambient
        // rather than skip the check.
        return context.Features.Get<ISparkAuthenticatedSchemeFeature>() is not { Scheme.IsAmbient: false };
    }

    /// <summary>
    /// Spark's implementation of <see cref="IAntiforgeryValidationFeature"/>, recording the outcome
    /// of <see cref="IAntiforgery.ValidateRequestAsync"/>. The concrete class in
    /// <c>Microsoft.AspNetCore.Antiforgery</c> is internal, so we provide our own.
    /// </summary>
    private sealed class SparkAntiforgeryValidationFeature(
        bool isValid, AntiforgeryValidationException? error = null)
        : IAntiforgeryValidationFeature
    {
        public bool IsValid { get; } = isValid;
        public Exception? Error { get; } = error;
    }
}
