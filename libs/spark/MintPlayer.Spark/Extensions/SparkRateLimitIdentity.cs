using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Visitor;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Who a metered request is, for the rate limiter's two budgets (commerce PRD X10), decided ahead of
/// authentication by <see cref="Classify"/> and kept on the request.
/// </summary>
/// <param name="Metered">Whether the path is under one of the limiter's prefixes.</param>
/// <param name="CallerKey">
/// The caller budget's key when it is known before authentication: <c>visitor:{id}</c> for a valid visitor
/// cookie, else <c>ip:{address}</c>. <see langword="null"/> when the request presents a credential, whose caller
/// budget is taken after authentication, keyed on the user it turns out to be.
/// </param>
/// <param name="AddressKey">The address ceiling's key (<see cref="SparkClientAddress.Key"/>: IPv4, or an IPv6 /64).</param>
internal sealed record SparkRateLimitIdentity(bool Metered, string? CallerKey, string AddressKey)
{
    private static readonly object ItemKey = new();

    /// <summary>The classification <see cref="Classify"/> stored on <paramref name="context"/>, if any.</summary>
    public static SparkRateLimitIdentity? Of(HttpContext context)
        => context.Items.TryGetValue(ItemKey, out var value) ? value as SparkRateLimitIdentity : null;

    /// <summary>
    /// The middleware that classifies a metered request, ahead of <c>UseRateLimiter()</c>. Cheap: a header
    /// check, and a visitor cookie unprotected with the key ring in memory. No credential is validated.
    /// </summary>
    public static Func<HttpContext, RequestDelegate, Task> Classify(PathString[] prefixes)
    {
        string[]? credentialCookies = null;
        return async (context, next) =>
        {
            if (SparkBuilderRateLimiterExtensions.MatchesAnyPrefix(context.Request.Path, prefixes))
            {
                credentialCookies ??= await CredentialCookieNamesAsync(context.RequestServices);
                var address = SparkClientAddress.Key(context.Connection.RemoteIpAddress);
                var caller = PresentsCredential(context, credentialCookies) ? null
                    : SparkVisitorFeature.For(context).VisitorId is { } visitor ? "visitor:" + visitor
                    : "ip:" + address;
                context.Items[ItemKey] = new SparkRateLimitIdentity(true, caller, address);
            }
            await next(context);
        };
    }

    /// <summary>
    /// Whether the request carries something an authentication handler may turn into a user: an
    /// <c>Authorization</c> header, a client certificate, or the cookie of a cookie scheme. Not validated
    /// here; a junk one costs authentication work bounded by the address ceiling, never a fresh caller budget,
    /// because the caller budget is then taken after authentication.
    /// </summary>
    private static bool PresentsCredential(HttpContext context, string[] credentialCookies)
    {
        if (context.Request.Headers.ContainsKey(HeaderNames.Authorization) || context.Connection.ClientCertificate is not null)
            return true;
        foreach (var name in credentialCookies)
        {
            if (context.Request.Cookies.ContainsKey(name))
                return true;
        }
        return false;
    }

    /// <summary>The cookie names of every cookie authentication scheme (Identity's application, external and two-factor cookies).</summary>
    private static async Task<string[]> CredentialCookieNamesAsync(IServiceProvider services)
    {
        // Optional: an app without authentication (DemoApp) registers no scheme provider at all.
        if (services.GetService<IAuthenticationSchemeProvider>() is not { } schemes
            || services.GetService<IOptionsMonitor<CookieAuthenticationOptions>>() is not { } cookieOptions)
            return [];

        var names = new List<string>();
        foreach (var scheme in await schemes.GetAllSchemesAsync())
        {
            if (typeof(CookieAuthenticationHandler).IsAssignableFrom(scheme.HandlerType)
                && cookieOptions.Get(scheme.Name).Cookie.Name is { Length: > 0 } name)
                names.Add(name);
        }
        return [.. names];
    }
}
