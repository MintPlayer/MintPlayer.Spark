using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Tests.Authorization.Identity;

/// <summary>
/// The arithmetic behind "you cannot remove your last way in".
/// </summary>
/// <remarks>
/// Worth a table of its own because getting it wrong is not recoverable by the person it happens
/// to: a passwordless account whose only external login is removed has nothing left to authenticate
/// with, and no self-service route back.
/// </remarks>
public class SparkCredentialInventoryTests
{
    [Theory]
    // One login, no password: the whole point of the guard.
    [InlineData(1, false, SparkLocalCredentials.Disabled, true)]
    [InlineData(1, false, SparkLocalCredentials.Full, true)]
    // Two logins: removing one leaves one.
    [InlineData(2, false, SparkLocalCredentials.Disabled, false)]
    // A password is a fallback — but only where it can be used.
    [InlineData(1, true, SparkLocalCredentials.Full, false)]
    [InlineData(1, true, SparkLocalCredentials.SignInOnly, false)]
    [InlineData(1, true, SparkLocalCredentials.Disabled, true)]
    public void Removing_one_login_leaves_the_account_reachable_or_not(
        int logins, bool hasPassword, SparkLocalCredentials mode, bool expected)
        => SparkCredentialInventory.WouldRemoveLastCredential(logins, hasPassword, mode)
            .Should().Be(expected);

    /// <summary>
    /// ⚠️ The case a simpler guard gets wrong, called out on its own because it is the one that
    /// matters most.
    /// </summary>
    /// <remarks>
    /// Under <see cref="SparkLocalCredentials.Disabled"/> there is no login endpoint, so a stored
    /// password hash is an artefact rather than a credential. A guard that counted it would wave
    /// through exactly the lockout it exists to prevent — and it would do so in the application
    /// most likely to hit this, since external-only login is why the account has no usable password
    /// in the first place.
    /// </remarks>
    [Fact]
    public void A_password_nobody_can_use_is_not_a_credential()
    {
        SparkCredentialInventory.WouldRemoveLastCredential(1, hasPassword: true,
            SparkLocalCredentials.Disabled).Should().BeTrue();

        SparkCredentialInventory.WouldRemoveLastCredential(1, hasPassword: true,
            SparkLocalCredentials.Full).Should().BeFalse(
            "the same account is fine the moment the application serves a password login");
    }

    /// <summary>
    /// An account with nothing attached cannot have a login removed, and the answer must not go
    /// negative and read as "fine".
    /// </summary>
    [Fact]
    public void An_account_with_no_logins_is_not_reported_as_safe_to_unlink()
        => SparkCredentialInventory.WouldRemoveLastCredential(0, hasPassword: false,
            SparkLocalCredentials.Disabled).Should().BeTrue();
}
