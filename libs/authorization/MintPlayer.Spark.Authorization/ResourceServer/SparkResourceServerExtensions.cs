using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Raven.Client.Documents;
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

        ValidateIssuer(options, nameof(AddSparkResourceServer));
        if (string.IsNullOrWhiteSpace(options.Audience))
            throw new InvalidOperationException(
                "AddSparkResourceServer requires an audience. Without it this application accepts every token the "
                + "issuer has minted, including ones obtained for an entirely different resource.");

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, SparkScopeRequirementHandler>());
        AddTokenScheme(builder.Services, SparkJwtBearerExtensions.Scheme, options);

        // A bearer or DPoP token is not ambient, so the caller is exempt from the antiforgery gate (D2).
        return builder.AddCredentialScheme(SparkJwtBearerExtensions.Scheme, isAmbient: false);
    }

    /// <summary>The checks of <paramref name="options"/> that do not depend on the audience.</summary>
    internal static void ValidateIssuer(SparkResourceServerOptions options, string caller)
    {
        if (string.IsNullOrWhiteSpace(options.Authority))
            throw new InvalidOperationException($"{caller} requires an authority.");
        if (options.UseIntrospection && (string.IsNullOrWhiteSpace(options.IntrospectionClientId) || string.IsNullOrWhiteSpace(options.IntrospectionClientSecret)))
            throw new InvalidOperationException(
                "UseIntrospection needs IntrospectionClientId and IntrospectionClientSecret: the issuer answers only "
                + "an authenticated client whose client id is the token's audience.");
    }

    /// <summary>
    /// Registers the ASP.NET Core scheme <paramref name="scheme"/> that validates <paramref name="options"/>' tokens: a JWT
    /// bearer, or introspection with <see cref="SparkResourceServerOptions.UseIntrospection"/>. Shared by
    /// <see cref="AddSparkResourceServer"/> and the MCP external authorization server
    /// (<see cref="SparkExternalAgentServerExtensions.AddExternalAgentAuthorizationServer"/>), which registers it under its own
    /// name and does not declare it a Spark credential.
    /// </summary>
    /// <remarks>
    /// <paramref name="options"/> is read when the scheme's options are first built (the first request), not now, so a caller
    /// may fill <see cref="SparkResourceServerOptions.Audience"/> at startup, as the MCP wiring does from the canonical URL.
    /// </remarks>
    internal static void AddTokenScheme(IServiceCollection services, string scheme, SparkResourceServerOptions options)
    {
        services.AddMemoryCache();

        if (options.UseIntrospection)
        {
            services.AddHttpClient(SparkResourceServerOptions.HttpClientName);
            services.TryAddSingleton<SparkResourceServerDiscovery>();
            services
                .AddAuthentication()
                .AddScheme<SparkIntrospectionOptions, SparkIntrospectionHandler>(scheme, o => o.ResourceServer = options);
        }
        else
        {
            services
                .AddAuthentication()
                .AddJwtBearer(scheme, jwt =>
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
                            var store = context.HttpContext.RequestServices.GetRequiredService<IDocumentStore>();
                            var error = await SparkProofOfPossession.ValidateAsync(context.HttpContext, context.Principal!, token, store);
                            if (error is not null)
                                context.Fail(error);
                        },
                    };
                });
        }
    }
}
