using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Authorization.Contracts;

// The account and sign-in responses that used to be anonymous objects (commerce X20 item 2,
// docs/guide-typescript-contracts.md). Each record serializes exactly as the anonymous object did: the same
// property names (camelCase from the web defaults), the same order, nulls written. SparkAccountContractsTests
// pins every shape against the anonymous object it replaced, because CodeCoverage and SeoTester run on them.

/// <summary>
/// The body of an auth endpoint's refusal that carries only a Spark error code, e.g.
/// <c>{"error":"last_credential"}</c> (unlink), <c>{"error":"passkey_failed"}</c> (passkey sign-in) or
/// <c>{"error":"reauthentication_required"}</c> (account deletion).
/// </summary>
/// <param name="Error">The error code; the client switches on it.</param>
[SparkContract]
public sealed record SparkAuthError(string Error);

/// <summary>One external login attached to the signed-in account (<c>GET /spark/auth/external-logins</c>).</summary>
/// <param name="Provider">The authentication scheme.</param>
/// <param name="ProviderKey">The provider's id for the user.</param>
/// <param name="DisplayName">The provider's display name, or the scheme when the login carries none.</param>
/// <param name="CanUnlink">
/// ⚠️ <see langword="false"/> when removing this login would leave the account with no way to sign in. Served so the
/// UI can disable the control instead of offering an action that will be refused. It is not the enforcement: the
/// unlink endpoint refuses the request too.
/// </param>
[SparkContract]
public sealed record SparkLinkedExternalLogin(string Provider, string ProviderKey, string DisplayName, bool CanUnlink);

/// <summary>A provider that is configured but not yet attached to the signed-in account.</summary>
[SparkContract]
public sealed record SparkAvailableExternalProvider(string Provider, string DisplayName);

/// <summary>What <c>GET /spark/auth/external-logins</c> answers: the linked logins and the providers still available.</summary>
[SparkContract]
public sealed record SparkExternalLogins(IReadOnlyList<SparkLinkedExternalLogin> Linked, IReadOnlyList<SparkAvailableExternalProvider> Available);

/// <summary>What <c>POST /spark/auth/external-logins/unlink</c> answers on success: <c>{"unlinked":true}</c>.</summary>
[SparkContract]
public sealed record SparkExternalLoginUnlinked(bool Unlinked)
{
    /// <summary>The only value the endpoint sends.</summary>
    public static SparkExternalLoginUnlinked Done { get; } = new(true);
}

/// <summary>What <c>POST /spark/auth/passkeys/sign-in</c> answers on success: <c>{"success":true}</c>.</summary>
[SparkContract]
public sealed record SparkPasskeySignedIn(bool Success)
{
    /// <summary>The only value the endpoint sends.</summary>
    public static SparkPasskeySignedIn Done { get; } = new(true);
}

/// <summary>
/// What the agent-grant and connected-application revoke endpoints answer on success: <c>{"revoked":true}</c>
/// (<c>POST /spark/auth/manage/agents/revoke</c>, <c>…/agents/applications/revoke</c>).
/// </summary>
[SparkContract]
public sealed record SparkGrantRevoked(bool Revoked)
{
    /// <summary>The only value the endpoints send.</summary>
    public static SparkGrantRevoked Done { get; } = new(true);
}

/// <summary>
/// What a phone-verification send or verify answers when it succeeded (<c>POST /spark/auth/manage/phone/send</c>,
/// <c>…/phone/verify</c>). Every refusal is a problem document with the same <c>outcome</c> extension.
/// </summary>
/// <param name="Outcome"><c>codeSent</c>, <c>verified</c> or <c>alreadyVerified</c>: the status, camelCased.</param>
/// <param name="PhoneNumber">The E.164 number the outcome is about.</param>
/// <param name="ExpiresAtUtc">When a sent code stops being valid; null for a verify.</param>
[SparkContract]
public sealed record SparkPhoneVerificationOutcome(string Outcome, string? PhoneNumber, DateTime? ExpiresAtUtc);

/// <summary>
/// What <c>GET /spark/auth/manage/personal-data</c> answers, as a download: what the account holds about the
/// person, never credentials, hashes, keys or tokens, plus each personal-data contributor's data by its name.
/// </summary>
/// <param name="Account">The account itself.</param>
/// <param name="Data">Each <c>ISparkPersonalDataContributor</c>'s data, by the contributor's name; its shape is the contributor's.</param>
[SparkContract]
public sealed record SparkPersonalData(SparkPersonalDataAccount Account, IReadOnlyDictionary<string, object?> Data);

/// <summary>The account part of <see cref="SparkPersonalData"/>.</summary>
/// <param name="HasPassword">Whether a password exists, never the hash. The deletion form asks for the password only when there is one.</param>
[SparkContract]
public sealed record SparkPersonalDataAccount(
    string? Id,
    string? UserName,
    string? Email,
    bool EmailConfirmed,
    string? PhoneNumber,
    bool PhoneNumberConfirmed,
    bool TwoFactorEnabled,
    bool HasPassword,
    DateTime? CreatedAtUtc,
    string? RegistrationMethod,
    string? PreferredCulture,
    string? PreferredTimeZone,
    IReadOnlyList<string> Roles,
    IReadOnlyList<SparkPersonalDataClaim> Claims,
    IReadOnlyList<SparkPersonalDataExternalLogin> ExternalLogins,
    IReadOnlyList<SparkPersonalDataPasskey> Passkeys);

/// <summary>One claim of the account.</summary>
[SparkContract]
public sealed record SparkPersonalDataClaim(string Type, string Value);

/// <summary>One external login of the account; never the provider's key.</summary>
[SparkContract]
public sealed record SparkPersonalDataExternalLogin(string Provider, string? DisplayName);

/// <summary>One passkey of the account: its name and when it was registered, never its key material.</summary>
[SparkContract]
public sealed record SparkPersonalDataPasskey(string? Name, DateTimeOffset CreatedAt);
