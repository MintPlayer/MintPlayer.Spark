using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Http;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Endpoints;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Extensions;

public static class SparkIdentityProviderExtensions
{
    /// <summary>The application's local-credential mode.</summary>
    /// <remarks>
    /// Required, not optional: the provider refuses to start without <c>AddAuthentication&lt;TUser&gt;()</c>
    /// (<see cref="OidcUserEndpoints.RequireUserType"/>, checked before anything is mapped), which is
    /// also what registers these options. The old fallback to <see cref="SparkLocalCredentials.Full"/>
    /// for "used without <c>AddAuthentication</c>" described a configuration that can no longer start.
    /// </remarks>
    internal static SparkLocalCredentials LocalCredentialsOf(IServiceProvider services) =>
        services.GetRequiredService<SparkAuthenticationOptions>().LocalCredentials;

    /// <summary>
    /// Configures this Spark application as an OIDC Identity Provider.
    /// Registers OIDC endpoints, signing key service, token generator,
    /// and token cleanup background service.
    /// </summary>
    /// <remarks>
    /// Every flow and endpoint by default; <see cref="SparkIdentityProviderOptions.Flows"/>,
    /// <see cref="SparkIdentityProviderOptions.Endpoints"/> and <see cref="SparkIdentityProviderOptions.AgentsOnly"/> choose fewer.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The chosen flows and endpoints do not fit together (the message names the dependency).</exception>
    public static ISparkBuilder AddIdentityProvider(
        this ISparkBuilder builder,
        Action<SparkIdentityProviderOptions>? configure = null)
    {
        var options = new SparkIdentityProviderOptions();
        // Spark:IdentityProvider first, the lambda second: code wins over configuration, as everywhere in Spark.
        builder.Configuration?.GetSection("Spark:IdentityProvider").Bind(options);
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        // What is mapped and advertised (Flows, Endpoints, the agent server), decided here once: every endpoint's IsEnabled,
        // the token endpoint and the metadata documents read it. A combination that cannot work stops startup now, naming
        // the dependency, rather than mapping something half-usable.
        builder.Services.AddSingleton(OidcSurface.For(options));

        // Register services
        // I10: the signing keys live in OidcKeys, protected with Data Protection; a key file named by
        // SigningKeyPath is imported once. Rotated on schedule by OidcKeyRotationService.
        builder.Services.AddSingleton<OidcKeyRing>();
        builder.Services.AddHostedService<OidcKeyRotationService>();

        // I10: every sign-in at the provider gets a session id (sid) in its cookie, so logout can
        // reach the clients that received tokens in that session.
        builder.Services.AddSingleton<OidcSessionStore>();
        builder.Services.PostConfigure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
            Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme, cookie =>
            {
                var previous = cookie.Events.OnSigningIn;
                cookie.Events.OnSigningIn = async context =>
                {
                    OidcSessionStore.StampSessionId(context.Principal!);
                    await previous(context);
                };
            });
        builder.Services.AddSingleton<OidcTokenGenerator>();
        builder.Services.AddSingleton<OidcIssuer>();
        // The typed /connect pages reach their HttpContext through the accessor (D8). AddSpark
        // registers it too; registering it here keeps the provider from depending on that.
        builder.Services.AddHttpContextAccessor();

        // Validation of the OIDC admin screens (#482: interceptors, not Actions-class overrides).
        builder.AddInterceptor<Interceptors.OidcApplicationInterceptors>();
        builder.AddInterceptor<Interceptors.OidcResourceInterceptors>();

        // The developer portal (PRD D2): developer status on the user document, membership of the
        // bound developers group derived from it per request, and the audit trail (D9).
        builder.Services.AddSingleton<OidcDevelopers>();
        var moduleRegistry = builder.Registry;
        builder.Services.AddSingleton(sp => new OidcUserDocuments(moduleRegistry, sp.GetRequiredService<IDocumentStore>()));
        builder.Services.AddSingleton<OidcAudit>();
        builder.Services.AddSingleton<OidcPortalMail>();
        // The portal mails' default templates (Mail/SparkIdentityProvider/*.mjml, embedded as SparkMail/…);
        // an app's Templates/Mail/SparkIdentityProvider/*.mjml overrides them. Never declared before, so every
        // portal mail (invitations, developer and review decisions) was dead-lettered with "No mail template
        // 'SparkIdentityProvider/…'" (found by the E2E journey). Anchored on a type of this assembly.
        MintPlayer.Spark.MailManager.SparkMailTemplateServiceCollectionExtensions.AddSparkMailTemplates(
            builder.Services, typeof(OidcPortalMailTemplates).Assembly, "SparkMail/");
        builder.Services.AddSingleton<OidcPortalLinks>();
        builder.Services.AddSingleton<OidcInvitations>();
        builder.Services.AddSingleton<OidcTeamMail>();
        builder.Services.AddScoped<OidcPortalAccess>();
        builder.Services.AddScoped<ConnectText>();
        builder.Services.AddSingleton<OidcGrantWithdrawal>();

