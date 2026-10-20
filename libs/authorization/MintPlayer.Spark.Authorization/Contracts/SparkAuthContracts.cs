using System.Text.Json.Serialization;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Configuration;

// The TypeScript contracts of the auth endpoints ng-spark reads (commerce X20 item 2,
// docs/guide-typescript-contracts.md), generated into ng-spark's auth/models entry point.
[assembly: SparkClientModule("MintPlayer.Spark.Authorization.Contracts", "@mintplayer/ng-spark/auth/models")]

namespace MintPlayer.Spark.Authorization.Contracts;

/// <summary>
/// What <c>GET /spark/auth/me</c> answers: the signed-in user as the store has it now. An anonymous caller
/// gets only <see cref="IsAuthenticated"/> = <see langword="false"/>; the other members are then left out.
/// </summary>
[SparkContract("AuthUser")]
public sealed record SparkAuthUser(
    bool IsAuthenticated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Email = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Roles = null)
{
    /// <summary>The answer for a caller who is not signed in.</summary>
    public static SparkAuthUser Anonymous { get; } = new(false);
}

/// <summary>An external provider a user can actually click, as the server reports it.</summary>
/// <param name="Scheme">The authentication scheme, which the challenge endpoint takes.</param>
/// <param name="DisplayName">The provider's name for the button; never empty (a scheme without one is not interactive).</param>
[SparkContract]
public sealed record SparkExternalProvider(string Scheme, string DisplayName);

/// <summary>An identifier kind password sign-in can accept.</summary>
[SparkContract]
[JsonConverter(typeof(JsonStringEnumConverter<SparkSignInIdentifier>))]
public enum SparkSignInIdentifier
{
    /// <summary>The email address.</summary>
    [JsonStringEnumMemberName("email")] Email,

    /// <summary>The user name, the public handle.</summary>
    [JsonStringEnumMemberName("userName")] UserName,
}

/// <summary>
/// What <c>GET /spark/auth/capabilities</c> reports: how much of the local-credential surface this application
/// mounts, and which external providers are registered. It exists so the client's route config (a build-time
/// choice) and the server's mode (a deployment-time one) cannot silently disagree, which shows up as a form that
/// posts into a 404 or a sign-in page with no way to sign in. Every flag is derived from the mapped endpoints.
/// </summary>
/// <param name="LocalCredentials">The local-credential mode.</param>
/// <param name="SignInIdentifiers">What password sign-in accepts as the identifier (the server's <c>SignInIdentifiers</c>); empty when <paramref name="LocalCredentials"/> is <c>Disabled</c>.</param>
/// <param name="Passkeys">
/// An anonymous visitor can sign in with a passkey: the passkey sign-in endpoint is mapped. Necessary but not
/// sufficient for the button: the browser must support the ceremony too (<c>passkeysSupported()</c>).
/// </param>
/// <param name="TwoFactor">The two-factor setup page is served: <c>manage/2fa</c> and the authenticator-URI endpoint are mapped.</param>
/// <param name="EmailChange">A signed-in user may change their email address: <c>SparkEmailChange.Enabled</c>, in a mode that maps <c>POST manage/info</c>.</param>
/// <param name="ExternalLogins">The connected-logins page is served (<c>ExternalLoginLinking</c> is not <c>Disabled</c>). External sign-in can be on while this is off.</param>
/// <param name="ExternalLoginTwoFactorBypass">A user with two-factor enabled may skip the code when signing in with an external account (<c>manage/external-login-two-factor</c>).</param>
/// <param name="ExternalProviders">The interactive external providers.</param>
[SparkContract]
public sealed record SparkAuthCapabilities(
    [property: JsonConverter(typeof(JsonStringEnumConverter<SparkLocalCredentials>))] SparkLocalCredentials LocalCredentials,
    IReadOnlyList<SparkSignInIdentifier> SignInIdentifiers,
    bool Passkeys,
    bool TwoFactor,
    bool EmailChange,
    bool ExternalLogins,
    bool ExternalLoginTwoFactorBypass,
    IReadOnlyList<SparkExternalProvider> ExternalProviders);
