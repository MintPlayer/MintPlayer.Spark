using ExternalLoginErrors = MintPlayer.Spark.Authorization.Extensions.SparkAuthenticationExtensions.ExternalLoginErrors;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.ExternalLogin;

/// <summary>
/// Handles the OAuth callback: signs in an existing external login, or provisions an account when
/// the issuer attested the email.
/// </summary>
/// <remarks>
/// ⚠️ Anonymous by necessity — the caller is arriving from the provider and has no session yet. The
/// route sits under <c>/spark/auth</c> like every other member of the group; it was previously
/// mapped on the root builder "to avoid group-level auth configuration from MapIdentityApi", which
/// was never true: <c>MapIdentityApi</c> is applied to its own separate <c>RouteGroupBuilder</c> and
/// could not reach this group.
/// </remarks>
[MemberOf<SparkAuthGroup>]
internal sealed partial class ExternalLoginCallback<TUser> : IGetEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/external-login-callback";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.AllowAnonymous();
    }

    [Inject] private readonly SignInManager<TUser> signInManager;
    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly SparkExternalLoginLinker<TUser> linker;
    [Inject] private readonly IOptions<SparkAuthenticationOptions> options;
    [Inject] private readonly IAntiforgery antiforgery;
    [Inject] private readonly SparkAccountMail<TUser> accountMail;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var returnUrlRaw = httpContext.Request.Query["returnUrl"].ToString();

        // R2-C4: returnUrl is interpolated into the response below, so anything other than a
        // relative in-app path is a vector for XSS (and at minimum open-redirect after successful
        // OAuth). Sanitize first.
        var safeReturnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(
            string.IsNullOrEmpty(returnUrlRaw) ? null : returnUrlRaw);

        var info = await signInManager.GetExternalLoginInfoAsync();
        if (info is null)
            return SparkAuthenticationExtensions.ExternalLoginOutcome(httpContext, safeReturnUrl, ExternalLoginErrors.NoLoginInfo);

        // Try signing in with existing external login
        var result = await signInManager.ExternalLoginSignInAsync(
            info.LoginProvider, info.ProviderKey, isPersistent: true);

        TUser? user;
        if (result.Succeeded)
        {
            user = await userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        }
        else if (await userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey) is not null)
        {
            // ⚠️ 4h: the login IS attached, so this is a refusal — lockout, two-factor, or a
            // confirmation requirement — not a first-time sign-in. Falling through to provisioning
            // treated a locked-out user as a stranger, and the branch below would then answer about
            // their email rather than about why they were refused. Worse, a lockout is a deliberate
            // security response and silently routing around it into an account-creation path is the
            // last thing that should happen to one.
            var refusal = result.IsLockedOut ? ExternalLoginErrors.LockedOut
                : result.RequiresTwoFactor ? ExternalLoginErrors.RequiresTwoFactor
                : result.IsNotAllowed ? ExternalLoginErrors.NotAllowed
                : ExternalLoginErrors.SignInRefused;

            return SparkAuthenticationExtensions.ExternalLoginOutcome(httpContext, safeReturnUrl, refusal);
        }
        else
        {
            // 4g/R2-H11: only provision when the issuer attested the email. An unverified address
            // from a provider is a *claim*, not a proof — anyone who can assert your address at some
            // IdP could otherwise squat your identity here.
            //
            // ⚠️ One claim, `email_verified`, for every provider. It used to accept a
            // GitHub-specific `urn:github:` claim alongside it, which would have meant a new
            // vocabulary term per forge — and a forge whose term nobody remembered to add would fail
            // closed in a way that reads like a broken provider.
            //
            // ⚠️ And it is the *only* check there is. D23 dropped the account-confirmation mail for
            // externally provisioned users, on the grounds that the SSO already proves the person
            // controls the address — which is true exactly when the provider says the address is
            // verified. So there is no longer a second chance to establish this later, and the gate
            // must fail closed: a provider that does not say counts as not verified. A forge that
            // cannot report it must not be trusted to assert identity by email at all.
            //
            // #460 D7: *which* claim carries the signal, and whether a provider has one at all, is
            // the provider preset's declaration (SparkExternalProviderPolicy). A scheme nobody
            // described keeps the rule above: email_verified=true or no account. A provider that
            // declares it has no reliable signal gets an unconfirmed account and a confirmation mail
            // instead — the mail is then the proof the SSO could not give.
            var policy = SparkExternalProviderPolicies.For(httpContext.RequestServices, info.LoginProvider);
            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            var verification = policy.EmailVerification(info.Principal);

            if (string.IsNullOrEmpty(email) || verification == SparkEmailVerification.Unverified)
                return SparkAuthenticationExtensions.ExternalLoginOutcome(httpContext, safeReturnUrl, ExternalLoginErrors.EmailNotVerified);

            var providerHandle = info.Principal.FindFirstValue(ClaimTypes.Name)
                ?? info.Principal.FindFirstValue(ClaimTypes.NameIdentifier);

            // 4c: the address may already belong to somebody. Asked *before* provisioning rather
            // than inferred from a failed CreateAsync, because the store's DuplicateEmail arrives
            // with no user attached and is indistinguishable from a validation failure — which is
            // how this case used to surface as "account_creation_failed", a message that reads as
            // "this application is broken" rather than "you already have an account".
            var existing = await userManager.FindByEmailAsync(email);
            if (existing is not null)
            {
                return await SparkAuthenticationExtensions.LinkOrRefuseAsync(
                    httpContext, signInManager, userManager, linker, options, antiforgery,
                    existing, info, providerHandle, safeReturnUrl);
            }

            // #460 D7: a slug of the display name (john-doe, john-doe-2, …), never the email's local
            // part — unless the preset declares its name claim a unique handle the app relies on.
            var userName = policy.UserName == SparkUserNameSource.ProviderHandle
                ? providerHandle
                : await UniqueSlugAsync(info.Principal.FindFirstValue(ClaimTypes.Name) ?? providerHandle);

            user = new TUser();
            await userManager.SetUserNameAsync(user, userName);
            await userManager.SetEmailAsync(user, email);
            user.RegistrationMethod = SparkRegistrationMethods.External(info.LoginProvider);

            // 4f: set because the provider *said the address is verified* — a fact about the token,
            // checked immediately above — and not by fiat. The distinction is the whole of 4g:
            // writing `true` unconditionally would make the field mean nothing, and it is the field
            // the next feature will trust.
            //
            // D23: no confirmation mail follows a verified address. The SSO already established what
            // one would have. A NoSignal provider (D7) is the exception, handled after creation.
            user.EmailConfirmed = verification == SparkEmailVerification.Verified;

            var createResult = await userManager.CreateAsync(user);
            if (!createResult.Succeeded)
                return SparkAuthenticationExtensions.ExternalLoginOutcome(httpContext, safeReturnUrl, ExternalLoginErrors.AccountCreationFailed);

            // ⚠️ 4h: both results were discarded. A failed AddLoginAsync leaves an account with no
            // credential attached — unreachable by anyone, and holding the email reservation so the
            // same person cannot even try again. The account exists only because of this request and
            // has nothing in it, so the honest repair is to undo it rather than leave a tombstone
            // that blocks the address forever.
            var linkResult = await userManager.AddLoginAsync(user, info);
            if (!linkResult.Succeeded)
            {
                await userManager.DeleteAsync(user);
                return SparkAuthenticationExtensions.ExternalLoginOutcome(httpContext, safeReturnUrl, ExternalLoginErrors.AccountCreationFailed);
            }

            if (!user.EmailConfirmed)
            {
                await accountMail.SendConfirmationAsync(httpContext, user, email);

                // SignInAsync does not consult RequireConfirmedEmail (only the password and external
                // sign-in paths do), so the option is honoured here explicitly.
                if (options.Value.RequireConfirmedEmail)
                    return SparkAuthenticationExtensions.ExternalLoginOutcome(httpContext, safeReturnUrl, ExternalLoginErrors.ConfirmEmailSent);
            }

            await signInManager.SignInAsync(user, isPersistent: true);
        }

        // Store OAuth tokens for later API use
        if (user is not null && info.AuthenticationTokens is not null)
        {
            foreach (var token in info.AuthenticationTokens)
            {
                await userManager.SetAuthenticationTokenAsync(
                    user, info.LoginProvider, token.Name, token.Value);
            }
        }

        // Ensure antiforgery cookie is set before redirect
        antiforgery.GetAndStoreTokens(httpContext);

        // R2-C4: Server-side redirect instead of HTML interpolation. The previous implementation
        // built an HTML page with returnUrl as a raw JS string literal — even after
        // SanitizeReturnUrl removes the worst-case XSS payloads, the popup branch
        // (window.opener.postMessage) is unaffected (no caller-data interpolation), and the
        // non-popup branch is now a standard server redirect that the framework HTML-encodes for us.
        return SparkAuthenticationExtensions.ExternalLoginOutcome(httpContext, safeReturnUrl, error: null);
    }

    /// <summary><c>slug</c>, else <c>slug-2</c>, <c>slug-3</c>, … — the first user name nobody holds.</summary>
    private async Task<string> UniqueSlugAsync(string? displayName)
    {
        var slug = SparkExternalProviderPolicy.Slugify(displayName);
        if (await userManager.FindByNameAsync(slug) is null)
            return slug;

        for (var n = 2; n <= 100; n++)
        {
            var candidate = $"{slug}-{n}";
            if (await userManager.FindByNameAsync(candidate) is null)
                return candidate;
        }

        // A hundred people with one display name: stop probing and take a random suffix.
        return $"{slug}-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4))}";
    }
}
