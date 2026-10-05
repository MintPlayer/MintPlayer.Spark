using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Configuration;
using System.Globalization;
using System.Security.Claims;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Spark's <see cref="SignInManager{TUser}"/>: password sign-in accepts an <b>email address or a
/// user name</b> (#460, D4) — whichever of the two
/// <see cref="SparkAuthenticationOptions.SignInIdentifiers"/> allows — and every sign-in records
/// when it happened so that sensitive account operations can ask for a recent one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The resolution rule</b> (<see cref="FindUserForSignInAsync"/>): an identifier containing
/// <c>@</c> is an email and is looked up only by email; an identifier without <c>@</c> is a user name
/// and is looked up only by user name. Exactly one lookup, so a wrong password never falls through to
/// a second candidate — the password is checked against one account alone.
/// </para>
/// <para>
/// The <c>@</c> decides the kind because of <see cref="SparkUserNameValidator{TUser}"/>: a user name
/// never contains <c>@</c> (G-Q22). There used to be a fallback from an unmatched email to a user
/// name, for accounts that predate the rule; <see cref="Migrations.M_202610051200_UserNamesAreNotEmails"/>
/// gives those a handle, so it is gone, and an <c>@</c>-shaped user name left over from before the
/// migration cannot sign in by that name.
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
    private readonly SparkSignInIdentifiers signInIdentifiers;

    public SparkSignInManager(
        UserManager<TUser> userManager,
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<TUser> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor,
        ILogger<SignInManager<TUser>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<TUser> confirmation,
        TimeProvider? timeProvider = null,
        IOptions<SparkAuthenticationOptions>? authenticationOptions = null)
        : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        signInIdentifiers = authenticationOptions?.Value.SignInIdentifiers
            ?? SparkSignInIdentifiers.Email | SparkSignInIdentifiers.UserName;
    }

    /// <summary>
    /// Resolves the account a sign-in identifier names: by email when it contains <c>@</c>, by user
    /// name otherwise — and <see langword="null"/>, without a lookup, when
    /// <see cref="SparkAuthenticationOptions.SignInIdentifiers"/> does not allow that kind.
    /// </summary>
    /// <remarks>
    /// A disallowed kind returns what an unknown account returns, so the caller answers both with the
    /// same failure. Skipping the lookup makes the refusal a little faster than an unknown account's,
    /// which tells a prober only which kinds are allowed — what <c>/spark/auth/capabilities</c>
    /// publishes anyway — and nothing about any account.
    /// </remarks>
    public virtual async Task<TUser?> FindUserForSignInAsync(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return null;

        identifier = identifier.Trim();

        if (identifier.Contains('@'))
        {
            return signInIdentifiers.HasFlag(SparkSignInIdentifiers.Email)
                ? await UserManager.FindByEmailAsync(identifier)
                : null;
        }

        return signInIdentifiers.HasFlag(SparkSignInIdentifiers.UserName)
            ? await UserManager.FindByNameAsync(identifier)
            : null;
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
