using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.AspNetCore.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.WebUtilities;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using System.Security.Claims;

namespace MintPlayer.Spark.Authorization.Extensions;

internal static class SparkAuthenticationExtensions
{
    /// <summary>
    /// Adds Spark authentication with RavenDB-backed identity stores.
    /// Registers ASP.NET Core Identity services, bearer + cookie authentication,
    /// and the RavenDB user/role stores.
    /// <example>
    /// <code>
    /// // Basic usage:
    /// builder.Services.AddSparkAuthentication&lt;AppUser&gt;();
    ///
    /// // With identity options:
    /// builder.Services.AddSparkAuthentication&lt;AppUser&gt;(options =&gt;
    /// {
    ///     options.Password.RequireDigit = true;
    ///     options.Lockout.MaxFailedAccessAttempts = 5;
    /// });
    ///
    /// // With external login providers:
    /// builder.Services
    ///     .AddSparkAuthentication&lt;AppUser&gt;()
    ///     .AddGoogle(o =&gt; builder.Configuration.GetSection("Authentication:Google").Bind(o))
    ///     .AddMicrosoftAccount(o =&gt; builder.Configuration.GetSection("Authentication:Microsoft").Bind(o));
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureIdentity">Optional callback to configure identity options (password rules, lockout, etc.).</param>
    /// <typeparam name="TUser">The user type, must extend <see cref="SparkUser"/>.</typeparam>
    /// <returns>The <see cref="IdentityBuilder"/> for further configuration (e.g. adding external login providers).</returns>
    internal static IdentityBuilder AddSparkAuthentication<TUser>(
        this IServiceCollection services,
        Action<IdentityOptions>? configureIdentity = null)
        where TUser : SparkUser, new()
    {
        var builder = services
            .AddIdentityApiEndpoints<TUser>(options =>
            {
                configureIdentity?.Invoke(options);
            })
            .AddRoles<SparkRole>();

        builder.Services.AddScoped<IUserStore<TUser>, UserStore<TUser>>();
        builder.Services.AddScoped<IRoleStore<SparkRole>, RoleStore>();
        builder.Services.AddScoped<SparkExternalLoginLinker<TUser>>();
        // Not TryAdd-ed by the framework, and the linker's expiry window is the one thing tests
        // need to move. Registered rather than reading DateTimeOffset.UtcNow inline so that
        // "the link expired" is testable without waiting an hour for it.
        builder.Services.TryAddSingleton(TimeProvider.System);

        services.AddAntiforgery(options => options.HeaderName = "X-XSRF-TOKEN");

        return builder;
    }

