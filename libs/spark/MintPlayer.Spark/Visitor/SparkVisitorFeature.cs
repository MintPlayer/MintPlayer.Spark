using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Visitor;

namespace MintPlayer.Spark.Visitor;

/// <summary>
/// The request's <see cref="ISparkVisitor"/>: the visitor cookie, unprotected once per request, plus the
/// identity the request is otherwise known by.
/// </summary>
/// <remarks>
/// <para>
/// Created on first use (<see cref="For"/>), so it is the same object for the rate limiter ahead of
/// authentication, <c>UseSparkVisitor</c>, the antiforgery gate and the endpoint. <see cref="UserId"/> and
/// <see cref="IdentityKey"/> are read from <see cref="HttpContext.User"/> each time: ahead of authentication
/// they say "visitor" or "ip", after it they say "user".
/// </para>
/// <para>
/// Payload <c>{"v":"&lt;guid&gt;","iat":&lt;unix seconds&gt;}</c> through the Data Protection purpose
/// <see cref="Purpose"/>. A cookie that does not unprotect, does not parse or is older than
/// <see cref="SparkVisitorOptions.Lifetime"/> is treated as absent, never as an error.
/// </para>
/// </remarks>
internal sealed class SparkVisitorFeature : ISparkVisitor
{
    /// <summary>The Data Protection purpose of the cookie. A new version gets a new purpose, which drops every old cookie.</summary>
    internal const string Purpose = "MintPlayer.Spark.Visitor.v1";

    private readonly HttpContext context;
    private readonly IDataProtector? protector;
    private readonly SparkVisitorOptions options;
    private readonly TimeProvider time;
    private string? visitorId;

    private SparkVisitorFeature(HttpContext context, IDataProtector? protector, SparkVisitorOptions options, TimeProvider time)
    {
        this.context = context;
        this.protector = protector;
        this.options = options;
        this.time = time;
        visitorId = Read();
    }

    /// <summary>The request's visitor feature, created (and the cookie read) on first use.</summary>
    public static SparkVisitorFeature For(HttpContext context)
    {
        if (context.Features.Get<ISparkVisitor>() is SparkVisitorFeature existing)
            return existing;

        // Optional services: a pipeline built without AddSpark (a unit test of one middleware) has no key ring,
        // and then has no visitors either.
        var services = context.RequestServices;
        var feature = new SparkVisitorFeature(
            context,
            services?.GetService<IDataProtectionProvider>()?.CreateProtector(Purpose),
            services?.GetService<IOptions<SparkVisitorOptions>>()?.Value ?? new SparkVisitorOptions(),
            services?.GetService<TimeProvider>() ?? TimeProvider.System);
        context.Features.Set<ISparkVisitor>(feature);
        return feature;
    }

    public string? VisitorId => visitorId;

    public bool HasVisitorCookie => visitorId is not null;

    public string? UserId
        => context.User.Identity?.IsAuthenticated == true
           && context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
            ? id
            : null;

    public string IdentityKey
        => UserId is { } user ? "user:" + user
            : visitorId is { } visitor ? "visitor:" + visitor
            : "ip:" + SparkClientAddress.Key(context.Connection.RemoteIpAddress);

    public ValueTask<string> EnsureAsync()
    {
        if (visitorId is { } existing)
            return ValueTask.FromResult(existing);

        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            throw new InvalidOperationException(
                "A visitor cookie is issued by an action (POST, PUT, PATCH, DELETE), never by a read: it is a strictly "
                + "necessary cookie, set when the visitor does something that needs it (adding to a basket, joining a queue).");
        if (UserId is not null)
            throw new InvalidOperationException(
                "The request is signed in, so it already has an identity: key the state on ISparkVisitor.UserId "
                + "(or IdentityKey) instead of issuing a visitor cookie.");
        if (context.Response.HasStarted)
            throw new InvalidOperationException("The visitor cookie cannot be issued after the response has started.");
        if (protector is null)
            throw new InvalidOperationException("Visitors need Spark's Data Protection key ring (AddSpark registers it).");

        var id = Guid.NewGuid().ToString("N");
        var now = time.GetUtcNow();
        var payload = JsonSerializer.Serialize(new Payload(id, now.ToUnixTimeSeconds()));
        context.Response.Cookies.Append(options.CookieName, protector.Protect(payload), CookieOptions(now + options.Lifetime));
        visitorId = id;
        return ValueTask.FromResult(id);
    }

    /// <summary>
    /// Deletes the cookie (after a merge into an account) and forgets the visitor for the rest of the request.
    /// A response that has already started keeps the cookie; the next request merges again, which finds the
    /// merge recorded and deletes it then.
    /// </summary>
    public void Forget()
    {
        if (visitorId is null && !context.Request.Cookies.ContainsKey(options.CookieName))
            return;
        visitorId = null;
        if (!context.Response.HasStarted)
            context.Response.Cookies.Delete(options.CookieName, CookieOptions(expires: null));
    }

    /// <summary>
    /// Whether the request's principal came from a credential the browser attaches by itself (a cookie), the
    /// only kind a visitor cookie can travel beside. Same rule as the antiforgery gate: a scheme that recorded
    /// itself decides; nothing recorded is taken as a cookie.
    /// </summary>
    public bool IsAmbientlyAuthenticated
        => context.User.Identity?.IsAuthenticated == true
           && context.Features.Get<ISparkAuthenticatedSchemeFeature>() is not { Scheme.IsAmbient: false };

    private CookieOptions CookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        // Secure whenever the request is, like XSRF-TOKEN: a plain-http development host still works.
        Secure = context.Request.IsHttps,
        // Lax, not Strict: a visitor who follows a link to the shop from elsewhere keeps their basket. Lax
        // still withholds it from a cross-site POST, and antiforgery covers the rest.
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,
        Expires = expires,
    };

    private string? Read()
    {
        if (protector is null || !context.Request.Cookies.TryGetValue(options.CookieName, out var raw) || string.IsNullOrEmpty(raw))
            return null;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(protector.Unprotect(raw));
            if (payload is not { V: { Length: 32 } id } || !id.All(Uri.IsHexDigit))
                return null;
            var issued = DateTimeOffset.FromUnixTimeSeconds(payload.Iat);
            return issued + options.Lifetime < time.GetUtcNow() ? null : id;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentOutOfRangeException)
        {
            // Forged, tampered, from another key ring or another version: no visitor. Never an error.
            return null;
        }
    }

    private sealed record Payload(
        [property: JsonPropertyName("v")] string V,
        [property: JsonPropertyName("iat")] long Iat);
}
