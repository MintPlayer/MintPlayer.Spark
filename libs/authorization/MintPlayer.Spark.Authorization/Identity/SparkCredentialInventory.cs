using MintPlayer.Spark.Authorization.Configuration;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Whether an account would still be reachable after removing one of its credentials.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Unlinking the last way in is permanent and silent.</b> A passwordless account whose only
/// external login is removed has nothing left to authenticate with: no password to fall back on and
/// no provider left to prove ownership. Nothing in Identity prevents it, and the person only finds
/// out at their next sign-in, by which point there is no self-service way back — recovery means an
/// operator editing the database. MintPlayer's own account page has this bug
/// (<c>AccountRepository.cs:325-338</c>), which is where the requirement came from.
/// </para>
/// <para>
/// A pure function with the inputs spelled out, rather than a method reaching for a
/// <c>UserManager</c>, because the interesting part is the arithmetic and every branch of it is
/// worth pinning in a test.
/// </para>
/// </remarks>
internal static class SparkCredentialInventory
{
    /// <summary>
    /// Whether removing one external login would leave the account with no way to sign in.
    /// </summary>
    /// <param name="externalLoginCount">How many external logins the account has right now.</param>
    /// <param name="hasPassword">Whether a password hash is set.</param>
    /// <param name="localCredentials">
    /// The application's local-credential mode.
    /// </param>
    /// <remarks>
    /// ⚠️ <b>A password only counts when the application actually serves a way to use it.</b> Under
    /// <see cref="SparkLocalCredentials.Disabled"/> there is no login endpoint, so a stored hash is
    /// an artefact rather than a credential — counting it would let the guard wave through the exact
    /// lockout it exists to prevent, and in the application most likely to hit this, since
    /// external-only is why the account has no password in the first place.
    /// </remarks>
    internal static bool WouldRemoveLastCredential(
        int externalLoginCount,
        bool hasPassword,
        SparkLocalCredentials localCredentials,
        int passkeyCount = 0,
        SparkPasskeys passkeys = SparkPasskeys.Disabled)
    {
        var remaining = Math.Max(externalLoginCount - 1, 0)
            + (IsPasswordUsable(hasPassword, localCredentials) ? 1 : 0)
            + UsablePasskeys(passkeyCount, passkeys);

        return remaining == 0;
    }

    /// <summary>
    /// Whether removing one passkey would leave the account with no way to sign in.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="WouldRemoveLastCredential"/>, and it exists for the same reason: an
    /// account whose only credential is a passkey is exactly the account this guard protects, and
    /// that shape is reachable as soon as an application offers passkeys without passwords.
    /// </remarks>
    internal static bool WouldRemoveLastPasskey(
        int passkeyCount,
        int externalLoginCount,
        bool hasPassword,
        SparkLocalCredentials localCredentials,
        SparkPasskeys passkeys)
    {
        var remaining = externalLoginCount
            + (IsPasswordUsable(hasPassword, localCredentials) ? 1 : 0)
            + Math.Max(UsablePasskeys(passkeyCount, passkeys) - 1, 0);

        return remaining == 0;
    }

    private static bool IsPasswordUsable(bool hasPassword, SparkLocalCredentials localCredentials)
        => hasPassword && localCredentials != SparkLocalCredentials.Disabled;

    /// <summary>
    /// ⚠️ Passkeys count only while the application still mounts the passkey sign-in route, for the
    /// same reason a password stops counting under <see cref="SparkLocalCredentials.Disabled"/>: an
    /// enrolled credential with no endpoint to present it to is an artefact, not a way in. Turning
    /// <see cref="SparkPasskeys"/> off would otherwise let this guard wave through the very lockout
    /// it exists to prevent.
    /// </summary>
    private static int UsablePasskeys(int passkeyCount, SparkPasskeys passkeys)
        => passkeys == SparkPasskeys.Enabled ? Math.Max(passkeyCount, 0) : 0;
}
