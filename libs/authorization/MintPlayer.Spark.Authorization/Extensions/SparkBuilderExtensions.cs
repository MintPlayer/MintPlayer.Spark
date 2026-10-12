using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.Extensions;

namespace MintPlayer.Spark.Authorization.Extensions;

public static class SparkBuilderAuthorizationExtensions
{
    /// <summary>
    /// Adds Spark authentication with RavenDB-backed identity stores.
    /// Registers ASP.NET Core Identity services, bearer + cookie authentication,
    /// and the RavenDB user/role stores.
    /// </summary>
    /// <param name="configure">
    /// Configures Spark's own authentication surface — notably
    /// <see cref="SparkAuthenticationOptions.LocalCredentials"/>, which chooses how much of the
    /// email/password endpoint family to mount. Defaults to <see cref="SparkLocalCredentials.Full"/>,
    /// so an application that omits it keeps the endpoint set Spark has always mapped.
    /// </param>
    /// <remarks>
    /// External providers are added afterwards, on the same builder: <c>spark.AddGitHub(…)</c>,
    /// <c>spark.AddGoogle(…)</c>, <c>spark.AddOpenIdConnect(…)</c>, <c>spark.AddExternalProviders(configuration)</c>,
    /// or <c>spark.AddExternalScheme(scheme, policy)</c> for a handler registered by hand.
    /// </remarks>
    public static ISparkBuilder AddAuthentication<TUser>(
        this ISparkBuilder builder,
        Action<SparkAuthenticationOptions>? configure = null,
        Action<IdentityOptions>? configureIdentity = null)
        where TUser : SparkUser, new()
    {
        var options = new SparkAuthenticationOptions();
        configure?.Invoke(options);

        builder.Registry.IdentityUserType = typeof(TUser);

        // Published so other Spark packages can honour the same mode instead of carrying their own
        // copy of the flag. MintPlayer.Spark.IdentityProvider serves a second local-credential
        // surface at /connect/login; two flags could disagree, and an application reporting that
        // local credentials are off while still serving a password form would be believed.
        builder.Services.AddSingleton(options);

        // ⚠️ And the same instance behind IOptions<>. Registering only the concrete type looks
        // harmless because IOptions<SparkAuthenticationOptions> still *resolves* — the options
        // infrastructure constructs a default one for any T. So a consumer asking for it gets a
        // fully-formed object with every setting at its default and no indication that it is not
        // the configured one. That silently disabled the ConfirmByEmail startup guard and would
        // have made every external-login linking decision read Disabled in production, while tests
        // that configured the mode through Configure<T> passed. Options.Create hands back the very
        // object above, so the two accessors cannot disagree.
        builder.Services.AddSingleton<IOptions<SparkAuthenticationOptions>>(Options.Create(options));

        builder.Services.AddSparkAuthentication<TUser>(configureIdentity);

        // #460 D6. Registered after configureIdentity, and only ever switched on, so an application
        // that set IdentityOptions.SignIn.RequireConfirmedEmail itself keeps it.
        if (options.RequireConfirmedEmail)
            builder.Services.Configure<IdentityOptions>(identity => identity.SignIn.RequireConfirmedEmail = true);

        // Pinned explicitly, including where the value equals today's framework default.
        //
        // UserVerification is the one that matters: under "preferred" an authenticator may skip the
        // PIN or biometric and the assertion still verifies, which quietly turns a passkey into a
        // possession-only credential — anyone holding the unlocked device signs in. "required" is
        // the current default, and writing it down means a future default change cannot make that
        // trade on our behalf.
        //
        // ResidentKey stays "preferred" because sign-in is discoverable-only: a non-discoverable
        // credential simply will not be offered, and the user falls back to an external login.
        builder.Services.Configure<IdentityPasskeyOptions>(passkeyOptions =>
        {
            passkeyOptions.UserVerificationRequirement = "required";
            passkeyOptions.ResidentKeyRequirement = "preferred";

            // Null means "derive the relying-party id from the request Host", which is right until a
            // proxy is misconfigured — and a wrong RP id is permanent, because a passkey is bound to
            // it for life. Set it and the value is reviewable in one place.
            if (!string.IsNullOrWhiteSpace(options.PasskeyServerDomain))
                passkeyOptions.ServerDomain = options.PasskeyServerDomain;
        });

        // Identity's own two schemes, declared separately rather than as the combined
        // BearerAndApplication scheme. The combination would authenticate a request without saying
        // which half did it, and the antiforgery gate needs exactly that: the cookie is ambient and
        // must be defended against CSRF, the bearer is not and must not be obstructed by it.
        builder.AddCredentialScheme(IdentityConstants.ApplicationScheme, isAmbient: true);
        builder.AddCredentialScheme(IdentityConstants.BearerScheme);

        builder.AddAgentAccess();
        // Setup 2's mapping (MCP PRD D22): Identity's login lookup for this user type.
        builder.Services.TryAddScoped<IAgentLinkedLogins, AgentLinkedLogins<TUser>>();

        // Register middleware and endpoint callbacks
        builder.Registry.AddEndpoints(endpoints =>
            endpoints.MapSparkIdentityApi<TUser>());

        return builder;
    }

    /// <summary>
    /// Agent grants and personal access tokens (MCP PRD D4, D11–D14): setup 1 is wired whenever this package is.
    /// Inert without the Mcp package — nothing runs the token scheme and the account endpoints are not mapped.
    /// </summary>
    /// <remarks>
    /// The token scheme is registered with ASP.NET Core but deliberately <b>not</b> declared a Spark credential
    /// (<c>AddCredentialScheme</c>): the Mcp package runs it on <c>/mcp</c> only, where the agent layers narrow the
    /// token's rights; on every other endpoint a personal access token authenticates nothing. The registration of
    /// <see cref="IAgentCredentialScheme"/> is how the Mcp package learns the setup is wired (no assembly sniffing).
    /// </remarks>
    private static void AddAgentAccess(this ISparkBuilder builder)
    {
        var services = builder.Services;
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, PersonalAccessTokenHandler>(PersonalAccessTokens.Scheme, _ => { });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AgentGrantStore>();
        services.TryAddScoped<AgentRequestUser>();
        services.TryAddScoped<AgentGrantsAccount>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentCredentialScheme, PersonalAccessTokenScheme>());

        // PRD §4.6: an agent token carries no groups; they come from the user store on every request.
        builder.AddGroupMembershipProvider<AgentStoreGroupMembership>();
    }
}
