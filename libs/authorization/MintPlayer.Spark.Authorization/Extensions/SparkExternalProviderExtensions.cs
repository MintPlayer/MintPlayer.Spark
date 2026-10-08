using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Facebook;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Configuration;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>
/// External-provider presets (#460 D7, #464/#490 Q4b), on <see cref="ISparkBuilder"/>. Each wraps an
/// ASP.NET Core handler, signs into Identity's external cookie, reports a provider-side failure back to
/// the popup or page (#490 D4), and <b>declares its verified-email signal</b> as a
/// <see cref="SparkExternalProviderRegistration"/>, which decides whether a first sign-in creates a
/// confirmed account, an unconfirmed one plus a confirmation mail, or none.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Preset</term><description>Signal</description></listheader>
/// <item><term>GitHub (<c>AddGitHub</c>)</term><description><c>email_verified</c> from <c>/user/emails</c>; absent → no account.</description></item>
/// <item><term>Google</term><description>userinfo <c>email_verified</c>; false/absent → no account.</description></item>
/// <item><term>Microsoft</term><description>trusted only for <b>personal</b> accounts (id token <c>tid</c> = the consumers tenant); work/school accounts → unconfirmed + mail (an organisation's admin can set any address).</description></item>
/// <item><term>Facebook</term><description>none reliable → unconfirmed + mail.</description></item>
/// <item><term>X (Twitter)</term><description>none reliable → unconfirmed + mail.</description></item>
/// <item><term>LinkedIn</term><description>OpenID Connect userinfo <c>email_verified</c>; false/absent → no account.</description></item>
/// <item><term>OpenID Connect</term><description><c>email_verified</c> (boolean or the string <c>"true"</c>); false/absent → no account.</description></item>
/// </list>
/// <para>
/// Every preset needs <c>spark.AddAuthentication&lt;TUser&gt;()</c> first and throws otherwise. Its
/// <c>configure</c> callback runs after Spark's defaults (and after configuration binding, for a provider
/// enabled through <c>AddExternalProviders</c>), so an application can still change any option.
/// Calling a preset twice for the same scheme <b>merges</b>: the handler is registered once and each
/// call's <c>configure</c> runs, in call order.
/// </para>
/// </remarks>
public static class SparkExternalProviderExtensions
{
    /// <summary>The tenant id Microsoft's identity platform uses for personal (consumer) accounts.</summary>
    public const string MicrosoftConsumersTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

    /// <summary>The default scheme of <see cref="AddTwitter(ISparkBuilder, Action{OAuthOptions}?)"/>.</summary>
    public const string TwitterScheme = "Twitter";

    /// <summary>The default scheme of <see cref="AddLinkedIn(ISparkBuilder, Action{OAuthOptions}?)"/>.</summary>
    public const string LinkedInScheme = "LinkedIn";

    /// <summary>X's OAuth 2.0 user-lookup endpoint, asking for the fields the preset maps.</summary>
    internal const string XUserInformationEndpoint = "https://api.x.com/2/users/me?user.fields=confirmed_email,name,username";

    // --- Google ---------------------------------------------------------------------------------

    /// <summary>Google, through <c>Microsoft.AspNetCore.Authentication.Google</c>.</summary>
    public static ISparkBuilder AddGoogle(this ISparkBuilder spark, Action<GoogleOptions>? configure = null)
        => spark.AddGoogle(GoogleDefaults.AuthenticationScheme, displayName: null, configure);

    /// <inheritdoc cref="AddGoogle(ISparkBuilder, Action{GoogleOptions}?)"/>
    public static ISparkBuilder AddGoogle(this ISparkBuilder spark, string scheme, string? displayName, Action<GoogleOptions>? configure = null)
        => AddProvider(spark, nameof(AddGoogle), scheme, displayName, VerifiedClaimOrRefuse(),
            (auth, options) => auth.AddGoogle(scheme, displayName ?? GoogleDefaults.DisplayName, options),
            options =>
            {
                // The handler maps no verification claim of its own; the v3 userinfo body carries a
                // boolean "email_verified".
                options.ClaimActions.MapJsonKey(SparkExternalProviderPolicy.EmailVerifiedClaim, "email_verified");
            },
            configure);

    // --- Microsoft ------------------------------------------------------------------------------

