using Microsoft.AspNetCore.Identity;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// The user-name rule that makes "sign in with email or user name" unambiguous (#460, D4):
/// <b>a user name containing <c>@</c> must equal that user's own email</b> (case-insensitively).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SparkSignInManager{TUser}"/> looks an identifier containing <c>@</c> up as an email
/// first and as a user name only when no account has that email. Without this rule, account B could
/// take a user name equal to account A's email, and the day A changes address, "A's old email" would
/// start signing into B. With it, the only user name that can look like an email is the owner's own
/// email, so the two namespaces cannot collide by construction.
/// </para>
/// <para>
/// Runs on every create and update, alongside Identity's own <see cref="UserValidator{TUser}"/>
/// (uniqueness, allowed characters). <see cref="SparkUserManager{TUser}"/> keeps an email-shaped user
/// name in step when the email changes, so a confirmed email change does not trip the rule.
/// </para>
/// </remarks>
public class SparkUserNameValidator<TUser> : IUserValidator<TUser>
    where TUser : SparkUser
{
    /// <summary>The <see cref="IdentityError.Code"/> this validator reports.</summary>
    public const string ErrorCode = "UserNameMustMatchEmail";

    public Task<IdentityResult> ValidateAsync(UserManager<TUser> manager, TUser user)
    {
        var userName = user.UserName;
        if (string.IsNullOrEmpty(userName) || !userName.Contains('@'))
            return Task.FromResult(IdentityResult.Success);

        if (string.Equals(userName, user.Email, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(IdentityResult.Success);

        return Task.FromResult(IdentityResult.Failed(new IdentityError
        {
            Code = ErrorCode,
            Description = "A user name may contain '@' only when it is the account's own email address.",
        }));
    }
}
