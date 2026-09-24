namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// A WebAuthn credential (passkey) enrolled by a user, embedded in the user document.
/// <para>
/// This mirrors <see cref="Microsoft.AspNetCore.Identity.UserPasskeyInfo"/> field for field. It is
/// deliberately <em>not</em> <c>IdentityUserPasskey&lt;TKey&gt;</c>: that type ships in
/// <c>Microsoft.Extensions.Identity.Stores</c> shaped for an EF <c>DbSet</c>, carrying a
/// <c>UserId</c> back-pointer that an embedded document has no use for.
/// </para>
/// <para>
/// None of this is secret. A passkey's public key is public by construction — the private half never
/// leaves the authenticator — so a database dump discloses no credential material. That is the whole
/// point of the credential type, and it is why these fields are stored in the clear where
/// <see cref="SparkUser.TwoFactorRecoveryCodes"/> are not.
/// </para>
/// </summary>
public class SparkUserPasskey
{
    /// <summary>
    /// The credential id, as issued by the authenticator. Unique across all users — enforced by a
    /// compare/exchange reservation rather than by a query, so the guarantee is cluster-wide.
    /// </summary>
    public byte[] CredentialId { get; set; } = [];

    /// <summary>The COSE-encoded public key the assertion signature is verified against.</summary>
    public byte[] PublicKey { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Transports the authenticator advertised (<c>usb</c>, <c>nfc</c>, <c>ble</c>,
    /// <c>internal</c>, <c>hybrid</c>). A hint for the browser, never a security control.
    /// </summary>
    public string[] Transports { get; set; } = [];

    public byte[] AttestationObject { get; set; } = [];
    public byte[] ClientDataJson { get; set; } = [];

    /// <summary>
    /// Whether the authenticator said this credential <em>may</em> be backed up (synced). Fixed at
    /// registration; <see cref="IsBackedUp"/> is the part that changes.
    /// </summary>
    public bool IsBackupEligible { get; set; }

    /// <summary>A human label, chosen by the user. Never used to identify the credential.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The authenticator's signature counter.
    /// <para>
    /// ⚠️ This is a clone-detection signal, and it must be re-persisted on every successful
    /// assertion or the detection is worthless. A counter that goes <em>backwards</em> from a
    /// non-zero baseline means two authenticators hold the same credential. A counter that stays at
    /// zero means nothing at all — synced passkeys (iCloud Keychain and friends) never increment it,
    /// so requiring an increase would lock those users out permanently.
    /// </para>
    /// </summary>
    public uint SignCount { get; set; }

    /// <summary>Whether user verification (PIN, biometric) was performed on the last ceremony.</summary>
    public bool IsUserVerified { get; set; }

    /// <summary>Whether the credential is currently backed up. Changes over the credential's life.</summary>
    public bool IsBackedUp { get; set; }
}
