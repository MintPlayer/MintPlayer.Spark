namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>
/// What a person may type into the identifier field of a password sign-in — their email address,
/// their user name, or either. The password is always required.
/// </summary>
/// <remarks>
/// <para>
/// The two kinds never overlap: a user name never contains <c>@</c>
/// (<see cref="Identity.SparkUserNameValidator{TUser}"/>, G-Q22), so an identifier with <c>@</c> is
/// an email and one without is a user name. <see cref="Identity.SparkSignInManager{TUser}"/> decides
/// the kind from that alone and refuses a kind the application did not allow exactly as it refuses an
/// unknown account.
/// </para>
/// <para>
/// Only password sign-in reads this. Passkeys name no account, external logins are the provider's,
/// and the password-recovery family (<c>forgotPassword</c>, <c>resetPassword</c>) is addressed by
/// email in every setting — the reset link has to be mailed somewhere.
/// </para>
/// </remarks>
[Flags]
public enum SparkSignInIdentifiers
{
    /// <summary>The email address.</summary>
    Email = 1,

    /// <summary>The user name — the public handle.</summary>
    UserName = 2,
}