    /// <summary>
    /// Maps Spark's authentication endpoints under the <c>/spark/auth</c> route prefix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three groups, only the first of which is configurable:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Local credentials</b> — POST /register, POST /login, POST /refresh, GET /confirmEmail,
    /// POST /resendConfirmationEmail, POST /forgotPassword, POST /resetPassword, POST /manage/2fa,
    /// GET|POST /manage/info. Mapped by Microsoft's <c>MapIdentityApi</c>, gated by
    /// <paramref name="localCredentials"/>.
    /// </description></item>
    /// <item><description>
    /// <b>Spark's own</b> — GET /me, POST /logout, POST /csrf-refresh (source-generated). Always mapped;
    /// /csrf-refresh in particular is load-bearing, because without it the XSRF cookie is never rotated
    /// after sign-in and every subsequent mutating call fails antiforgery.
    /// </description></item>
    /// <item><description>
    /// <b>External login</b> — GET /external-login, GET /external-login-callback. Always mapped.
    /// </description></item>
    /// </list>
    /// </remarks>
    internal static IEndpointRouteBuilder MapSparkIdentityApi<TUser>(
        this IEndpointRouteBuilder endpoints,
        SparkLocalCredentials localCredentials = SparkLocalCredentials.Full)
        where TUser : SparkUser, new()
    {
        var authGroup = endpoints.MapGroup("/spark/auth");

        // Microsoft's mapper is all-or-nothing and its defaults attach no IAntiforgeryMetadata, so
        // both the filtering and the CSRF stamping live in LocalCredentialEndpointFilter.
        endpoints.MapLocalCredentialApi<TUser>(localCredentials);

        // Map Spark auth endpoints (source-generated)
        endpoints.MapSparkAuthEndpoints();

        // External login: initiate OAuth challenge
        authGroup.MapGet("/external-login", (
            HttpContext context,
            SignInManager<TUser> signInManager,
            string provider,
            string? returnUrl,
            string? popup) =>
        {
            // R2-M3: validate returnUrl at the entry point. Even if R2-C4's
            // callback fix were bypassed, accepting an absolute attacker URL here
            // round-trips through OAuth state and reflects back to the callback
            // unchanged. Substitute the default for anything non-local.
            var safeReturnUrl = SanitizeReturnUrl(returnUrl);
            var callbackUrl = $"/spark/auth/external-login-callback?returnUrl={Uri.EscapeDataString(safeReturnUrl)}";
            // The callback is a fresh top-level navigation, so the only thing that survives
            // this hop is the URL — carrying the popup flag forward here is what makes the
            // callback's postMessage branch reachable at all. Plain query string, not OAuth
            // `state`: this URL never reaches the provider (ASP.NET encrypts it into `state`
            // itself as AuthenticationProperties.RedirectUri, and the provider only ever sees
            // the registered CallbackPath).
            if (popup is not null)
                callbackUrl += "&popup=1";
            var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, callbackUrl);
            return Results.Challenge(properties, [provider]);
        });

