using Microsoft.AspNetCore.Identity;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// The framework's user-name rule: <b>a user name never contains <c>@</c></b> (#264, G-Q22).
/// </summary>
/// <remarks>
/// <para>
/// The user name is the account's <em>public handle</em>: a reference label (<c>{UserName}</c>
/// breadcrumb), a history or contribution author, an OIDC <c>preferred_username</c>. An email address
/// must never reach another user's browser, and refusing <c>@</c> is what keeps one out of the handle,
/// whichever path sets it — registration, an external sign-up, the profile page, an application's own
/// admin code.
/// </para>
/// <para>
/// It also keeps "sign in with email or user name" unambiguous (#460, D4): an identifier containing
/// <c>@</c> can only be an email, so account B can never hold a user name equal to account A's address.
/// </para>
/// <para>
/// Runs on every create and update, alongside Identity's own <see cref="UserValidator{TUser}"/>
/// (uniqueness, allowed characters). An application's <c>AllowedUserNameCharacters</c> cannot switch
/// it off. Accounts created before the rule are given a handle once by
/// <see cref="Migrations.M_202610051200_UserNamesAreNotEmails"/>.
/// </para>
/// </remarks>
public class SparkUserNameValidator<TUser> : IUserValidator<TUser>
    where TUser : SparkUser
{
    /// <summary>The <see cref="IdentityError.Code"/> this validator reports.</summary>
    public const string ErrorCode = "UserNameContainsAt";

    public Task<IdentityResult> ValidateAsync(UserManager<TUser> manager, TUser user)
    {
        if (user.UserName is not { } userName || !userName.Contains('@'))
            return Task.FromResult(IdentityResult.Success);

        return Task.FromResult(IdentityResult.Failed(new IdentityError
        {
            Code = ErrorCode,
            Description = "A user name cannot contain '@'. It is shown to other users, so it must not be an email address.",
        }));
    }
}

internal static class SparkUserNames
{
    /// <summary>
    /// A generated handle, <c>user-</c> and six hex digits, for an account with nothing better to go
    /// on (an external sign-up whose only name is an email, a pre-G-Q22 account). Callers check it is free.
    /// </summary>
    internal static string NewHandle()
        => $"user-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3))}";
}