    /// <summary>
    /// Microsoft accounts, through <c>Microsoft.AspNetCore.Authentication.MicrosoftAccount</c>. Adds
    /// the <c>openid</c> scope so the token response carries an id token, whose <c>tid</c> tells a
    /// personal account from a work or school one.
    /// </summary>
    public static ISparkBuilder AddMicrosoftAccount(this ISparkBuilder spark, Action<MicrosoftAccountOptions>? configure = null)
        => spark.AddMicrosoftAccount(MicrosoftAccountDefaults.AuthenticationScheme, displayName: null, configure);

    /// <inheritdoc cref="AddMicrosoftAccount(ISparkBuilder, Action{MicrosoftAccountOptions}?)"/>
    public static ISparkBuilder AddMicrosoftAccount(this ISparkBuilder spark, string scheme, string? displayName, Action<MicrosoftAccountOptions>? configure = null)
        => AddProvider(spark, nameof(AddMicrosoftAccount), scheme, displayName,
            new SparkExternalProviderPolicy
            {
                EmailVerification = principal => SparkExternalProviderPolicy.HasTrueClaim(principal, SparkExternalProviderPolicy.EmailVerifiedClaim)
                    ? SparkEmailVerification.Verified
                    : SparkEmailVerification.NoSignal,
            },
            (auth, options) => auth.AddMicrosoftAccount(scheme, displayName ?? MicrosoftAccountDefaults.DisplayName, options),
            options =>
            {
                options.Scope.Add("openid");
                options.Events.OnCreatingTicket = context =>
                {
                    // Received directly from the token endpoint over TLS, which OIDC Core §3.1.3.7 accepts
                    // in place of a signature check. Only the tenant id is read.
                    if (TenantOf(context.TokenResponse.Response) == MicrosoftConsumersTenantId)
                        context.Identity?.AddClaim(new Claim(SparkExternalProviderPolicy.EmailVerifiedClaim, "true"));
                    return Task.CompletedTask;
                };
            },
            configure);

    // --- Facebook -------------------------------------------------------------------------------

    /// <summary>Facebook, through <c>Microsoft.AspNetCore.Authentication.Facebook</c>. No reliable verified-email signal.</summary>
    public static ISparkBuilder AddFacebook(this ISparkBuilder spark, Action<FacebookOptions>? configure = null)
        => spark.AddFacebook(FacebookDefaults.AuthenticationScheme, displayName: null, configure);

    /// <inheritdoc cref="AddFacebook(ISparkBuilder, Action{FacebookOptions}?)"/>
    public static ISparkBuilder AddFacebook(this ISparkBuilder spark, string scheme, string? displayName, Action<FacebookOptions>? configure = null)
        => AddProvider(spark, nameof(AddFacebook), scheme, displayName, SparkExternalProviderPolicy.WithoutVerifiedEmailSignal(),
            (auth, options) => auth.AddFacebook(scheme, displayName ?? FacebookDefaults.DisplayName, options),
            _ => { },
            configure);

    // --- X (Twitter) ----------------------------------------------------------------------------

    /// <summary>
    /// X (Twitter) on OAuth 2.0 with PKCE (#490 D6, Q3), over the generic OAuth handler — no
    /// third-party package. Confidential-client authentication on the token request is HTTP Basic
    /// (<see cref="SparkXClientAuthenticationHandler"/>); the user comes from <c>GET /2/users/me</c>.
    /// </summary>
    /// <remarks>
    /// The email is <c>confirmed_email</c>, which needs the <c>users.email</c> scope <b>and</b> the
    /// "Request email from users" setting on the X developer app. X does not document it as a verified
    /// address, so the policy stays "no signal": an unconfirmed account plus a confirmation mail.
    /// </remarks>
    public static ISparkBuilder AddTwitter(this ISparkBuilder spark, Action<OAuthOptions>? configure = null)
        => spark.AddTwitter(TwitterScheme, displayName: null, configure);