        // External login: handle OAuth callback — mapped on root endpoints (not authGroup)
        // to avoid any group-level auth configuration from MapIdentityApi
        endpoints.MapGet("/spark/auth/external-login-callback", async (
            HttpContext context,
            SignInManager<TUser> signInManager,
            UserManager<TUser> userManager,
            SparkExternalLoginLinker<TUser> linker,
            IOptions<SparkAuthenticationOptions> options,
            IAntiforgery antiforgery,
            string? returnUrl) =>
        {
            // R2-C4: returnUrl is interpolated into the HTML/JS response body below,
            // so anything other than a relative in-app path is a vector for XSS
            // (and at minimum open-redirect after successful OAuth). Sanitize first.
            var safeReturnUrl = SanitizeReturnUrl(returnUrl);

            var info = await signInManager.GetExternalLoginInfoAsync();
            if (info is null)
                return ExternalLoginOutcome(context, safeReturnUrl, ExternalLoginErrors.NoLoginInfo);

            // Try signing in with existing external login
            var result = await signInManager.ExternalLoginSignInAsync(
                info.LoginProvider, info.ProviderKey, isPersistent: true);

            TUser? user;
            if (result.Succeeded)
            {
                user = await userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            }
            else
            {
                // R2-H11: only auto-provision when the issuer attested the email.
                // GitHub: 'urn:github:email_verified' is set by GitHubAuthenticationExtensions
                // when /user/emails reports primary && verified. Google/Microsoft/Apple:
                // standard "email_verified" claim. Refuse to bind an unverified email
                // to a fresh account — otherwise an attacker claiming any email at an
                // external IdP can squat that identity locally.
                var email = info.Principal.FindFirstValue(ClaimTypes.Email);
                var emailVerified = string.Equals(
                        info.Principal.FindFirstValue("email_verified"), "true",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        info.Principal.FindFirstValue("urn:github:email_verified"), "true",
                        StringComparison.OrdinalIgnoreCase);

                if (string.IsNullOrEmpty(email) || !emailVerified)
                    return ExternalLoginOutcome(context, safeReturnUrl, ExternalLoginErrors.EmailNotVerified);

                var userName = info.Principal.FindFirstValue(ClaimTypes.Name)
                    ?? info.Principal.FindFirstValue(ClaimTypes.NameIdentifier);

                // 4c: the address may already belong to somebody. Asked *before* provisioning
                // rather than inferred from a failed CreateAsync, because the store's DuplicateEmail
                // arrives with no user attached and is indistinguishable from a validation failure —
                // which is how this case used to surface as "account_creation_failed", a message
                // that reads as "this application is broken" rather than "you already have an
                // account".
                var existing = await userManager.FindByEmailAsync(email);
                if (existing is not null)
                {
                    return await LinkOrRefuseAsync(
                        context, signInManager, userManager, linker, options, antiforgery,
                        existing, info, userName, safeReturnUrl);
                }

                user = new TUser();
                await userManager.SetUserNameAsync(user, userName);
                await userManager.SetEmailAsync(user, email);
                // Issuer attested the email — mark it confirmed so the local account
                // is in the same state as a password-flow user who confirmed their address.
                user.EmailConfirmed = true;

                var createResult = await userManager.CreateAsync(user);
                if (!createResult.Succeeded)
                    return ExternalLoginOutcome(context, safeReturnUrl, ExternalLoginErrors.AccountCreationFailed);

                await userManager.AddLoginAsync(user, info);
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
            antiforgery.GetAndStoreTokens(context);

            // R2-C4: Server-side redirect instead of HTML interpolation. The previous
            // implementation built an HTML page with returnUrl as a raw JS string
            // literal — even after SanitizeReturnUrl removes the worst-case XSS
            // payloads, the popup branch (window.opener.postMessage) is unaffected
            // (no caller-data interpolation), and the non-popup branch is now a
            // standard server redirect that the framework HTML-encodes for us.
            return ExternalLoginOutcome(context, safeReturnUrl, error: null);
        }).AllowAnonymous();

        // 4e: the other half of ConfirmByEmail. Reached from a link in a mailbox, so it is a plain
        // top-level GET — there is no popup to post back to and no session to carry an antiforgery
        // token. The single-use token *is* the credential; that is what a confirmation link is.
        endpoints.MapGet("/spark/auth/confirm-external-link", async (
            HttpContext context,
            SignInManager<TUser> signInManager,
            UserManager<TUser> userManager,
            SparkExternalLoginLinker<TUser> linker,
            string? token,
            string? returnUrl) =>
        {
            var safeReturnUrl = SanitizeReturnUrl(returnUrl);

            // Usually absent: the reader is in their mailbox, not mid-OAuth. When it *is* present
            // and names a different identity than the confirmation was issued for, that is the
            // substitution the whole design is built against, and the linker refuses it.
            var ambient = await signInManager.GetExternalLoginInfoAsync();
            var result = await linker.ConfirmAsync(token, ambient?.ProviderKey, context.RequestAborted);

            if (result.Outcome != SparkLinkConfirmationOutcome.Linked)
            {
                return Results.Redirect(QueryHelpers.AddQueryString(
                    safeReturnUrl, "sparkLinkConfirmation", ConfirmationCode(result.Outcome)));
            }

            // Signing in here is the point: the person proved control of the account's mailbox, and
            // sending them back to a sign-in page after that would ask them to prove it twice.
            var user = await userManager.FindByIdAsync(result.UserId!);
            if (user is not null)
                await signInManager.SignInAsync(user, isPersistent: true);

            return Results.Redirect(QueryHelpers.AddQueryString(
                safeReturnUrl, "sparkLinkConfirmation", "linked"));
        }).AllowAnonymous();

        MapExternalLoginManagement<TUser>(endpoints, authGroup, localCredentials);

        return endpoints;
    }