        // Protocol (PRD D8): client authentication and the clients' own keys.
        builder.Services.AddHttpClient();
        AddClientRequestClients(builder);
        builder.Services.AddMemoryCache();
        builder.Services.Configure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(rl =>
            rl.AddPolicy(RateLimitPolicy, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.RateLimits.PermitLimit,
                    Window = options.RateLimits.Window,
                    QueueLimit = 0,
                })));
        builder.Services.AddSingleton<OidcClientKeys>();
        builder.Services.AddSingleton<OidcClientAuthenticator>();
        builder.Services.AddSingleton<OidcRequestObjects>();
        builder.Services.AddSingleton<OidcProofOfPossession>();
        builder.Services.AddSingleton<OidcJwe>();
        builder.Services.AddSingleton<OidcClientRegistration>();
        builder.Services.AddScoped<OidcAuthorizeHandler>();
        builder.AddGroupMembershipProvider<OidcDeveloperMembership>();

        AddAgents(builder, options);

        // Constructed rather than resolved, because the CORS policy's predicate below has no service
        // provider of its own. Registered unconditionally: an unused snapshot costs nothing, and it
        // saves OidcApplicationActions from taking an optional dependency.
        var corsOrigins = new OidcCorsOrigins();
        builder.Services.AddSingleton(corsOrigins);

        // Register dynamic CORS policy for OIDC endpoints
        if (options.EnableDynamicCors)
        {
            builder.Services.AddCors(corsOptions =>
            {
                corsOptions.AddPolicy(CorsPolicy, policy =>
                {
                    // Narrowed to the origins registered on enabled OidcApplication documents.
                    // `AllowedCorsOrigins` used to be read by nothing at all, which made the
                    // "validated at runtime" comment that once sat here describe a control that did
                    // not exist.
                    //
                    // Any-origin was defensible for what this policy covers — a public OIDC client
                    // has no secret, so `/token` is *designed* to be called cross-origin and its
                    // control is PKCE, not the Origin header. Narrowing is defence in depth: it
                    // stops a hostile page burning a victim's authorization code.
                    //
                    // ⚠️ Do NOT add AllowCredentials here. Echoing a specific origin makes it
                    // reachable where `AllowAnyOrigin` made it impossible by construction — the
                    // wildcard's safety property was load-bearing, and narrowing quietly removes
                    // it. See docs/guide-cors.md.
                    policy.SetIsOriginAllowed(corsOrigins.IsAllowed)
                          .AllowAnyHeader()
                          .AllowAnyMethod();
                });
            });
        }

        // Register OIDC endpoints
        // The user-generic endpoints close here, at map time, when every Add* call has run - so
        // AddAuthentication<TUser>() may come before or after this method.
        var registry = builder.Registry;
        builder.Registry.AddEndpoints(endpoints =>
        {
            // Checked first: the generated mapping reads SparkAuthenticationOptions (the local-
            // credentials group's IsEnabled), which only AddAuthentication<TUser>() registers.
            var userType = OidcUserEndpoints.RequireUserType(registry.IdentityUserType);
            endpoints.MapSparkIdentityProviderEndpoints();
            OidcUserEndpoints.Map(endpoints, userType);
        });

        // Authorization requests, tokens, sessions and audit events carry @expires, so RavenDB reaps them
        // itself rather than needing a sweeper. Deletion is housekeeping only: an expired record is refused
        // on read whether or not the document is still there (OidcExpiry). UseSpark() enables expiration
        // once, with the shared frequency.
        builder.Registry.RequireDocumentExpiration("IdentityProvider");

        // Register middleware to deploy indexes
        builder.Registry.AddMiddleware(app =>
        {
            // ⚠️ No UseCors here. `UseSpark` registers the CORS middleware unconditionally — the same
            // arrangement as antiforgery — so this module declares only what its endpoints need
            // (`RequireCors`) and leaves the pipeline alone.
            //
            // It used to call `app.UseCors("SparkOidcCors")`, which applies a policy to the ENTIRE
            // pipeline: merely enabling the identity provider let any page on any origin read the
            // anonymous view of `/spark/types`, `/spark/translations`, `/spark/permissions/*` and
            // `/spark/auth/capabilities`. A module arranging the pipeline for itself is how a local
            // decision became a global one.

            // The interactive pages must not be framable. Every one of them turns a single click
            // into a security decision — granting a client access, or removing it — and a framed
            // page makes that click something an attacker can arrange. Nothing set this before;
            // the consent screen has been framable since it was written.
            //
            // Middleware, not an endpoint filter (D6): a filter would miss the 404s, 405s and
            // short-circuited responses under the prefix. The prefix is the group's own, so the two
            // cannot drift apart; the three /connect groups share it (Groups.cs).
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments(OidcConnectGroup.Prefix))
                {
                    context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
                    context.Response.Headers["X-Frame-Options"] = "DENY";
                }

                await next();
            });

            var documentStore = app.ApplicationServices.GetRequiredService<IDocumentStore>();
            new OidcApplications_ByClientId().Execute(documentStore);

            // The key ring before the first request: a ring that cannot be decrypted stops startup here,
            // with the reason, rather than failing the first token request.
            app.ApplicationServices.GetRequiredService<OidcKeyRing>().InitializeAsync().GetAwaiter().GetResult();

            // Load the CORS origin snapshot before the first request, not lazily: a browser does
            // not retry a preflight it lost, and this fails closed until the load lands.
            if (options.EnableDynamicCors)
            {
                var corsOrigins = app.ApplicationServices.GetRequiredService<OidcCorsOrigins>();
                corsOrigins.Initialize(
                    documentStore,
                    app.ApplicationServices.GetService<ILoggerFactory>()?.CreateLogger<OidcCorsOrigins>());
                corsOrigins.LoadAsync().GetAwaiter().GetResult();
            }

            new OidcGrants_BySubject().Execute(documentStore);

            // ForMcp() / ForMcpResource(): one MCP resource per endpoint, its canonical URL as its audience, before the
            // first request (startup fails here, with the reason: ForMcp() without spark.AddMcp or a canonical URL, a
            // malformed or duplicate endpoint URL).
            if (options.Mcp is not null)
                app.ApplicationServices.GetRequiredService<Mcp.OidcMcpResources>().InitializeAsync(app.ApplicationServices, documentStore).GetAwaiter().GetResult();
        });

        return builder;
    }

    /// <summary>
    /// The agent side (MCP PRD D4 setup 3, D12, D14). The Connected-applications contribution and the grant link are
    /// always registered: a provider's sign-ins at other websites belong on the account page with or without agents.
    /// The rest exists only with <see cref="SparkIdentityProviderOptions.ForMcp"/> or
    /// <see cref="SparkIdentityProviderOptions.ForMcpResource"/>; the <c>/mcp</c> bearer scheme only for this
    /// application's own endpoint.
    /// </summary>
    private static void AddAgents(ISparkBuilder builder, SparkIdentityProviderOptions options)
    {
        var services = builder.Services;
        services.AddSingleton<Mcp.OidcMcpResources>();
        // A factory for the same reason as the grants below; the consent endpoints inject it.
        services.AddScoped(sp => ActivatorUtilities.CreateInstance<Endpoints.AgentConsent>(sp));
        services.AddSingleton<Mcp.OidcAgentRegistrationLimiter>();
        services.AddSingleton<Mcp.OidcClientMetadataDocuments>();
        services.AddSingleton<Mcp.OidcAgentDynamicRegistration>();
        // A factory, so validation without AddAuthentication<TUser>() reports the missing user type (OidcUserEndpoints), not the grant store.
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<Mcp.OidcAgentGrants>(sp));
        services.AddSingleton<MintPlayer.Spark.Authorization.Agents.IAgentGrantLink>(sp => sp.GetRequiredService<Mcp.OidcAgentGrants>());
        services.AddScoped<MintPlayer.Spark.Authorization.Agents.ISparkConnectedApplicationSource, Mcp.OidcConnectedApplicationSource>();

        if (options.Mcp is not { } mcp)
            return;

        // The metadata-document fetch is the caller's URL (D5): SSRF-safe, https on 443 only, no redirects (the
        // document must be at its own URL), a few KiB, a few seconds. Configuration cannot open private networks
        // for it; ClientRequests is for the jwks/logout clients only.
        services.Configure<SafeHttpOptions>(Mcp.OidcClientMetadataDocuments.HttpClientName, o =>
        {
            o.AllowedPorts = [443];
            o.FollowRedirects = false;
            o.MaxRedirects = 0;
            o.Timeout = mcp.MetadataDocumentTimeout;
            o.ConnectTimeout = mcp.MetadataDocumentTimeout;
            o.MaxResponseBytes = mcp.MetadataDocumentMaxBytes;
            o.MaxDecompressedBytes = mcp.MetadataDocumentMaxBytes;
        });
        services.AddSafeHttpClient(Mcp.OidcClientMetadataDocuments.HttpClientName);
        services.AddHostedService<Mcp.OidcAgentClientCleanupService>();

        // The agent token on this application's /mcp only: registered with ASP.NET Core, never as a Spark (web)
        // credential. The Mcp package finds it through IAgentCredentialScheme (M3a seam). A provider that signs agents in
        // only for other applications' endpoints has no /mcp of its own to accept them on, and advertising itself in this
        // application's protected-resource metadata would send agents for a token it cannot mint.
        if (!mcp.ServesLocalEndpoint)
            return;
        services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, Mcp.OidcAgentBearerHandler>(
            Mcp.OidcAgentCredentialScheme.SchemeName, _ => { });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<MintPlayer.Spark.Abstractions.IAgentCredentialScheme, Mcp.OidcAgentCredentialScheme>());
    }

    /// <summary>
    /// The policy the OIDC endpoints a browser reaches with <c>fetch</c> opt into, and nothing else.
    /// </summary>
    /// <remarks>
    /// Discovery and JWKS are fetched by every SPA OIDC library on boot; <c>/token</c> is the PKCE
    /// code exchange; <c>/userinfo</c> and <c>/revoke</c> are called with the resulting token.
    /// Everything else under <c>/connect</c> — authorize, login, consent, applications — is reached by
    /// top-level navigation, which is not a CORS request at all, so opting those in would grant
    /// something no caller can use.
    ///
    /// ⚠️ <c>/introspect</c> is deliberately left out: it is a resource-server-to-provider call
    /// authenticated by client credentials, so a browser has no business making it.
    /// </remarks>
    internal const string CorsPolicy = "SparkOidcCors";

    /// <summary>
    /// The named rate-limit policy on the machine endpoints (<c>docs/identity_provider_platform_PRD.md</c> D9):
    /// a fixed window per caller IP address from <see cref="SparkIdentityProviderOptions.RateLimits"/>.
    /// </summary>
    /// <remarks>
    /// It is always registered, so an application that runs the rate-limiting middleware never meets an unknown
    /// policy name; in one that does not, the endpoint metadata is inert. It applies on top of Spark's global
    /// per-IP budget for <c>/connect</c> (<c>spark.AddRateLimiter()</c>), giving the endpoints a client polls or
    /// scripts against (token, device polling) a budget of their own that the interactive pages do not share.
    /// </remarks>
    public const string RateLimitPolicy = "SparkIdentityProviderMachine";

    /// <summary>
    /// The SSRF-safe client (<c>docs/seo_analyzer_PRD.md</c> X2) that fetches a client's <c>jwks_uri</c>:
    /// 10 s, 1 MiB, up to 5 redirects. The URI is the client's choice, so loopback, private and metadata
    /// addresses are refused.
    /// </summary>
    public const string JwksHttpClient = "Spark.IdentityProvider.ClientJwks";

    /// <summary>
    /// The SSRF-safe client that POSTs back-channel logout tokens to a client's <c>backchannel_logout_uri</c>:
    /// 5 s, 64 KiB, up to 3 redirects and only within the origin of the registered URI (D40 of
    /// <c>docs/seo_analyzer_PRD.md</c>): a cross-origin redirect would re-send the logout token to a URI the
    /// client never registered, so it is refused; every hop is still SSRF-checked.
    /// </summary>
    public const string BackChannelLogoutHttpClient = "Spark.IdentityProvider.BackChannelLogout";

    /// <summary>
    /// Configuration both client-request clients bind (<see cref="SafeHttpOptions"/>), on top of their defaults;
    /// a list it names replaces the default list (so <c>AllowedPorts: [ 5001 ]</c> allows 5001 only).
    /// There is no exception for development by default. A local relying party whose JWKS or logout endpoint
    /// listens on loopback is allowed by naming it, e.g. in <c>appsettings.Development.json</c>:
    /// <c>"Spark": { "IdentityProvider": { "ClientRequests": { "AllowedNetworks": [ "127.0.0.1/32", "::1/128" ], "AllowedPorts": [ 5001 ] } } }</c>.
    /// </summary>
    public const string ClientRequestsSection = "Spark:IdentityProvider:ClientRequests";

    private static void AddClientRequestClients(ISparkBuilder builder)
    {
        var section = builder.Configuration?.GetSection(ClientRequestsSection);

        // Defaults first, so the section and any later services.Configure<SafeHttpOptions>(name, …) win.
        builder.Services.Configure<SafeHttpOptions>(JwksHttpClient, o =>
        {
            o.Timeout = TimeSpan.FromSeconds(10);
            o.MaxResponseBytes = 1024 * 1024;
            o.MaxDecompressedBytes = 1024 * 1024;
        });
        builder.Services.AddSafeHttpClient(JwksHttpClient, section);

        builder.Services.Configure<SafeHttpOptions>(BackChannelLogoutHttpClient, o =>
        {
            o.Timeout = TimeSpan.FromSeconds(5);
            o.MaxRedirects = 3;
            o.RedirectsWithinOriginOnly = true;
            o.MaxResponseBytes = 64 * 1024;
            o.MaxDecompressedBytes = 64 * 1024;
        });
        builder.Services.AddSafeHttpClient(BackChannelLogoutHttpClient, section);
    }
}
