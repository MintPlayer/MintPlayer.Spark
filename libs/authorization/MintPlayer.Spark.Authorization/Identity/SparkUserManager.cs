using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Spark's <see cref="UserManager{TUser}"/>. One behaviour differs from Identity's: a confirmed email
/// change also moves a user name that <em>was</em> the old email (#460, D4).
/// </summary>
/// <remarks>
/// Identity changes the email and saves, then — in <c>MapIdentityApi</c>'s confirm step — sets the
/// user name in a second save. Between the two, the account holds an email-shaped user name that is
/// not its email, which <see cref="SparkUserNameValidator{TUser}"/> refuses, so the first save would
/// fail and the change could never complete. Moving both in the one save keeps the rule true at
/// every write. A user name that is not the old email (a chosen handle) is left alone.
/// </remarks>
public class SparkUserManager<TUser> : UserManager<TUser>
    where TUser : SparkUser
{
    public SparkUserManager(
        IUserStore<TUser> store,
        IOptions<IdentityOptions> optionsAccessor,
        IPasswordHasher<TUser> passwordHasher,
        IEnumerable<IUserValidator<TUser>> userValidators,
        IEnumerable<IPasswordValidator<TUser>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services,
        ILogger<UserManager<TUser>> logger)
        : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
    {
    }

    /// <inheritdoc />
    public override async Task<IdentityResult> ChangeEmailAsync(TUser user, string newEmail, string token)
    {
        ArgumentNullException.ThrowIfNull(user);

        var followsEmail = !string.IsNullOrEmpty(user.UserName)
            && string.Equals(user.UserName, user.Email, StringComparison.OrdinalIgnoreCase);

        var previousUserName = user.UserName;
        var previousNormalizedUserName = user.NormalizedUserName;

        if (followsEmail)
        {
            // Set on the entity only; base.ChangeEmailAsync verifies the token and performs the one
            // save that carries both changes (and runs the validators against the final state).
            user.UserName = newEmail;
            user.NormalizedUserName = NormalizeName(newEmail);
        }

        var result = await base.ChangeEmailAsync(user, newEmail, token);

        if (!result.Succeeded && followsEmail)
        {
            // Nothing was saved; undo the in-memory change so a later save in the same scope cannot
            // persist a user name the token never authorized.
            user.UserName = previousUserName;
            user.NormalizedUserName = previousNormalizedUserName;
        }

        return result;
    }
}
