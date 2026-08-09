using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>
/// Where tokens come from and who they must be for.
/// </summary>
public sealed class SparkJwtBearerOptions
{
    /// <summary>
    /// The issuer's base URL. Its OpenID discovery document supplies the signing keys, which are
    /// fetched and refreshed automatically — so a key rotation at the provider does not require a
    /// deployment here.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// The audience this application answers to. **Required, deliberately.**
    /// <para>
    /// Skipping audience validation is the classic way a resource server becomes a confused deputy:
    /// every token the issuer ever minted — including ones a client obtained for a completely
    /// different resource — verifies correctly, because the signature is genuine. The audience is
    /// the only part of the token that says the bearer was meant to be talking to *you*.
    /// </para>
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Require HTTPS when fetching discovery metadata. Only turn this off against a local
    /// development issuer.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}

public static class SparkJwtBearerExtensions
{
    /// <summary>The scheme name for tokens validated against an external issuer.</summary>
    public const string Scheme = "Spark:JwtBearer";

    /// <summary>
    /// Accepts OAuth2/OIDC access tokens from <see cref="SparkJwtBearerOptions.Authority"/> as a
    /// Spark credential — the consumer half of <c>client_credentials</c>.
    /// <para>
    /// This is what lets a CI job, a script, or another service call Spark's ordinary endpoints with
    /// a token instead of a session. The issuing half lives in
    /// <c>MintPlayer.Spark.IdentityProvider</c>; nothing connected the two, so a token that package
    /// minted could not authenticate anything.
    /// </para>
    /// <para>
    /// Authorization needs no new concept: group membership is resolved from <c>group</c>/<c>groups</c>
    /// claims, so a client configured with a <c>group</c> claim is governed by the same
    /// <c>security.json</c> as a person. Inbound claim mapping is disabled to keep it that way —
    /// the default renames claims to legacy WS-Federation URIs, which would silently stop
    /// <c>group</c> from being found.
    /// </para>
    /// </summary>
    public static ISparkBuilder AddJwtBearerCredential(
        this ISparkBuilder builder,
        Action<SparkJwtBearerOptions> configure)
    {
        var options = new SparkJwtBearerOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.Authority))
            throw new InvalidOperationException("AddJwtBearerCredential requires an Authority.");

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            throw new InvalidOperationException(
                "AddJwtBearerCredential requires an Audience. Without it this application accepts "
                + "every token the issuer has minted, including ones issued to a client for an "
                + "entirely different resource — the signature is genuine, so nothing else refuses "
                + "them.");
        }

        builder.Services
            .AddAuthentication()
            .AddJwtBearer(Scheme, jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.Audience = options.Audience;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;

                // Keep claim types verbatim. The default inbound mapping rewrites short names to
                // WS-Federation URIs, which would rename "group" out from under
                // ClaimsGroupMembershipProvider and resolve every caller to zero groups — silently,
                // since zero groups is also what an unauthenticated caller has.
                jwt.MapInboundClaims = false;

                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,

                    // The signing key is not configured here on purpose: it is discovered from the
                    // authority's JWKS and refreshed on rotation. A pinned key would outlive the
                    // provider's next rotation and fail closed at an unpredictable moment.
                    ValidAudience = options.Audience,
                    ValidIssuer = options.Authority.TrimEnd('/'),

                    // No clock skew allowance beyond a token's stated lifetime is not realistic
                    // across machines; five minutes is the framework default and is kept
                    // explicitly so it is a decision rather than an inheritance.
                    ClockSkew = TimeSpan.FromMinutes(5),

                    NameClaimType = "sub",
                    RoleClaimType = "role",
                };
            });

        // A bearer token is not ambient — a cross-site page cannot make a browser attach one — so
        // this caller is exempt from the antiforgery gate. That exemption is the reason external
        // POSTs work at all; see D2.
        return builder.AddCredentialScheme(Scheme, isAmbient: false);
    }
}
