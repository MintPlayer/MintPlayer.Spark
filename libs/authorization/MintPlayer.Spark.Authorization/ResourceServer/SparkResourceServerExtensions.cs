using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Extensions;

namespace MintPlayer.Spark.Authorization.ResourceServer;

public static class SparkResourceServerExtensions
{
    /// <summary>
    /// Makes this application a resource server for <paramref name="authority"/>'s access tokens
    /// (<c>docs/identity_provider_platform_PRD.md</c> D8, I12): what <see cref="SparkJwtBearerExtensions.AddJwtBearerCredential"/>
    /// does, plus what an OAuth resource server needs beyond a signature check.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b><c>at+jwt</c> only</b> (RFC 9068), unless <see cref="SparkResourceServerOptions.RequireAccessTokenType"/> is off.</item>
    /// <item><b>DPoP and certificate binding</b> are enforced (<c>cnf</c>); the <c>DPoP</c> authorization scheme is accepted.</item>
    /// <item><b>Introspection</b> instead of local validation with <see cref="SparkResourceServerOptions.UseIntrospection"/>.</item>
    /// <item>Scopes are checked per endpoint: <c>.RequireScope("fleet.read")</c> or <c>[RequireScope("fleet.read")]</c>.</item>
    /// </list>
    /// It registers the same scheme as <see cref="SparkJwtBearerExtensions.AddJwtBearerCredential"/>
    /// (<see cref="SparkJwtBearerExtensions.Scheme"/>); call one of the two, not both.
    /// </remarks>
    public static ISparkBuilder AddSparkResourceServer(
        this ISparkBuilder builder, string authority, string audience, Action<SparkResourceServerOptions>? configure = null)
    {
        var options = new SparkResourceServerOptions { Authority = authority, Audience = audience };
        configure?.Invoke(options);

        if (string.IsNullOrWhiteSpace(options.Authority))
            throw new InvalidOperationException("AddSparkResourceServer requires an authority.");
        if (string.IsNullOrWhiteSpace(options.Audience))
            throw new InvalidOperationException(
                "AddSparkResourceServer requires an audience. Without it this application accepts every token the "
                + "issuer has minted, including ones obtained for an entirely different resource.");
        if (options.UseIntrospection && (string.IsNullOrWhiteSpace(options.IntrospectionClientId) || string.IsNullOrWhiteSpace(options.IntrospectionClientSecret)))
            throw new InvalidOperationException(
                "UseIntrospection needs IntrospectionClientId and IntrospectionClientSecret: the issuer answers only "
                + "an authenticated client whose client id is the token's audience.");

        builder.Services.AddMemoryCache();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, SparkScopeRequirementHandler>());

        if (options.UseIntrospection)
        {
            builder.Services.AddHttpClient();
            builder.Services
                .AddAuthentication()
                .AddScheme<SparkIntrospectionOptions, SparkIntrospectionHandler>(SparkJwtBearerExtensions.Scheme, o => o.ResourceServer = options);
        }
        else
        {
            builder.Services
                .AddAuthentication()
                .AddJwtBearer(SparkJwtBearerExtensions.Scheme, jwt =>
                {
                    jwt.Authority = options.Authority;
                    jwt.Audience = options.Audience;
                    jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                    // Verbatim claim types, for the reason AddJwtBearerCredential gives (group claims).
                    jwt.MapInboundClaims = false;
                    jwt.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ValidAudience = options.Audience,
                        ValidIssuer = options.Authority.TrimEnd('/'),
                        ValidTypes = options.RequireAccessTokenType ? ["at+jwt", "application/at+jwt"] : null,
                        ClockSkew = TimeSpan.FromMinutes(5),
                        NameClaimType = "sub",
                        RoleClaimType = "role",
                    };
                    jwt.Events = new JwtBearerEvents
                    {
                        // The default handler reads only "Bearer"; a DPoP-bound token arrives as "DPoP <token>".
                        OnMessageReceived = context =>
                        {
                            context.Token = SparkProofOfPossession.ReadToken(context.HttpContext);
                            return Task.CompletedTask;
                        },
                        OnTokenValidated = async context =>
                        {
                            var token = SparkProofOfPossession.ReadToken(context.HttpContext)!;
                            var cache = context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();
                            var error = await SparkProofOfPossession.ValidateAsync(context.HttpContext, context.Principal!, token, cache);
                            if (error is not null)
                                context.Fail(error);
                        },
                    };
                });
        }

        // A bearer or DPoP token is not ambient, so the caller is exempt from the antiforgery gate (D2).
        return builder.AddCredentialScheme(SparkJwtBearerExtensions.Scheme, isAmbient: false);
    }
}
