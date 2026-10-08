using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace MintPlayer.Spark.Authorization.ResourceServer;

/// <summary>Options of the introspection scheme; filled from <see cref="SparkResourceServerOptions"/>.</summary>
internal sealed class SparkIntrospectionOptions : AuthenticationSchemeOptions
{
    public SparkResourceServerOptions ResourceServer { get; set; } = new();
}

/// <summary>
/// Validates access tokens by asking the issuer's introspection endpoint (RFC 7662;
/// <c>docs/identity_provider_platform_PRD.md</c> D8, I12) instead of checking the JWT locally, so a revoked token,
/// or one of a disabled application, stops working at once.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The endpoint comes from the issuer's discovery document; the resource server authenticates with its own
/// client (<c>client_secret_basic</c>).</item>
/// <item>The answer must be <c>active</c> and its <c>aud</c> must include <see cref="SparkResourceServerOptions.Audience"/>.</item>
/// <item>Active answers are cached by the token's hash for <see cref="SparkResourceServerOptions.IntrospectionCacheDuration"/>,
/// never beyond the token's <c>exp</c>. Inactive ones are not cached, so a token cannot be locked out by guessing.</item>
/// <item>Claims are the introspection members verbatim (<c>sub</c>, <c>scope</c>, <c>client_id</c>, <c>group</c>, <c>cnf</c>…),
/// so <c>RequireScope</c>, group membership and proof of possession read them exactly as from a JWT.</item>
/// </list>
/// </remarks>
internal sealed class SparkIntrospectionHandler(
    IOptionsMonitor<SparkIntrospectionOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHttpClientFactory httpClients,
    IMemoryCache cache)
    : AuthenticationHandler<SparkIntrospectionOptions>(options, logger, encoder)
{
    private static readonly Dictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> Discovery = [];

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = SparkProofOfPossession.ReadToken(Context);
        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();

        var rs = Options.ResourceServer;
        var cacheKey = "spark-introspection:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        if (!cache.TryGetValue(cacheKey, out ClaimsPrincipal? principal) || principal is null)
        {
            var (introspected, error) = await IntrospectAsync(rs, token);
            if (introspected is null)
                return AuthenticateResult.Fail(error ?? "The token is not active.");
            principal = introspected;

            var lifetime = rs.IntrospectionCacheDuration;
            if (long.TryParse(principal.FindFirst("exp")?.Value, out var exp))
            {
                var untilExpiry = DateTimeOffset.FromUnixTimeSeconds(exp) - DateTimeOffset.UtcNow;
                if (untilExpiry < lifetime) lifetime = untilExpiry;
            }
            if (lifetime > TimeSpan.Zero)
                cache.Set(cacheKey, principal, lifetime);
        }

        var possession = await SparkProofOfPossession.ValidateAsync(Context, principal, token, cache);
        if (possession is not null)
            return AuthenticateResult.Fail(possession);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    private async Task<(ClaimsPrincipal? Principal, string? Error)> IntrospectAsync(SparkResourceServerOptions rs, string token)
    {
        var configuration = await DiscoveryFor(rs).GetConfigurationAsync(Context.RequestAborted);
        if (string.IsNullOrEmpty(configuration.IntrospectionEndpoint))
            return (null, "The issuer advertises no introspection endpoint.");

        using var request = new HttpRequestMessage(HttpMethod.Post, configuration.IntrospectionEndpoint)
        {
            Content = new FormUrlEncodedContent([new("token", token), new("token_type_hint", "access_token")]),
        };
        // RFC 6749 §2.3.1: each half form-url-encoded before joining.
        var credentials = $"{Uri.EscapeDataString(rs.IntrospectionClientId!)}:{Uri.EscapeDataString(rs.IntrospectionClientSecret!)}";
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));

        using var response = await httpClients.CreateClient(nameof(SparkIntrospectionHandler)).SendAsync(request, Context.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("Introspection at {Endpoint} answered {Status}.", configuration.IntrospectionEndpoint, (int)response.StatusCode);
            return (null, "The issuer did not introspect the token.");
        }

        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Context.RequestAborted), cancellationToken: Context.RequestAborted);
        var root = body.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.True)
            return (null, "The token is not active.");

        var claims = new List<Claim>();
        foreach (var member in root.EnumerateObject())
        {
            if (member.Name == "active") continue;
            switch (member.Value.ValueKind)
            {
                case JsonValueKind.String:
                    claims.Add(new Claim(member.Name, member.Value.GetString()!));
                    break;
                case JsonValueKind.Array:
                    foreach (var item in member.Value.EnumerateArray())
                        claims.Add(item.ValueKind == JsonValueKind.String
                            ? new Claim(member.Name, item.GetString()!)
                            : new Claim(member.Name, item.GetRawText(), JsonClaimValueTypes.Json));
                    break;
                case JsonValueKind.Object:
                    claims.Add(new Claim(member.Name, member.Value.GetRawText(), JsonClaimValueTypes.Json));
                    break;
                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    claims.Add(new Claim(member.Name, member.Value.GetRawText()));
                    break;
            }
        }

        if (!claims.Any(c => c.Type == "aud" && string.Equals(c.Value, rs.Audience, StringComparison.Ordinal)))
            return (null, "The token is not for this audience.");
        if (!claims.Any(c => c.Type == "iss" && string.Equals(c.Value.TrimEnd('/'), rs.Authority.TrimEnd('/'), StringComparison.Ordinal)))
            return (null, "The token is from another issuer.");

        return (new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, "sub", "role")), null);
    }

    private static ConfigurationManager<OpenIdConnectConfiguration> DiscoveryFor(SparkResourceServerOptions rs)
    {
        lock (Discovery)
        {
            var authority = rs.Authority.TrimEnd('/');
            if (!Discovery.TryGetValue(authority, out var manager))
            {
                manager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    authority + "/.well-known/openid-configuration",
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever { RequireHttps = rs.RequireHttpsMetadata });
                Discovery[authority] = manager;
            }
            return manager;
        }
    }
}