    /// <inheritdoc cref="AddTwitter(ISparkBuilder, Action{OAuthOptions}?)"/>
    public static ISparkBuilder AddTwitter(this ISparkBuilder spark, string scheme, string? displayName, Action<OAuthOptions>? configure = null)
        => AddProvider(spark, nameof(AddTwitter), scheme, displayName, SparkExternalProviderPolicy.WithoutVerifiedEmailSignal(),
            (auth, options) => auth.AddOAuth(scheme, displayName ?? "X", options),
            options =>
            {
                options.AuthorizationEndpoint = "https://x.com/i/oauth2/authorize";
                options.TokenEndpoint = "https://api.x.com/2/oauth2/token";
                options.UserInformationEndpoint = XUserInformationEndpoint;
                options.CallbackPath = "/signin-twitter";
                options.UsePkce = true;
                options.Scope.Add("users.read");
                options.Scope.Add("tweet.read");
                options.Scope.Add("users.email");
                options.BackchannelHttpHandler = new SparkXClientAuthenticationHandler(new HttpClientHandler());

                options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
                options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
                options.ClaimActions.MapJsonKey(ClaimTypes.Email, "confirmed_email");

                options.Events.OnCreatingTicket = context => RunUserInformationClaimActions(context, unwrapDataProperty: true);
            },
            configure);

    // --- LinkedIn -------------------------------------------------------------------------------

    /// <summary>
    /// LinkedIn ("Sign In with LinkedIn using OpenID Connect"), over the generic OAuth handler — no
    /// third-party package. Claims come from the OIDC userinfo endpoint.
    /// </summary>
    /// <remarks>
    /// ⚠️ The provider key is the OIDC <c>sub</c>. Accounts linked through the retired LinkedIn v2
    /// <c>/me</c> API (e.g. <c>AspNet.Security.OAuth.LinkedIn</c>) stored that API's member id; whether
    /// the two are equal is not documented, so such a login may not match on first sign-in and falls
    /// to the email-already-registered path instead.
    /// </remarks>
    public static ISparkBuilder AddLinkedIn(this ISparkBuilder spark, Action<OAuthOptions>? configure = null)
        => spark.AddLinkedIn(LinkedInScheme, displayName: null, configure);

