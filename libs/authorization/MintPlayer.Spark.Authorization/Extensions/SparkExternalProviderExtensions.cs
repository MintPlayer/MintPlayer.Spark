using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Facebook;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.Twitter;
using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Authorization.Configuration;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>
/// External-provider presets (#460, D7). Each wraps the ASP.NET Core handler, signs into Identity's
/// external cookie, and <b>declares its verified-email signal</b> as a
/// <see cref="SparkExternalProviderRegistration"/> — which decides whether a first sign-in creates a
/// confirmed account, an unconfirmed one plus a confirmation mail, or none. See the spike SP-C results
/// in the #460 PRD for what each signal is based on.
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
/// </list>
/// Every preset's configure callback runs last, so an application can still change any option.
/// </remarks>
public static class SparkExternalProviderExtensions
{
    /// <summary>The tenant id Microsoft's identity platform uses for personal (consumer) accounts.</summary>
    public const string MicrosoftConsumersTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

    /// <summary>Google, through <c>Microsoft.AspNetCore.Authentication.Google</c>.</summary>
    public static IdentityBuilder AddSparkGoogle(this IdentityBuilder builder, Action<GoogleOptions> configure, string scheme = GoogleDefaults.AuthenticationScheme)
    {
        Declare(builder, scheme, VerifiedClaimOrRefuse());
        new AuthenticationBuilder(builder.Services).AddGoogle(scheme, options =>
        {
            options.SignInScheme = IdentityConstants.ExternalScheme;
            // The handler maps no verification claim of its own; the v3 userinfo body carries a
            // boolean "email_verified".
            options.ClaimActions.MapJsonKey(SparkExternalProviderPolicy.EmailVerifiedClaim, "email_verified");
            configure(options);
        });
        return builder;
    }

    /// <summary>
    /// Microsoft accounts, through <c>Microsoft.AspNetCore.Authentication.MicrosoftAccount</c>. Adds
    /// the <c>openid</c> scope so the token response carries an id token, whose <c>tid</c> tells a
    /// personal account from a work or school one.
    /// </summary>
    public static IdentityBuilder AddSparkMicrosoftAccount(this IdentityBuilder builder, Action<MicrosoftAccountOptions> configure, string scheme = MicrosoftAccountDefaults.AuthenticationScheme)
    {
        Declare(builder, scheme, new SparkExternalProviderPolicy
        {
            EmailVerification = principal => SparkExternalProviderPolicy.HasTrueClaim(principal, SparkExternalProviderPolicy.EmailVerifiedClaim)
                ? SparkEmailVerification.Verified
                : SparkEmailVerification.NoSignal,
        });

        new AuthenticationBuilder(builder.Services).AddMicrosoftAccount(scheme, options =>
        {
            options.SignInScheme = IdentityConstants.ExternalScheme;
            options.Scope.Add("openid");
            options.Events.OnCreatingTicket = context =>
            {
                // Received directly from the token endpoint over TLS, which OIDC Core §3.1.3.7 accepts
                // in place of a signature check. Only the tenant id is read.
                if (TenantOf(context.TokenResponse.Response) == MicrosoftConsumersTenantId)
                    context.Identity?.AddClaim(new Claim(SparkExternalProviderPolicy.EmailVerifiedClaim, "true"));
                return Task.CompletedTask;
            };
            configure(options);
        });
        return builder;
    }

    /// <summary>Facebook, through <c>Microsoft.AspNetCore.Authentication.Facebook</c>. No reliable verified-email signal.</summary>
    public static IdentityBuilder AddSparkFacebook(this IdentityBuilder builder, Action<FacebookOptions> configure, string scheme = FacebookDefaults.AuthenticationScheme)
    {
        Declare(builder, scheme, SparkExternalProviderPolicy.WithoutVerifiedEmailSignal());
        new AuthenticationBuilder(builder.Services).AddFacebook(scheme, options =>
        {
            options.SignInScheme = IdentityConstants.ExternalScheme;
            configure(options);
        });
        return builder;
    }

    /// <summary>
    /// X (Twitter), through <c>Microsoft.AspNetCore.Authentication.Twitter</c> (OAuth 1.0a).
    /// <c>RetrieveUserDetails</c> is switched on so the email is requested at all (the X app needs the
    /// "request email" permission). No reliable verified-email signal.
    /// </summary>
    public static IdentityBuilder AddSparkTwitter(this IdentityBuilder builder, Action<TwitterOptions> configure, string scheme = TwitterDefaults.AuthenticationScheme)
    {
        Declare(builder, scheme, SparkExternalProviderPolicy.WithoutVerifiedEmailSignal());
        new AuthenticationBuilder(builder.Services).AddTwitter(scheme, options =>
        {
            options.SignInScheme = IdentityConstants.ExternalScheme;
            options.RetrieveUserDetails = true;
            configure(options);
        });
        return builder;
    }

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
    public static IdentityBuilder AddSparkLinkedIn(this IdentityBuilder builder, Action<OAuthOptions> configure, string scheme = "LinkedIn")
    {
        Declare(builder, scheme, VerifiedClaimOrRefuse());
        new AuthenticationBuilder(builder.Services).AddOAuth(scheme, "LinkedIn", options =>
        {
            options.AuthorizationEndpoint = "https://www.linkedin.com/oauth/v2/authorization";
            options.TokenEndpoint = "https://www.linkedin.com/oauth/v2/accessToken";
            options.UserInformationEndpoint = "https://api.linkedin.com/v2/userinfo";
            options.CallbackPath = "/signin-linkedin";
            options.SignInScheme = IdentityConstants.ExternalScheme;
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");

            options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "sub");
            options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
            options.ClaimActions.MapJsonKey(ClaimTypes.GivenName, "given_name");
            options.ClaimActions.MapJsonKey(ClaimTypes.Surname, "family_name");
            options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
            options.ClaimActions.MapJsonKey(SparkExternalProviderPolicy.EmailVerifiedClaim, "email_verified");

            options.Events.OnCreatingTicket = async context =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);

                using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();

                using var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                context.RunClaimActions(user.RootElement);
            };

            configure(options);
        });
        return builder;
    }

    private static SparkExternalProviderPolicy VerifiedClaimOrRefuse() => new()
    {
        EmailVerification = SparkExternalProviderPolicy.Default.EmailVerification,
    };

    private static void Declare(IdentityBuilder builder, string scheme, SparkExternalProviderPolicy policy)
        => builder.Services.AddSingleton(new SparkExternalProviderRegistration(scheme, policy));

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
