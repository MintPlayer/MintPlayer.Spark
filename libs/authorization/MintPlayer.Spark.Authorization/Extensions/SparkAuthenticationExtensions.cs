using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.AspNetCore.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.WebUtilities;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Endpoints;
using MintPlayer.Spark.Authorization.Endpoints.ExternalLogin;
using MintPlayer.Spark.Authorization.Identity;
using System.Security.Claims;
using MintPlayer.Spark.MailManager;

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
            .AddRoles<SparkRole>()
            // #460 D4: sign in with email or user name, one resolver for every password sign-in
            // (MapIdentityApi's /login and the OIDC /connect/login page both call the string
            // overload), plus the user-name rule (never an '@', G-Q22) that keeps the user name a
            // public handle and the two namespaces from colliding.
            .AddSignInManager<SparkSignInManager<TUser>>()
            .AddUserValidator<SparkUserNameValidator<TUser>>();

        builder.Services.AddScoped<IUserStore<TUser>, UserStore<TUser>>();
        builder.Services.AddScoped<IRoleStore<SparkRole>, RoleStore>();
        builder.Services.AddScoped<SparkExternalLoginLinker<TUser>>();
        // Not TryAdd-ed by the framework, and the linker's expiry window is the one thing tests
        // need to move. Registered rather than reading DateTimeOffset.UtcNow inline so that
        // "the link expired" is testable without waiting an hour for it.
        builder.Services.TryAddSingleton(TimeProvider.System);

        // #460 D5 / item 6: protect legacy plaintext secrets and fill CreatedAtUtc, once, after start.
        builder.Services.TryAddSingleton<SparkUserBackfill<TUser>>();
        builder.Services.AddHostedService<SparkUserBackfillHostedService<TUser>>();

        // #460 D16: where mailed confirmation/reset links point (the SPA's pages, not the server's
        // plain-text confirmEmail). Replaceable.
        builder.Services.TryAddSingleton<ISparkAuthLinkBuilder, SparkAuthLinkBuilder>();
        builder.Services.TryAddSingleton<SparkQrCodeRenderer>();
        builder.Services.TryAddScoped<SparkAccountMail<TUser>>();

        // #460 M8: account mail through MailManager. Identity's AddIdentityApiEndpoints TryAdds its no-op
        // DefaultMessageEmailSender, so it is REPLACED here — but only that one: an app that registered
        // its own IEmailSender<TUser> first keeps it (and one registered later wins anyway).
        var emailSender = builder.Services.LastOrDefault(d => d.ServiceType == typeof(IEmailSender<TUser>));
        if (emailSender is null || IsIdentityNoOpSender(emailSender.ImplementationType))
            // Transient, like the default it replaces: MapIdentityApi resolves the sender from the ROOT
            // provider at mapping time, and a scoped one is refused there. Resolved in a request, its
            // IServiceProvider is the request scope, so the (scoped) ISparkMailer is the request's.
            builder.Services.Replace(ServiceDescriptor.Transient<IEmailSender<TUser>, SparkMailEmailSender<TUser>>());
        // Only while MailManager is present: the ConfirmByEmail guard tests for a registered sender.
        builder.Services.TryAddScoped<ISparkLinkConfirmationSender<TUser>>(sp =>
            sp.GetService<MailManager.ISparkMailer>() is { } mailer
                ? new SparkMailLinkConfirmationSender<TUser>(mailer, sp.GetRequiredService<TimeProvider>())
                : null!);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<MailManager.ISparkMailRecipientCulture, SparkUserMailCulture<TUser>>());
        // The account mails' default templates; an app's Templates/Mail/SparkAuth/*.mjml overrides them.
        // Anchored on SparkAuthMailTemplates, which lives in this assembly with the embedded templates. Not
        // SparkUser: it moved to Authorization.Abstractions (#388), which has no templates, and every
        // account mail then failed with "No mail template 'SparkAuth/…'".
        builder.Services.AddSparkMailTemplates(typeof(SparkAuthMailTemplates).Assembly, "SparkMail/");

        services.AddAntiforgery(options => options.HeaderName = "X-XSRF-TOKEN");

        return builder;
    }

    /// <summary>
    /// Identity's own sender, which discards every mail. Internal to Identity, so only recognisable by
    /// name (measured 2026-09-20: it is the resolved sender whether or not a transport exists).
    /// </summary>
    internal static bool IsIdentityNoOpSender(Type? type)
        => type is not null && type.Namespace == "Microsoft.AspNetCore.Identity" && type.Name.StartsWith("DefaultMessageEmailSender", StringComparison.Ordinal);

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

        // Hand-mapped, because the whole group is gated on SparkPasskeys and the generated mapper is
        // unconditional. MapIdentityApi contributes nothing here — measured, it maps no passkey
        // route at all.
        PasskeyEndpoints.MapPasskeyApi<TUser>(endpoints, authGroup);

        // External login: initiate OAuth challenge
        // The external-login surface. These are open generics, so this assembly's generated
        // MapSparkAuthEndpoints() skips them (MPEP025, Info) and they are mapped here, where
        // TUser is concrete. On `endpoints`, not `authGroup`: the /spark/auth prefix comes from
        // [MemberOf<SparkAuthGroup>] and mapping onto the group too would compose it twice.
        endpoints.MapEndpoint<ExternalLoginChallenge<TUser>>();

        // /me reads the user through UserManager<TUser>, so it is generic and mapped here too.
        endpoints.MapEndpoint<GetCurrentUser<TUser>>();

        // External login: handle the OAuth callback.
        endpoints.MapEndpoint<ExternalLoginCallback<TUser>>();

        // 4e: the other half of ConfirmByEmail. Reached from a link in a mailbox, so it is a plain
        // top-level GET — there is no popup to post back to and no session to carry an antiforgery
        // token. The single-use token *is* the credential; that is what a confirmation link is.
        endpoints.MapEndpoint<ConfirmExternalLink<TUser>>();

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

        // A passkey is a credential too, so it counts toward "is this the last way in". Without
        // this, an account holding a working passkey would be refused permission to unlink its only
        // external login — the guard failing closed, but wrongly.
        var passkeys = endpoints.ServiceProvider
            .GetService<IOptions<SparkAuthenticationOptions>>()?.Value.Passkeys
            ?? SparkPasskeys.Disabled;

        if (linking == SparkExternalLoginLinking.Disabled)
            return;

        endpoints.MapEndpoint<ListExternalLogins<TUser>>();

        endpoints.MapEndpoint<UnlinkExternalLogin<TUser>>();

        if (linking != SparkExternalLoginLinking.WhenSignedIn)
            return;

        endpoints.MapEndpoint<LinkExternalLoginChallenge<TUser>>();

        endpoints.MapEndpoint<LinkExternalLoginCallback<TUser>>();
    }

    /// <summary>
    /// The confirmation outcome as the client sees it.
    /// </summary>
    /// <remarks>
    /// Kept as a mapping rather than serialising the enum name, so that renaming a member cannot
    /// silently change a value that browser code and mail templates depend on.
    /// </remarks>
    internal static string ConfirmationCode(SparkLinkConfirmationOutcome outcome) => outcome switch
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
        /// An account was created from a provider without a reliable verified-email signal (#460,
        /// D7) and <c>RequireConfirmedEmail</c> is on: a confirmation link was mailed, nobody is
        /// signed in yet. Like <see cref="LinkConfirmationSent"/>, not an error.
        /// </summary>
        public const string ConfirmEmailSent = "confirm_email_sent";

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

        /// <summary>The requested provider is not a registered authentication scheme.</summary>
        /// <remarks>
        /// A bad request rather than a server error. It usually means a client and a deployment
        /// disagree about which providers exist — which is precisely what a 500 hides.
        /// </remarks>
        public const string UnknownProvider = "unknown_provider";

        /// <summary>The account is locked out.</summary>
        /// <remarks>
        /// ⚠️ Reported rather than routed around. A lockout is a deliberate security response, and
        /// the branch that used to follow it led into account provisioning.
        /// </remarks>
        public const string LockedOut = "locked_out";

        /// <summary>The account needs a second factor, which this flow does not collect.</summary>
        public const string RequiresTwoFactor = "requires_two_factor";

        /// <summary>Sign-in is not permitted — typically an unconfirmed account.</summary>
        public const string NotAllowed = "not_allowed";

        /// <summary>The login is attached, but Identity refused the sign-in for another reason.</summary>
        public const string SignInRefused = "sign_in_refused";
    }

    /// <summary>
    /// Decides what a known address does, once an external provider asserts one that already belongs
    /// to an account.
    /// </summary>
    /// <remarks>
    /// The three modes differ in <b>who proves what</b>, not in convenience, so each gets its own
    /// answer rather than a shared "failed" — see <see cref="SparkExternalLoginLinking"/>.
    /// </remarks>
    internal static async Task<IResult> LinkOrRefuseAsync<TUser>(
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
    internal static IResult ExternalLoginOutcome(HttpContext context, string safeReturnUrl, string? error)
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