    /// <summary>
    /// 4d: the account-page half of linking — list what is attached, attach another, detach one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which of these exist depends on the mode</b>, because the modes differ in who is allowed
    /// to attach a credential and how.
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="SparkExternalLoginLinking.Disabled"/> maps <b>nothing</b>. Linking is off, so an
    /// account page that offers it is surface with no purpose.
    /// </description></item>
    /// <item><description>
    /// <see cref="SparkExternalLoginLinking.WhenSignedIn"/> maps all four. This is the mode's whole
    /// point: proof comes from already holding the session.
    /// </description></item>
    /// <item><description>
    /// <see cref="SparkExternalLoginLinking.ConfirmByEmail"/> maps the list and the <b>unlink</b>,
    /// but not the attach. Its way in is the mailed confirmation; without unlink, links would
    /// accumulate with no way to undo one, which is a worse position than not linking at all.
    /// </description></item>
    /// </list>
    /// </remarks>
    private static void MapExternalLoginManagement<TUser>(
        IEndpointRouteBuilder endpoints,
        RouteGroupBuilder authGroup,
        SparkLocalCredentials localCredentials)
        where TUser : SparkUser, new()
    {
        var linking = endpoints.ServiceProvider
            .GetService<IOptions<SparkAuthenticationOptions>>()?.Value.ExternalLoginLinking
            ?? SparkExternalLoginLinking.Disabled;

        if (linking == SparkExternalLoginLinking.Disabled)
            return;

        authGroup.MapGet("/external-logins", async (
            HttpContext context,
            UserManager<TUser> userManager,
            IAuthenticationSchemeProvider schemes) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null)
                return Results.Unauthorized();

            var logins = await userManager.GetLoginsAsync(user);
            var hasPassword = await userManager.HasPasswordAsync(user);

            // Computed per login rather than once for the account, because it is the answer to
            // "can I remove *this* one" — and it is served to the client so the UI can disable the
            // button instead of offering an action that will be refused.
            var canUnlink = !SparkCredentialInventory.WouldRemoveLastCredential(
                logins.Count, hasPassword, localCredentials);

            var external = await schemes.GetAllSchemesAsync();
            var linked = logins.Select(l => l.LoginProvider).ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Results.Ok(new
            {
                linked = logins.Select(l => new
                {
                    provider = l.LoginProvider,
                    providerKey = l.ProviderKey,
                    displayName = l.ProviderDisplayName ?? l.LoginProvider,
                    canUnlink,
                }),
                available = external
                    .Where(scheme => scheme.DisplayName is not null && !linked.Contains(scheme.Name))
                    .Select(scheme => new { provider = scheme.Name, displayName = scheme.DisplayName }),
            });
        }).RequireAuthorization();

        authGroup.MapPost("/external-logins/unlink", async (
            HttpContext context,
            UserManager<TUser> userManager,
            SignInManager<TUser> signInManager,
            string provider,
            string providerKey) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null)
                return Results.Unauthorized();

            var logins = await userManager.GetLoginsAsync(user);
            if (!logins.Any(l =>
                    string.Equals(l.LoginProvider, provider, StringComparison.Ordinal)
                    && string.Equals(l.ProviderKey, providerKey, StringComparison.Ordinal)))
            {
                return Results.BadRequest(new { error = ExternalLoginErrors.LoginNotFound });
            }

            // ⚠️ The guard. Removing the last way in is permanent: no password to fall back on, no
            // provider left to prove ownership, and no self-service route back. Identity will
            // happily do it.
            if (SparkCredentialInventory.WouldRemoveLastCredential(
                    logins.Count, await userManager.HasPasswordAsync(user), localCredentials))
            {
                return Results.BadRequest(new { error = ExternalLoginErrors.LastCredential });
            }

            var result = await userManager.RemoveLoginAsync(user, provider, providerKey);
            if (!result.Succeeded)
                return Results.BadRequest(new { error = ExternalLoginErrors.UnlinkFailed });

            // The security stamp carries into the cookie, so refreshing it is what makes the
            // removal take effect on sessions other than this one.
            await signInManager.RefreshSignInAsync(user);
            return Results.Ok(new { unlinked = true });
        })
            .RequireAuthorization()
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));

        if (linking != SparkExternalLoginLinking.WhenSignedIn)
            return;

        authGroup.MapGet("/external-logins/link", (
            HttpContext context,
            SignInManager<TUser> signInManager,
            string provider,
            string? returnUrl,
            string? popup) =>
        {
            var safeReturnUrl = SanitizeReturnUrl(returnUrl);
            var callbackUrl = $"/spark/auth/link-external-login-callback?returnUrl={Uri.EscapeDataString(safeReturnUrl)}";
            if (popup is not null)
                callbackUrl += "&popup=1";

            // ⚠️ Keyed on the signed-in user so that the identity coming back is attached to the
            // session that asked, not to whoever the callback happens to find signed in. Identity
            // uses it to reject a callback that lands in a different session.
            var properties = signInManager.ConfigureExternalAuthenticationProperties(
                provider, callbackUrl, userId: context.User.FindFirstValue(ClaimTypes.NameIdentifier));
            return Results.Challenge(properties, [provider]);
        }).RequireAuthorization();

        endpoints.MapGet("/spark/auth/link-external-login-callback", async (
            HttpContext context,
            SignInManager<TUser> signInManager,
            UserManager<TUser> userManager,
            IAntiforgery antiforgery,
            string? returnUrl) =>
        {
            var safeReturnUrl = SanitizeReturnUrl(returnUrl);

            var user = await userManager.GetUserAsync(context.User);
            if (user is null)
                return ExternalLoginOutcome(context, safeReturnUrl, ExternalLoginErrors.SignInToLink);

            var info = await signInManager.GetExternalLoginInfoAsync(
                await userManager.GetUserIdAsync(user));
            if (info is null)
                return ExternalLoginOutcome(context, safeReturnUrl, ExternalLoginErrors.NoLoginInfo);

            var result = await userManager.AddLoginAsync(user, info);
            if (!result.Succeeded)
            {
                // Told apart deliberately. "Already attached somewhere" is a fact about the world
                // the user can act on — sign in with it, or detach it there first — while a store
                // failure is not, and reporting them the same way sends people looking for the
                // wrong problem.
                var error = result.Errors.Any(e => e.Code == "LoginAlreadyAssociated")
                    ? ExternalLoginErrors.LoginAlreadyAssociated
                    : ExternalLoginErrors.LinkFailed;
                return ExternalLoginOutcome(context, safeReturnUrl, error);
            }

            // Nothing about the session changed except which credentials reach it, and the external
            // cookie is spent; refreshing keeps this consistent with the unlink path.
            await signInManager.RefreshSignInAsync(user);
            antiforgery.GetAndStoreTokens(context);
            return ExternalLoginOutcome(context, safeReturnUrl, error: null);
        }).RequireAuthorization();
    }

    /// <summary>
    /// The confirmation outcome as the client sees it.
    /// </summary>
    /// <remarks>
    /// Kept as a mapping rather than serialising the enum name, so that renaming a member cannot
    /// silently change a value that browser code and mail templates depend on.
    /// </remarks>
    private static string ConfirmationCode(SparkLinkConfirmationOutcome outcome) => outcome switch
    {
        SparkLinkConfirmationOutcome.Linked => "linked",
        SparkLinkConfirmationOutcome.AlreadyUsed => "already_used",
        SparkLinkConfirmationOutcome.ProviderMismatch => "provider_mismatch",
        SparkLinkConfirmationOutcome.AccountGone => "account_gone",
        _ => "invalid_or_expired",
    };

    /// <summary>
    /// Why an external login did not sign anyone in. These codes are the popup handshake's
    /// public vocabulary — they reach browser code — so they are deliberately coarse: enough
    /// for the opener to show the right message, never enough to distinguish "no such account"
    /// from anything else.
    /// </summary>
    internal static class ExternalLoginErrors
    {
        /// <summary>No external login info on the request — usually the user cancelled at the provider.</summary>
        public const string NoLoginInfo = "no_login_info";
        /// <summary>The issuer did not attest the email address, so no account was auto-provisioned (R2-H11).</summary>
        public const string EmailNotVerified = "email_not_verified";
        /// <summary>Identity refused to create the local account (validation, duplicate, store failure).</summary>
        public const string AccountCreationFailed = "account_creation_failed";

        /// <summary>
        /// The provider's address already belongs to an account, and linking is
        /// <see cref="SparkExternalLoginLinking.Disabled"/>.
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>This code admits that an account exists</b>, which the codes above deliberately
        /// avoid doing. The trade is taken knowingly: the alternative is the generic failure this
        /// branch used to produce, and a person who cannot tell "you already have an account" from
        /// "this is broken" will file a bug or make a second account. The disclosure is also
        /// narrower than it looks — the asker already proved control of the address at the provider,
        /// so they can learn the same fact by attempting a password reset anywhere.
        /// </remarks>
        public const string EmailAlreadyRegistered = "email_already_registered";

        /// <summary>
        /// The address belongs to an account and linking happens from a session
        /// (<see cref="SparkExternalLoginLinking.WhenSignedIn"/>). Sign in with a provider that is
        /// already attached, then link this one.
        /// </summary>
        public const string SignInToLink = "sign_in_to_link";

        /// <summary>
        /// A confirmation was mailed to the existing account's address
        /// (<see cref="SparkExternalLoginLinking.ConfirmByEmail"/>). Nobody is signed in yet.
        /// </summary>
        /// <remarks>
        /// ⚠️ Carried on the failure channel because <em>no session was created</em>, which is what
        /// <c>success: false</c> means to the opener. It is not an error, and the client is expected
        /// to say so; reporting it as success would have the opener behave as though the user were
        /// signed in.
        /// </remarks>
        public const string LinkConfirmationSent = "link_confirmation_sent";

        /// <summary>
        /// The provider identity is already attached to an account — possibly this one, possibly
        /// another.
        /// </summary>
        /// <remarks>
        /// Deliberately distinct from <see cref="LinkFailed"/>. This is a fact the user can act on
        /// — sign in with it, or detach it where it is — while a store failure is not, and one
        /// message for both sends people looking for the wrong problem. It says no more than
        /// "taken": naming the other account would turn an account page into a lookup service.
        /// </remarks>
        public const string LoginAlreadyAssociated = "login_already_associated";

        /// <summary>The store refused to attach the login, for a reason that is not the above.</summary>
        public const string LinkFailed = "link_failed";

        /// <summary>
        /// ⚠️ Unlinking was refused because it would have removed the account's last way in.
        /// </summary>
        public const string LastCredential = "last_credential";

        /// <summary>The login named for removal is not attached to this account.</summary>
        public const string LoginNotFound = "login_not_found";

        /// <summary>The store refused to detach the login.</summary>
        public const string UnlinkFailed = "unlink_failed";
    }

    /// <summary>
    /// Decides what a known address does, once an external provider asserts one that already belongs
    /// to an account.
    /// </summary>
    /// <remarks>
    /// The three modes differ in <b>who proves what</b>, not in convenience, so each gets its own
    /// answer rather than a shared "failed" — see <see cref="SparkExternalLoginLinking"/>.
    /// </remarks>
    private static async Task<IResult> LinkOrRefuseAsync<TUser>(
        HttpContext context,
        SignInManager<TUser> signInManager,
        UserManager<TUser> userManager,
        SparkExternalLoginLinker<TUser> linker,
        IOptions<SparkAuthenticationOptions> options,
        IAntiforgery antiforgery,
        TUser existing,
        ExternalLoginInfo info,
        string? providerIdentity,
        string safeReturnUrl)
        where TUser : SparkUser, new()
    {
        switch (options.Value.ExternalLoginLinking)
        {
            case SparkExternalLoginLinking.WhenSignedIn:
                return ExternalLoginOutcome(context, safeReturnUrl, ExternalLoginErrors.SignInToLink);

            case SparkExternalLoginLinking.ConfirmByEmail:
                // The link is built from this request's own origin rather than from a stored base
                // address, because the confirmation has to come back to the host the person is
                // actually using. Host is not attacker-chosen here: the callback is only reached
                // through a provider redirect to a registered callback URL.
                var origin = $"{context.Request.Scheme}://{context.Request.Host}";
                await linker.RequestLinkAsync(
                    existing,
                    info.LoginProvider,
                    info.ProviderKey,
                    info.ProviderDisplayName ?? info.LoginProvider,
                    providerIdentity,
                    token => $"{origin}/spark/auth/confirm-external-link?token={Uri.EscapeDataString(token)}"
                        + $"&returnUrl={Uri.EscapeDataString(safeReturnUrl)}",
                    context.RequestAborted);

                return ExternalLoginOutcome(context, safeReturnUrl, ExternalLoginErrors.LinkConfirmationSent);

            default:
                return ExternalLoginOutcome(context, safeReturnUrl, ExternalLoginErrors.EmailAlreadyRegistered);
        }
    }

    /// <summary>
    /// How the external-login callback reports its result, in whichever way the caller can
    /// actually receive it: a popup cannot navigate its opener, so it must post a message and
    /// close, while a full-page flow simply redirects.
    /// <para>
    /// Every exit path goes through here — success <i>and</i> all three refusals. A branch that
    /// redirected unconditionally would leave a popup opener's listener waiting forever on a
    /// window the user had already closed, which is precisely the bug this milestone fixes.
    /// </para>
    /// </summary>
    /// <param name="error">
    /// <see langword="null"/> for success, otherwise a constant from <see cref="ExternalLoginErrors"/>.
    /// It is interpolated into a JS object literal below, which is safe only while it stays a
    /// compile-time constant — never pass caller-supplied text.
    /// </param>
    private static IResult ExternalLoginOutcome(HttpContext context, string safeReturnUrl, string? error)
    {
        if (!context.Request.Query.ContainsKey("popup"))
        {
            // ⚠️ The redirect branch used to drop `error` entirely, so a refused sign-in in
            // full-page mode landed back on the sign-in page with nothing to show for it. That was
            // survivable while every refusal meant "it did not work"; it is not survivable now that
            // one of them means "check your mail", which the user will never do if nobody says so.
            return Results.Redirect(error is null
                ? safeReturnUrl
                : QueryHelpers.AddQueryString(safeReturnUrl, "sparkExternalLogin", error));
        }

        var payload = error is null
            ? "{ type: 'spark:external-login', success: true }"
            : $"{{ type: 'spark:external-login', success: false, error: '{error}' }}";

        var html = $$"""
            <!DOCTYPE html>
            <html><head><title>Signing in...</title></head>
            <body>
            <script>
            if (window.opener) {
                window.opener.postMessage({{payload}}, window.location.origin);
            }
            window.close();
            </script>
            </body></html>
            """;

        return Results.Content(html, "text/html");
    }

    /// <summary>
    /// R2-C4 / R2-M3 shared validator. Accepts only local relative URLs:
    /// must start with '/', must not start with '//' or '/\' (protocol-relative
    /// or backslash-confused-as-slash navigation), must not contain CR/LF
    /// (header-splitting). Returns "/" for anything else — never throws so
    /// callers can use it unconditionally.
    /// </summary>
    /// <summary>Shared with the IdentityProvider package: both flows redirect to a caller-supplied
    /// returnUrl and must reject off-origin targets. Duplicating it would let the two drift.</summary>
    internal static string SanitizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl)) return "/";
        if (returnUrl.IndexOfAny(['\r', '\n']) >= 0) return "/";
        if (!returnUrl.StartsWith('/')) return "/";
        if (returnUrl.Length >= 2 && (returnUrl[1] == '/' || returnUrl[1] == '\\')) return "/";
        return returnUrl;
    }

}
