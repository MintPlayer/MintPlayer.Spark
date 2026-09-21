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
        SparkLocalCredentials localCredentials)
    {
        var passwordIsUsable = hasPassword && localCredentials != SparkLocalCredentials.Disabled;
        var remaining = Math.Max(externalLoginCount - 1, 0) + (passwordIsUsable ? 1 : 0);
        return remaining == 0;
    }
}
