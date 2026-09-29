using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Security.Claims;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Spark's <see cref="SignInManager{TUser}"/>: password sign-in accepts an <b>email address or a
/// user name</b> (#460, D4), and every sign-in records when it happened so that sensitive account
/// operations can ask for a recent one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The resolution rule</b> (<see cref="FindUserForSignInAsync"/>): an identifier containing
/// <c>@</c> is looked up as an email first, and only when <em>no account</em> has that email is it
/// looked up as a user name; an identifier without <c>@</c> is only ever a user name. A wrong
/// password never falls through to a second candidate — the lookup picks one account and the
/// password is checked against that account alone, so an identifier can never be used to try a
/// password against two accounts.
/// </para>
/// <para>
/// The fallback is safe because of <see cref="SparkUserNameValidator{TUser}"/>: a user name that
/// contains <c>@</c> must equal that user's own email, so "email of account A" and "user name of
/// account B" cannot be the same string. The fallback exists only for accounts that predate the
/// rule.
/// </para>
/// <para>
/// Two-factor and recovery-code steps need nothing extra: the password step stores the
/// <em>resolved</em> user for the second factor, exactly as it does for Identity's own lookup.
/// </para>
/// </remarks>
public class SparkSignInManager<TUser> : SignInManager<TUser>
    where TUser : SparkUser
{
    /// <summary>
    /// The <see cref="AuthenticationProperties.Items"/> key holding the UTC instant of the sign-in
    /// that produced the current session (round-trip "O" format).
    /// </summary>
    /// <remarks>
    /// Kept in the ticket's properties rather than a claim: the security-stamp validator rebuilds
    /// the principal every interval but keeps the properties, and a refresh
    /// (<see cref="SignInManager{TUser}.RefreshSignInAsync"/>) passes the existing properties back
    /// in — so the value survives both and still means "when the person last proved who they are".
    /// A bearer <c>/refresh</c> issues a new ticket without it, which is correct: a refresh is not a
    /// re-authentication.
    /// </remarks>
    public const string AuthenticatedAtItem = ".spark.authenticated_at";

    private readonly TimeProvider timeProvider;

    public SparkSignInManager(
        UserManager<TUser> userManager,
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<TUser> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor,
        ILogger<SignInManager<TUser>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<TUser> confirmation,
        TimeProvider? timeProvider = null)
        : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Resolves the account a sign-in identifier names: email first when it contains <c>@</c>, user
    /// name otherwise, and user name as a fallback only when no account has that email.
    /// </summary>
    public virtual async Task<TUser?> FindUserForSignInAsync(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return null;

        identifier = identifier.Trim();

        if (identifier.Contains('@'))
        {
            var byEmail = await UserManager.FindByEmailAsync(identifier);
            if (byEmail is not null)
                return byEmail;
        }

        return await UserManager.FindByNameAsync(identifier);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Identity's own overload looks the identifier up as a user name only; this one applies
    /// <see cref="FindUserForSignInAsync"/>. Every caller of the string overload —
    /// <c>MapIdentityApi</c>'s <c>/login</c> and the OIDC <c>/connect/login</c> page — therefore
    /// shares one resolver.
    /// </remarks>
    public override async Task<SignInResult> PasswordSignInAsync(
        string userName, string password, bool isPersistent, bool lockoutOnFailure)
    {
        var user = await FindUserForSignInAsync(userName);
        if (user is null)
            return SignInResult.Failed;

        return await PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stamps <see cref="AuthenticatedAtItem"/> when the properties do not carry one yet. Every
    /// sign-in — password, second factor, recovery code, external login, passkey — ends here with
    /// fresh properties; a refresh arrives with the existing ones and keeps its original instant.
    /// </remarks>
    public override Task SignInWithClaimsAsync(
        TUser user, AuthenticationProperties? authenticationProperties, IEnumerable<Claim> additionalClaims)
    {
        authenticationProperties ??= new AuthenticationProperties();
        if (!authenticationProperties.Items.ContainsKey(AuthenticatedAtItem))
        {
            authenticationProperties.Items[AuthenticatedAtItem] =
                timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        return base.SignInWithClaimsAsync(user, authenticationProperties, additionalClaims);
    }

    /// <summary>
    /// When the current session's sign-in happened, or <see langword="null"/> when the request is
    /// not signed in or its ticket predates the stamp.
    /// </summary>
    /// <remarks>
    /// Reads the cookie scheme first and the bearer scheme second — the two credentials Spark's
    /// Identity wiring issues.
    /// </remarks>
    public virtual async Task<DateTimeOffset?> GetAuthenticatedAtAsync()
    {
        foreach (var scheme in new[] { IdentityConstants.ApplicationScheme, IdentityConstants.BearerScheme })
        {
            AuthenticateResult result;
            try
            {
                result = await Context.AuthenticateAsync(scheme);
            }
            catch (InvalidOperationException)
            {
                // The scheme is not registered in this host.
                continue;
            }

            if (!result.Succeeded || result.Properties is null)
                continue;

            if (result.Properties.Items.TryGetValue(AuthenticatedAtItem, out var raw)
                && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            {
                return at;
            }

            return null;
        }

        return null;
    }
}