    /// <inheritdoc cref="AddLinkedIn(ISparkBuilder, Action{OAuthOptions}?)"/>
    public static ISparkBuilder AddLinkedIn(this ISparkBuilder spark, string scheme, string? displayName, Action<OAuthOptions>? configure = null)
        => AddProvider(spark, nameof(AddLinkedIn), scheme, displayName, VerifiedClaimOrRefuse(),
            (auth, options) => auth.AddOAuth(scheme, displayName ?? "LinkedIn", options),
            options =>
            {
                options.AuthorizationEndpoint = "https://www.linkedin.com/oauth/v2/authorization";
                options.TokenEndpoint = "https://www.linkedin.com/oauth/v2/accessToken";
                options.UserInformationEndpoint = "https://api.linkedin.com/v2/userinfo";
                options.CallbackPath = "/signin-linkedin";
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");

                options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "sub");
                options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
                options.ClaimActions.MapJsonKey(ClaimTypes.GivenName, "given_name");
                options.ClaimActions.MapJsonKey(ClaimTypes.Surname, "family_name");
                options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
                options.ClaimActions.MapJsonKey(SparkExternalProviderPolicy.EmailVerifiedClaim, "email_verified");

                options.Events.OnCreatingTicket = context => RunUserInformationClaimActions(context, unwrapDataProperty: false);
            },
            configure);

    // --- OpenID Connect -------------------------------------------------------------------------

    /// <summary>
    /// Any OpenID Connect provider — notably another Spark application's own identity provider
    /// (<c>MintPlayer.Spark.IdentityProvider</c>, #490 D7) — through
    /// <c>Microsoft.AspNetCore.Authentication.OpenIdConnect</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Code flow with PKCE, <c>response_mode=query</c> (the Spark IdP never does form_post, S6 item 5),
    /// client_secret_post (the handler's default), scopes <c>openid profile email</c>, claims from the
    /// id token plus the userinfo endpoint, tokens not saved. Claims are read under their JWT names
    /// (<c>MapInboundClaims = false</c>) and then <c>sub</c>/<c>email</c>/<c>name</c> are copied to the
    /// <see cref="ClaimTypes"/> the external-login callback reads.
    /// </para>
    /// <para>
    /// Callback <c>/signin-{scheme}</c>, signed-out callback <c>/signout-callback-{scheme}</c>, remote
    /// sign-out <c>/signout-{scheme}</c>, so several OIDC providers never share a path. Sign-out sends
    /// <c>client_id</c> as well as <c>id_token_hint</c> (S6 gap 1).
    /// </para>
    /// <para>
    /// Policy: <c>email_verified</c> = <c>true</c> (a boolean in userinfo, or the string <c>"true"</c>
    /// in an id token) → a confirmed account; anything else → no account.
    /// </para>
    /// </remarks>
    /// <param name="spark">The Spark builder.</param>
    /// <param name="scheme">The authentication scheme, also the provider name the client asks for.</param>
    /// <param name="displayName">The button label served by <c>/spark/auth/capabilities</c>.</param>
    /// <param name="configure">Sets at least <c>Authority</c>, <c>ClientId</c> and <c>ClientSecret</c>.</param>
    public static ISparkBuilder AddOpenIdConnect(this ISparkBuilder spark, string scheme, string displayName, Action<OpenIdConnectOptions>? configure = null)
        => AddProvider(spark, nameof(AddOpenIdConnect), scheme, displayName, VerifiedClaimOrRefuse(),
            (auth, options) => auth.AddOpenIdConnect(scheme, displayName, options),
            options =>
            {
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                options.SaveTokens = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false;
                options.CallbackPath = $"/signin-{scheme}";
                options.SignedOutCallbackPath = $"/signout-callback-{scheme}";
                options.RemoteSignOutPath = $"/signout-{scheme}";

                // The handler's default claim actions map sub, name, email, … from userinfo, but not
                // the verification flag.
                options.ClaimActions.MapUniqueJsonKey(SparkExternalProviderPolicy.EmailVerifiedClaim, "email_verified");

                options.Events.OnTicketReceived = context =>
                {
                    if (context.Principal?.Identity is ClaimsIdentity identity)
                        MapOpenIdConnectClaims(identity);
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToIdentityProviderForSignOut = context =>
                {
                    // S6 gap 1: the Spark IdP honours post_logout_redirect_uri only with a client_id.
                    if (string.IsNullOrEmpty(context.ProtocolMessage.ClientId))
                        context.ProtocolMessage.ClientId = context.Options.ClientId;
                    return Task.CompletedTask;
                };
            },
            configure);

    // --- raw handlers ---------------------------------------------------------------------------

    /// <summary>
    /// Declares an external-login scheme whose handler the application registers itself (any
    /// <c>RemoteAuthenticationHandler</c> added through <c>services.AddAuthentication()</c>), with the
    /// policy the external-login callback applies to its first sign-in.
    /// </summary>
    /// <remarks>
    /// A remote scheme without a declaration is refused at startup: without one, nobody has said
    /// whether the provider's email can be trusted.
    /// </remarks>
    public static ISparkBuilder AddExternalScheme(this ISparkBuilder spark, string scheme, SparkExternalProviderPolicy policy)
    {
        RequireAuthentication(spark, nameof(AddExternalScheme));
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentNullException.ThrowIfNull(policy);
        spark.Services.AddSingleton(new SparkExternalProviderRegistration(scheme, policy));
        return spark;
    }

    // --- shared ---------------------------------------------------------------------------------

    /// <summary>
    /// Registers (or, for a scheme a preset already registered, extends) one provider: the handler
    /// with Spark's defaults, its declared policy, then the application's <paramref name="configure"/>.
    /// </summary>
    internal static ISparkBuilder AddProvider<TOptions>(
        ISparkBuilder spark,
        string method,
        string scheme,
        string? displayName,
        SparkExternalProviderPolicy policy,
        Func<AuthenticationBuilder, Action<TOptions>, AuthenticationBuilder> register,
        Action<TOptions> defaults,
        Action<TOptions>? configure)
        where TOptions : RemoteAuthenticationOptions
    {
        RequireAuthentication(spark, method);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);

        var catalog = SparkExternalProviderCatalog.Of(spark.Services);
        if (catalog.TryGet(scheme, out var registered))
        {
            if (registered.OptionsType != typeof(TOptions) || registered.Method != method)
            {
                throw new InvalidOperationException(
                    $"spark.{method}(\"{scheme}\") conflicts with spark.{registered.Method}(\"{scheme}\"): one scheme "
                    + "can only be one provider. Give one of them another scheme name.");
            }

            // A second call for the same provider (configuration first, code after — or two code
            // calls) merges: its configure runs after everything registered so far.
            if (configure is not null)
                spark.Services.Configure(scheme, configure);
            if (!string.IsNullOrWhiteSpace(displayName))
                spark.Services.Configure<AuthenticationOptions>(options =>
                {
                    if (options.SchemeMap.TryGetValue(scheme, out var builder))
                        builder.DisplayName = displayName;
                });
            return spark;
        }

        catalog.Add(scheme, method, typeof(TOptions));
        spark.Services.AddSingleton(new SparkExternalProviderRegistration(scheme, policy));
        register(new AuthenticationBuilder(spark.Services), options =>
        {
            options.SignInScheme = IdentityConstants.ExternalScheme;
            // #490 D4: report a provider-side cancel or failure back to the popup or page. Assigned
            // before configure, so an application can still replace or wrap it.
            options.Events.OnRemoteFailure = SparkExternalLoginRemoteFailure.Handle;
            defaults(options);
            configure?.Invoke(options);
        });
        return spark;
    }

    /// <summary>Throws unless <c>spark.AddAuthentication&lt;TUser&gt;()</c> already ran on <paramref name="spark"/>.</summary>
    internal static void RequireAuthentication(ISparkBuilder spark, string method)
    {
        ArgumentNullException.ThrowIfNull(spark);
        if (spark.Registry.IdentityUserType is not null)
            return;

        throw new InvalidOperationException(
            $"spark.{method}(...) was called before spark.AddAuthentication<TUser>(). External providers sign "
            + "into ASP.NET Core Identity, which AddAuthentication<TUser>() registers: call it first, then add "
            + "the providers.");
    }

    /// <summary>
    /// Runs the claim actions over the provider's user-information response. X wraps the user in a
    /// <c>data</c> property; LinkedIn's OIDC userinfo is the object itself.
    /// </summary>
    internal static async Task RunUserInformationClaimActions(OAuthCreatingTicketContext context, bool unwrapDataProperty)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);

        using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
        response.EnsureSuccessStatusCode();

        using var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
        var root = user.RootElement;
        if (unwrapDataProperty)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The user-information response carried no 'data' object.");
            }
            root = data;
        }
        context.RunClaimActions(root);
    }

    /// <summary>
    /// Copies an OIDC principal's <c>sub</c>, <c>email</c> and <c>name</c> to the
    /// <see cref="ClaimTypes"/> the external-login callback reads, where those are not already present.
    /// </summary>
    internal static void MapOpenIdConnectClaims(ClaimsIdentity identity)
    {
        Copy("sub", ClaimTypes.NameIdentifier);
        Copy("email", ClaimTypes.Email);
        Copy("name", ClaimTypes.Name);

        void Copy(string from, string to)
        {
            if (identity.HasClaim(c => c.Type == to))
                return;
            if (identity.FindFirst(from) is { } source && !string.IsNullOrEmpty(source.Value))
                identity.AddClaim(new Claim(to, source.Value, source.ValueType, source.Issuer, source.OriginalIssuer));
        }
    }

    private static SparkExternalProviderPolicy VerifiedClaimOrRefuse() => new()
    {
        EmailVerification = SparkExternalProviderPolicy.Default.EmailVerification,
    };

    /// <summary>The <c>tid</c> of the <c>id_token</c> in a token-endpoint response, or <see langword="null"/>.</summary>
    internal static string? TenantOf(JsonDocument? tokenResponse)
    {
        if (tokenResponse is null
            || !tokenResponse.RootElement.TryGetProperty("id_token", out var idToken)
            || idToken.GetString() is not { } jwt)
        {
            return null;
        }

        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var claims = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return claims.RootElement.TryGetProperty("tid", out var tid) ? tid.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>The schemes Spark's presets registered on one service collection, so a second call can merge.</summary>
internal sealed class SparkExternalProviderCatalog
{
    private readonly Dictionary<string, (string Method, Type OptionsType)> schemes = new(StringComparer.Ordinal);

    internal static SparkExternalProviderCatalog Of(IServiceCollection services)
    {
        if (services.FirstOrDefault(d => d.ServiceType == typeof(SparkExternalProviderCatalog))?.ImplementationInstance
            is SparkExternalProviderCatalog existing)
        {
            return existing;
        }

        var catalog = new SparkExternalProviderCatalog();
        services.AddSingleton(catalog);
        return catalog;
    }

    internal bool TryGet(string scheme, out (string Method, Type OptionsType) registered)
        => schemes.TryGetValue(scheme, out registered);

    internal void Add(string scheme, string method, Type optionsType) => schemes[scheme] = (method, optionsType);

    internal IReadOnlyCollection<string> Schemes => schemes.Keys;
}
