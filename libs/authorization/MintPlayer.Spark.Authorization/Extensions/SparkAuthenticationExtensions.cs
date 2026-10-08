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
using MintPlayer.Spark.Authorization.Endpoints.Account;
using MintPlayer.Spark.Authorization.Endpoints.ExternalLogin;
using MintPlayer.Spark.Authorization.Endpoints.Passkeys;
using MintPlayer.Spark.Authorization.Identity;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// // External login providers are added on the Spark builder, after spark.AddAuthentication&lt;AppUser&gt;(),
    /// // never as raw handlers on the returned IdentityBuilder (those skip Spark's email policy and are
    /// // refused at startup unless declared with spark.AddExternalScheme):
    /// spark.AddAuthentication&lt;AppUser&gt;();
    /// spark.AddExternalProviders(builder.Configuration); // Spark:Auth:Providers
    /// spark.AddGoogle(o =&gt; o.Scope.Add("https://www.googleapis.com/auth/calendar.readonly"));
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
        // The generic passkeys page (Passkeys / PasskeyRow): its actions classes are resolved by name,
        // so they reach the user through this seam, closed over TUser here.
        builder.Services.AddHttpContextAccessor();
        builder.Services.TryAddScoped<ISparkPasskeyAccount, SparkPasskeyAccount<TUser>>();
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
    /// What is mapped follows <see cref="SparkAuthenticationOptions"/>, read from the root provider:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Microsoft's local credentials</b> — POST /login, POST /refresh, POST /manage/2fa,
    /// GET /manage/info, through <c>MapIdentityApi</c> filtered by <see cref="SparkAuthenticationOptions.LocalCredentials"/>
    /// (<see cref="LocalCredentialEndpointFilter"/>).
    /// </description></item>
    /// <item><description>
    /// <b>Spark's account endpoints</b> (<c>Endpoints/Account</c>) — register, confirmation, recovery and
    /// the <c>/manage</c> pages. Every one is mapped here; the ones a mode excludes switch themselves off
    /// through their own <c>IsEnabled</c> (D5), so there is no <c>if</c> around any mapping call.
    /// </description></item>
    /// <item><description>
    /// <b>Spark's own</b> — GET /capabilities, POST /logout, POST /csrf-refresh (source-generated). Always
    /// mapped; /csrf-refresh in particular is load-bearing, because without it the XSRF cookie is never
    /// rotated after sign-in and every subsequent mutating call fails antiforgery.
    /// </description></item>
    /// <item><description>
    /// <b>External login and passkeys</b> — sign-in always; passkeys and account linking per their
    /// options, again through the endpoints' <c>IsEnabled</c>.
    /// </description></item>
    /// </list>
    /// <para>
    /// Every endpoint below is an open generic, so this assembly's generated <c>MapSparkAuthEndpoints()</c>
    /// skips it (MPEP025, Info) and it is mapped here, where <typeparamref name="TUser"/> is concrete. The
    /// <c>/spark/auth</c> prefix comes from <c>[MemberOf&lt;SparkAuthGroup&gt;]</c>.
    /// </para>
    /// </remarks>
    internal static IEndpointRouteBuilder MapSparkIdentityApi<TUser>(this IEndpointRouteBuilder endpoints)
        where TUser : SparkUser, new()
    {
        // #464/#490 Q4b: a remote (external-login) scheme registered behind Spark's back is refused
        // before anything is mapped — the sign-in page would offer it with no email policy.
        SparkExternalSchemeGuard.GuardAgainstUndeclaredRemoteSchemes(endpoints.ServiceProvider);

        // Microsoft's mapper is all-or-nothing and its defaults attach no IAntiforgeryMetadata, so
        // both the filtering and the CSRF stamping live in LocalCredentialEndpointFilter.
        endpoints.MapLocalCredentialApi<TUser>(LocalCredentialMode.Get(endpoints.ServiceProvider));

        // Map Spark auth endpoints (source-generated)
        endpoints.MapSparkAuthEndpoints();

        // Spark's account endpoints, replacing Microsoft's mail-sending half (#460 D6, D8, D16).
        endpoints.MapEndpoint<Register<TUser>>();
        endpoints.MapEndpoint<ResendConfirmationEmail<TUser>>();
        endpoints.MapEndpoint<ForgotPassword<TUser>>();
        endpoints.MapEndpoint<ResetPassword<TUser>>();
        endpoints.MapEndpoint<ConfirmEmailLink<TUser>>();
        endpoints.MapEndpoint<ConfirmEmail<TUser>>();
        endpoints.MapEndpoint<UpdateInfo<TUser>>();
        endpoints.MapEndpoint<SetPassword<TUser>>();
        endpoints.MapEndpoint<Profile<TUser>>();
        endpoints.MapEndpoint<UpdateProfile<TUser>>();
        endpoints.MapEndpoint<AuthenticatorUri<TUser>>();
        endpoints.MapEndpoint<PersonalData<TUser>>();
        endpoints.MapEndpoint<DeleteAccount<TUser>>();

        // Passkey sign-in, gated on SparkPasskeys. MapIdentityApi contributes nothing here —
        // measured, it maps no passkey route at all.
        endpoints.MapEndpoint<PasskeyRequestOptions<TUser>>();
        endpoints.MapEndpoint<PasskeySignIn<TUser>>();

        // External login: initiate the OAuth challenge.
        endpoints.MapEndpoint<ExternalLoginChallenge<TUser>>();

        // /me reads the user through UserManager<TUser>, so it is generic and mapped here too.
        endpoints.MapEndpoint<GetCurrentUser<TUser>>();

        // External login: handle the OAuth callback.
        endpoints.MapEndpoint<ExternalLoginCallback<TUser>>();

        // 4e: the other half of ConfirmByEmail. Reached from a link in a mailbox, so it is a plain
        // top-level GET — there is no popup to post back to and no session to carry an antiforgery
        // token. The single-use token *is* the credential; that is what a confirmation link is.
        endpoints.MapEndpoint<ConfirmExternalLink<TUser>>();

        // 4d: the account-page half of linking — list what is attached, attach another, detach one.
        // Which of these exist depends on SparkExternalLoginLinking, and each endpoint says so in its
        // own IsEnabled: Disabled maps none, ConfirmByEmail the list and the unlink (attaching is
        // mailed there), WhenSignedIn all four.
        endpoints.MapEndpoint<ListExternalLogins<TUser>>();
        endpoints.MapEndpoint<UnlinkExternalLogin<TUser>>();
        endpoints.MapEndpoint<LinkExternalLoginChallenge<TUser>>();
        endpoints.MapEndpoint<LinkExternalLoginCallback<TUser>>();

        return endpoints;
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

        /// <summary>
        /// The provider reported an error, or the round trip itself failed (correlation, state) —
        /// anything at the provider hop other than the user declining (#490, D4).
        /// </summary>
        /// <remarks>
        /// A provider-side <c>access_denied</c> — the user cancelled at the provider — is reported as
        /// <see cref="NoLoginInfo"/> instead, the code that case has always produced.
        /// </remarks>
        public const string RemoteFailure = "remote_failure";

        /// <summary>
        /// The challenge's <c>nonce</c> was present but not 16–64 base64url characters (#490, D1).
        /// </summary>
        /// <remarks>
        /// Only ever an HTTP 400 at the challenge; it never reaches a popup payload, because a nonce
        /// that fails the check is never forwarded.
        /// </remarks>
        public const string InvalidNonce = "invalid_nonce";
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
    /// actually receive it: a popup cannot navigate its opener, so it serves a page that hands the
    /// result back (see <see cref="ExternalLoginPopupHtml"/>), while a full-page flow simply redirects.
    /// Shared by the sign-in and link callbacks and by the provider-failure handler.
    /// <para>
    /// Every exit path goes through here — success <i>and</i> every refusal. A branch that
    /// redirected unconditionally would leave a popup opener's listener waiting forever on a
    /// window the user had already closed, which is precisely the bug this milestone fixes.
    /// </para>
    /// </summary>
    /// <param name="error">
    /// <see langword="null"/> for success, otherwise a constant from <see cref="ExternalLoginErrors"/>.
    /// </param>
    /// <remarks>
    /// The popup mode and the nonce are read from this request's query string, which is the callback
    /// URL the challenge built (see <see cref="SparkExternalLoginNonce.AppendCallbackFlags"/>).
    /// </remarks>
    internal static IResult ExternalLoginOutcome(HttpContext context, string safeReturnUrl, string? error)
        => ExternalLoginOutcome(
            popup: context.Request.Query.ContainsKey("popup"),
            nonce: SparkExternalLoginNonce.Accept(context.Request.Query[SparkExternalLoginNonce.QueryParameter]),
            safeReturnUrl,
            error,
            safeErrorUrl: SanitizeErrorUrl(context.Request.Query[ErrorUrlParameter]));

    /// <summary>
    /// The query parameter naming where a <b>redirect-mode</b> failure lands (#490 M6): the page that
    /// started the flow and reads <c>?sparkExternalLogin</c>, such as the sign-in page or the identity
    /// provider's <c>/connect/login</c>. Success still goes to <c>returnUrl</c>.
    /// </summary>
    internal const string ErrorUrlParameter = "errorUrl";

    /// <summary>
    /// <see cref="SanitizeReturnUrl"/> for an optional <c>errorUrl</c>: <see langword="null"/> when it is
    /// absent, so the caller can fall back to the return URL, and the site root for anything off-origin.
    /// </summary>
    internal static string? SanitizeErrorUrl(string? errorUrl)
        => string.IsNullOrEmpty(errorUrl) ? null : SanitizeReturnUrl(errorUrl);

    /// <summary>
    /// Appends an already sanitized <c>errorUrl</c> to a callback URL the challenge builds, so it survives
    /// the provider round trip the same way <c>returnUrl</c> does.
    /// </summary>
    internal static string AppendErrorUrl(string callbackUrl, string? safeErrorUrl)
        => safeErrorUrl is null ? callbackUrl : QueryHelpers.AddQueryString(callbackUrl, ErrorUrlParameter, safeErrorUrl);

    /// <summary>
    /// <see cref="ExternalLoginOutcome(HttpContext, string, string?)"/> with the hand-off flags given
    /// explicitly — for a caller that is not the callback request itself, such as
    /// <see cref="SparkExternalLoginRemoteFailure"/>, which reads them from the failed round trip's
    /// <c>RedirectUri</c>.
    /// </summary>
    /// <param name="nonce">An already validated nonce (<see cref="SparkExternalLoginNonce.Accept"/>), or <see langword="null"/>.</param>
    /// <param name="safeReturnUrl">An already sanitized return URL (<see cref="SanitizeReturnUrl"/>).</param>
    /// <param name="safeErrorUrl">
    /// An already sanitized error URL (<see cref="SanitizeErrorUrl"/>), or <see langword="null"/>. In
    /// redirect mode a failure lands there instead of on <paramref name="safeReturnUrl"/>: the return URL
    /// is where a <em>signed-in</em> user goes next (for the identity provider, the pending
    /// <c>/connect/authorize</c>, which cannot show an error), while the error URL is the page that reads
    /// <c>?sparkExternalLogin</c>. Popup mode ignores it.
    /// </param>
    internal static IResult ExternalLoginOutcome(bool popup, string? nonce, string safeReturnUrl, string? error, string? safeErrorUrl = null)
    {
        if (!popup)
        {
            // ⚠️ The redirect branch used to drop `error` entirely, so a refused sign-in in
            // full-page mode landed back on the sign-in page with nothing to show for it. That was
            // survivable while every refusal meant "it did not work"; it is not survivable now that
            // one of them means "check your mail", which the user will never do if nobody says so.
            return Results.Redirect(error is null
                ? safeReturnUrl
                : QueryHelpers.AddQueryString(safeErrorUrl ?? safeReturnUrl, "sparkExternalLogin", error));
        }

        return new ExternalLoginPopupPage(ExternalLoginPopupHtml(nonce, safeReturnUrl, error));
    }

    /// <summary>
    /// The popup-mode callback page (#490, D1): static HTML whose only dynamic parts are a
    /// <c>System.Text.Json</c>-serialized payload and return URL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ Nothing from the request is spliced into the script as text. The payload is serialized by
    /// <see cref="JsonSerializer"/>, whose default encoder escapes <c>&lt; &gt; &amp; '</c> (so not even
    /// a <c>&lt;/script&gt;</c> inside a value could end the element); the nonce has already passed
    /// <see cref="SparkExternalLoginNonce"/>'s shape check as well, and the return URL has been
    /// sanitized before it is encoded the same way.
    /// </para>
    /// <para>
    /// ⚠️ There is no Content-Security-Policy today. If one is ever added, this inline script needs a
    /// CSP nonce (or hash), or every popup sign-in stops reporting back.
    /// </para>
    /// <para>
    /// With a nonce, the page reports on every channel the opener might still be listening on — an
    /// opener <c>postMessage</c>, a <c>BroadcastChannel</c> and a <c>localStorage</c> key — because
    /// under COOP, or inside an installed app's captured tab, the opener may be gone. It closes itself
    /// once acknowledged (or straight away in standalone mode, where an ack may never come), restores
    /// the app when a captured tab is reopened after the hand-off, and otherwise shows a static
    /// "you can close this window" text with a Close button after about three seconds.
    /// </para>
    /// <para>Without a nonce (a pre-#490 client) it keeps the old behaviour: opener message, then close.</para>
    /// </remarks>
    internal static string ExternalLoginPopupHtml(string? nonce, string safeReturnUrl, string? error)
    {
        var payload = JsonSerializer.Serialize(new ExternalLoginMessage(
            Type: "spark:external-login",
            Success: error is null,
            Error: error,
            Nonce: nonce));

        if (nonce is null)
        {
            return $$"""
                <!DOCTYPE html>
                <html><head><meta charset="utf-8"><title>Signing in...</title></head>
                <body>
                <script>
                (function () {
                    var msg = {{payload}};
                    if (window.opener) {
                        window.opener.postMessage(msg, window.location.origin);
                    }
                    window.close();
                })();
                </script>
                </body></html>
                """;
        }

        var returnUrl = JsonSerializer.Serialize(safeReturnUrl);
        var fallbackText = error is null
            ? "Signed in — you can close this window and return to your browser."
            : "Sign-in failed — you can close this window.";

        return $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Signing in...</title></head>
            <body>
            <div id="spark-external-login-fallback" hidden>
            <p>{{fallbackText}}</p>
            <button type="button" id="spark-external-login-close">Close</button>
            </div>
            <script>
            (function () {
                var msg = {{payload}};
                var returnUrl = {{returnUrl}};
                var channelName = 'spark:external-login';
                var storageKey = 'spark:external-login:' + msg.nonce;
                var doneKey = 'spark:external-login-done:' + msg.nonce;
                var standalone = (!!window.matchMedia && window.matchMedia('(display-mode: standalone)').matches)
                    || window.navigator.standalone === true;

                function restore() {
                    if (!standalone) return false;
                    try {
                        if (window.localStorage.getItem(doneKey) === null) return false;
                        window.localStorage.removeItem(doneKey);
                    } catch (e) { return false; }
                    window.location.replace(returnUrl);
                    return true;
                }

                document.addEventListener('visibilitychange', function () {
                    if (document.visibilityState === 'visible') restore();
                });
                if (restore()) return;

                try {
                    var stored = {};
                    for (var k in msg) stored[k] = msg[k];
                    stored.at = Date.now();
                    window.localStorage.setItem(storageKey, JSON.stringify(stored));
                } catch (e) { }

                var bc = null;
                try {
                    bc = new BroadcastChannel(channelName);
                    bc.onmessage = function (e) {
                        var d = e.data;
                        if (d && d.type === 'spark:external-login-ack' && d.nonce === msg.nonce) {
                            try { window.localStorage.setItem(doneKey, '1'); } catch (x) { }
                            window.close();
                        }
                    };
                    bc.postMessage(msg);
                } catch (e) { }

                if (window.opener) {
                    try { window.opener.postMessage(msg, window.location.origin); } catch (e) { }
                }

                if (standalone) window.close();

                document.getElementById('spark-external-login-close').addEventListener('click', function () {
                    window.close();
                });
                window.setTimeout(function () {
                    document.getElementById('spark-external-login-fallback').hidden = false;
                }, 3000);
            })();
            </script>
            </body></html>
            """;
    }

    /// <summary>The popup hand-off's wire payload. Property names are the contract the client reads.</summary>
    private sealed record ExternalLoginMessage(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("nonce")] string? Nonce);

    /// <summary>
    /// Writes the popup callback page, and makes sure it goes out without a
    /// <c>Cross-Origin-Opener-Policy</c> (#490, D10).
    /// </summary>
    /// <remarks>
    /// Spark sets no COOP anywhere, but an application's own header middleware might. On this page a
    /// <c>same-origin</c> policy severs <c>window.opener</c> and the popup can no longer post back, so
    /// a header already on the response is removed here. (A middleware that adds it from a later
    /// <c>OnStarting</c> callback is beyond reach; don't.)
    /// </remarks>
    private sealed class ExternalLoginPopupPage(string html) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.Remove("Cross-Origin-Opener-Policy");
            return Results.Content(html, "text/html", Encoding.UTF8).ExecuteAsync(httpContext);
        }
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
