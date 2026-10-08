using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>
/// Mounts only the part of ASP.NET Core Identity's endpoint surface that the configured
/// <see cref="SparkLocalCredentials"/> mode allows.
/// </summary>
/// <remarks>
/// <para>
/// <c>MapIdentityApi&lt;TUser&gt;()</c> is Microsoft's and is all-or-nothing: it exposes no way to
/// map a subset, and <see cref="IEndpointConventionBuilder"/> can decorate endpoints but not remove
/// them. So the endpoints are mapped into a throwaway <see cref="IEndpointRouteBuilder"/> that is
/// never published, and only the wanted ones are re-published into the real builder. Microsoft's
/// handlers are kept intact — nothing upstream is re-implemented, and nothing has to track servicing
/// changes to Identity.
/// </para>
/// <para>
/// The endpoints the mode excludes are <em>absent</em> from the route table, not merely unreachable.
/// Shadowing them with middleware that returns 404 would leave them discoverable through the endpoint
/// data source and OpenAPI, and reachable by anything that runs before the shadow.
/// </para>
/// </remarks>
internal static class LocalCredentialEndpointFilter
{
    private static readonly string[] PasswordSignInRoutes = ["/login", "/refresh"];

    /// <summary>
    /// Mutating Identity endpoints that Spark defends with double-submit CSRF. Microsoft's defaults
    /// attach no <see cref="IAntiforgeryMetadata"/>, so Spark stamps it on.
    /// <para>
    /// ⚠️ <c>/login</c> is on this list, and the reason it is worth spelling out is that it used to
    /// be excluded — on the stated grounds that "there is no session yet, so there is no XSRF-TOKEN
    /// cookie to validate". That premise was false. <c>UseSpark()</c> mints an <c>XSRF-TOKEN</c>
    /// cookie on <em>every</em> response including anonymous ones, so a visitor who has loaded the
    /// SPA at all is holding an anonymous-bound token, and the login POST carries it.
    /// </para>
    /// <para>
    /// What the exclusion left open is <b>login CSRF</b>: an attacker page POSTs <c>/login</c> with
    /// the attacker's own credentials, the victim's browser silently acquires a session belonging to
    /// the attacker, and everything the victim does next — uploading coverage, linking a repository,
    /// saving a setting — lands in an account the attacker can log into and read. It is the one CSRF
    /// that is worth mounting against an endpoint nobody is signed in to yet, which is exactly why
    /// "anonymous" was the wrong reason to skip it.
    /// </para>
    /// <para>
    /// The gate is reachable here because explicit metadata wins over the ambient-credential test —
    /// see <c>SparkAntiforgeryMiddleware</c>. The anonymous caller has no ambient credential, so
    /// nothing but this stamp would ever check it.
    /// </para>
    /// </summary>
    private static readonly string[] AntiforgeryGatedRoutes =
        ["/login", "/manage/2fa", "/manage/info", "/resetPassword", "/forgotPassword", "/logout"];

    internal static void MapLocalCredentialApi<TUser>(
        this IEndpointRouteBuilder endpoints,
        SparkLocalCredentials mode)
        where TUser : SparkUser, new()
    {
        // Before the Full short-circuit below: confirm-by-email linking is orthogonal to the
        // local-credential surface, so an application running Full is exactly as able to configure
        // a mode whose mail would be discarded. Placing this after the early return would have
        // meant the guard never fired for the most common configuration.
        GuardAgainstSilentlyDiscardedMail<TUser>(endpoints.ServiceProvider);
        if (mode == SparkLocalCredentials.Full)
            GuardAgainstRegistrationWithoutMail<TUser>(endpoints.ServiceProvider);

        // #460: every mode goes through the filter now, Full included — Spark replaces Microsoft's
        // mail-sending endpoints in all of them (Endpoints/Account), so there is no longer a mode
        // whose route table is MapIdentityApi's verbatim.
        if (mode == SparkLocalCredentials.Disabled)
            GuardAgainstUnreachableSignIn(endpoints.ServiceProvider);
        else
            GuardAgainstNoSignInIdentifier(endpoints.ServiceProvider);

        var throwaway = new UnpublishedEndpointRouteBuilder(endpoints.ServiceProvider);
        var identityApi = throwaway.MapGroup("/spark/auth").MapIdentityApi<TUser>();
        StampAntiforgery(identityApi);
        StampEndpointTypes(identityApi);

        GuardAgainstUnrecognizedIdentityRoutes(throwaway);

        // Materializing here runs the group conventions, including the antiforgery stamping above,
        // so the metadata is already on the endpoints being copied across.
        var kept = throwaway.DataSources
            .SelectMany(source => source.Endpoints)
            .Where(endpoint => IsAllowed(endpoint, mode))
            .ToArray();

        endpoints.DataSources.Add(new FixedEndpointDataSource(kept));
    }

    /// <summary>
    /// Microsoft's endpoints that Spark maps its own version of (<c>Endpoints/Account</c>) and
    /// that are therefore dropped in every mode. <c>/manage/info</c> is dropped for POST only.
    /// </summary>
    private static readonly string[] ReplacedBySpark =
        ["/register", "/resendConfirmationEmail", "/confirmEmail", "/forgotPassword", "/resetPassword"];

    /// <summary>
    /// The complete set of routes <c>MapIdentityApi</c> contributed when this filter was written,
    /// measured against <c>Microsoft.AspNetCore.App</c> 10.0.12 by enumerating a live
    /// <c>EndpointDataSource</c>.
    /// </summary>
    private static readonly string[] KnownIdentityApiRoutes =
    [
        "/spark/auth/register",
        "/spark/auth/login",
        "/spark/auth/refresh",
        "/spark/auth/confirmEmail",
        "/spark/auth/resendConfirmationEmail",
        "/spark/auth/forgotPassword",
        "/spark/auth/resetPassword",
        "/spark/auth/manage/2fa",
        "/spark/auth/manage/info",
    ];

    /// <summary>
    /// Fails loudly when <c>MapIdentityApi</c> contributes a route this filter does not recognise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <see cref="IsAllowed"/> classifies by suffix and ends in <c>return true</c>, so anything
    /// Microsoft adds in a servicing update is published in <em>every</em> mode, including
    /// <see cref="SparkLocalCredentials.Disabled"/>. Today that exposes nothing — 10.0.12 maps no
    /// passkey route, and the list above is the whole surface — but it is exactly the mechanism by
    /// which a future release could reintroduce a credential surface an application switched off,
    /// silently and on a patch upgrade.
    /// </para>
    /// <para>
    /// Deliberately a guard rather than an inversion of the filter to deny-by-default. Denying
    /// unknown routes would silently <em>drop</em> a route Spark ought to carry, trading a loud
    /// failure for a quiet one — and a quiet one in the authentication surface is the worse of the
    /// two. This way the upgrade breaks at startup, in one place, with the route named.
    /// </para>
    /// </remarks>
    private static void GuardAgainstUnrecognizedIdentityRoutes(UnpublishedEndpointRouteBuilder throwaway)
    {
        var unknown = throwaway.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(raw => raw is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(raw => !KnownIdentityApiRoutes.Contains(raw!, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (unknown.Length == 0)
            return;

        throw new InvalidOperationException(
            $"MapIdentityApi mapped {unknown.Length} route(s) Spark does not recognise: {string.Join(", ", unknown)}. " +
            "Spark filters that surface by name, and unrecognised routes are published in every SparkLocalCredentials " +
            "mode — including Disabled. Classify each one in LocalCredentialEndpointFilter.IsAllowed and add it to " +
            "KnownIdentityApiRoutes.");
    }

    /// <summary>
    /// Refuses to map an authentication surface nobody can sign into.
    /// </summary>
    /// <remarks>
    /// Lives here, next to the mode that causes it, rather than in the <c>AddAuthentication</c>
    /// wrapper — the check then holds for every path that reaches the mapper, and cannot be skipped
    /// by a caller that mounts the endpoints itself. Spark already refuses unsatisfiable
    /// configuration elsewhere for the same reason: <c>AddRateLimiter</c> throws on an empty
    /// path-prefix set, and <c>SparkModuleRegistry.AddMiddleware</c> throws for an applied stage.
    /// </remarks>
    private static void GuardAgainstUnreachableSignIn(IServiceProvider services)
    {
        var providers = ExternalAuthenticationSchemes.GetInteractiveAsync(services).GetAwaiter().GetResult();
        if (providers.Count > 0)
            return;

        throw new InvalidOperationException(
            "Spark authentication is configured with LocalCredentials = Disabled, but no external "
            + "authentication provider is registered, so no user could sign in. Register a provider "
            + "(for example identity.AddGitHub(...) via the configureProviders callback), or use "
            + "SparkLocalCredentials.SignInOnly or SparkLocalCredentials.Full instead.");
    }

    /// <summary>
    /// Refuses a password sign-in that accepts no identifier: with
    /// <see cref="SparkAuthenticationOptions.SignInIdentifiers"/> allowing neither the email nor the
    /// user name, <c>/login</c> is mapped but every attempt is a 401. Only called outside
    /// <see cref="SparkLocalCredentials.Disabled"/>, where the option is read at all.
    /// </summary>
    private static void GuardAgainstNoSignInIdentifier(IServiceProvider services)
    {
        var identifiers = services.GetService<IOptions<SparkAuthenticationOptions>>()?.Value.SignInIdentifiers
            ?? SparkSignInIdentifiers.Email | SparkSignInIdentifiers.UserName;
        if ((identifiers & (SparkSignInIdentifiers.Email | SparkSignInIdentifiers.UserName)) != 0)
            return;

        throw new InvalidOperationException(
            "Spark authentication maps password sign-in, but SignInIdentifiers allows neither "
            + "SparkSignInIdentifiers.Email nor SparkSignInIdentifiers.UserName, so no user could sign in "
            + "with a password. Allow at least one, or use SparkLocalCredentials.Disabled.");
    }

    /// <summary>
    /// Refuses a configuration whose link-confirmation mail would never be sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SparkExternalLoginLinking.ConfirmByEmail"/> is only as good as the message it
    /// sends: the link is made when a confirmation is followed, so a message that goes nowhere means
    /// the link is never made and nothing reports a failure. The user sees a sign-in that appears to
    /// do nothing, and the log stays clean.
    /// </para>
    /// <para>
    /// <b>The check is simply whether a sender is registered</b>, which it can be because Spark
    /// deliberately ships <em>no</em> default for
    /// <see cref="ISparkLinkConfirmationSender{TUser}"/>. That is worth contrasting with ASP.NET
    /// Identity's mail: there, <c>AddIdentityApiEndpoints</c> <c>TryAdd</c>s a no-op sender, so
    /// absence is invisible and the only way to detect it is to recognise an internal type by name.
    /// Measured 2026-09-20: <c>IEmailSender&lt;TUser&gt;</c> is <em>always</em>
    /// <c>DefaultMessageEmailSender&lt;TUser&gt;</c> whether or not a transport exists, so the
    /// obvious guard against the generic interface would never fire. Shipping no default turns that
    /// whole problem into a null check.
    /// </para>
    /// </remarks>
    private static void GuardAgainstSilentlyDiscardedMail<TUser>(IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var options = services.GetService<IOptions<SparkAuthenticationOptions>>()?.Value;
        if (options?.ExternalLoginLinking != SparkExternalLoginLinking.ConfirmByEmail)
            return;

        // In a scope: senders are scoped (M8 registers a factory that yields none without MailManager).
        using (var scope = services.CreateScope())
            if (scope.ServiceProvider.GetService<ISparkLinkConfirmationSender<TUser>>() is not null)
                return;

        throw new InvalidOperationException(
            "Spark authentication is configured with ExternalLoginLinking = ConfirmByEmail, but no "
            + $"{nameof(ISparkLinkConfirmationSender<TUser>)} is registered, so no confirmation "
            + "would ever be sent and no external login would ever be linked. Register one, or use "
            + "SparkExternalLoginLinking.WhenSignedIn or SparkExternalLoginLinking.Disabled "
            + "instead.");
    }

    /// <summary>
    /// #460 D6: refuses a registration surface whose account mail goes nowhere — Identity's no-op
    /// sender, or Spark's MailManager-backed sender with no MailManager. A user who registers would
    /// never get the confirmation link, and nobody could ever reset a password. Opt out with
    /// <see cref="SparkAuthenticationOptions.AllowUnconfirmedRegistration"/> or
    /// <c>Spark:Auth:AllowUnconfirmedRegistration=true</c>.
    /// </summary>
    internal static void GuardAgainstRegistrationWithoutMail<TUser>(IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var options = services.GetService<IOptions<SparkAuthenticationOptions>>()?.Value;
        var configured = services.GetService<Microsoft.Extensions.Configuration.IConfiguration>()?["Spark:Auth:AllowUnconfirmedRegistration"];
        if (options?.AllowUnconfirmedRegistration == true || string.Equals(configured, "true", StringComparison.OrdinalIgnoreCase))
            return;

        using var scope = services.CreateScope();
        var sender = scope.ServiceProvider.GetService<IEmailSender<TUser>>();
        var discards = sender is null
            || SparkAuthenticationExtensions.IsIdentityNoOpSender(sender.GetType())
            || sender is SparkMailEmailSender<TUser> { CanSend: false };
        if (!discards)
            return;

        throw new InvalidOperationException(
            "Spark authentication maps registration (LocalCredentials = Full), but account mail would be "
            + "discarded: no mail transport is registered, so confirmation and password-reset links would "
            + "never arrive. Add MintPlayer.Spark.MailManager (spark.AddMailManager() with "
            + "Spark:Mail:Smtp:Host or Spark:Mail:PickupFolder), register your own IEmailSender<TUser>, use "
            + "SparkLocalCredentials.SignInOnly, or set Spark:Auth:AllowUnconfirmedRegistration=true to accept it.");
    }

    private static void StampAntiforgery(IEndpointConventionBuilder convention) =>
        convention.Add(builder =>
        {
            if (builder is RouteEndpointBuilder route
                && route.RoutePattern.RawText is { } raw
                && AntiforgeryGatedRoutes.Any(suffix => raw.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                && IsMutating(builder.Metadata))
            {
                route.Metadata.Add(new RequireAntiforgeryTokenAttribute(true));
            }
        });

    /// <summary>
    /// Gives each Microsoft endpoint Spark keeps an <see cref="EndpointTypeMetadata"/> naming its
    /// <see cref="SparkIdentityEndpoints"/> stand-in, so "is 2FA served?" is asked by type, like every
    /// endpoint class. The routes Spark replaces are dropped by <see cref="IsAllowed"/> and need none;
    /// Spark's replacements are endpoint classes (<c>Endpoints/Account</c>) and carry their own.
    /// </summary>
    private static void StampEndpointTypes(IEndpointConventionBuilder convention) =>
        convention.Add(builder =>
        {
            if (builder is not RouteEndpointBuilder { RoutePattern.RawText: { } raw } route)
                return;

            Type? endpointType = raw.ToLowerInvariant() switch
            {
                "/spark/auth/login" => typeof(SparkIdentityEndpoints.Login),
                "/spark/auth/refresh" => typeof(SparkIdentityEndpoints.Refresh),
                "/spark/auth/manage/2fa" => typeof(SparkIdentityEndpoints.TwoFactor),
                "/spark/auth/manage/info" when !IsMutating(builder.Metadata) => typeof(SparkIdentityEndpoints.Info),
                _ => null,
            };

            if (endpointType is not null)
                route.Metadata.Add(new EndpointTypeMetadata(endpointType));
        });

    private static bool IsAllowed(Endpoint endpoint, SparkLocalCredentials mode)
    {
        if (endpoint is not RouteEndpoint route || route.RoutePattern.RawText is not { } raw)
            return true;

        bool Matches(params string[] suffixes) =>
            suffixes.Any(suffix => raw.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        // Spark maps its own version of these in every mode, classified there (register and
        // resendConfirmationEmail in Full only; the recovery family outside Disabled; confirmEmail
        // everywhere). GET and POST /manage/info share one route pattern, so the POST — replaced —
        // is told apart by method; the GET, which only reads, stays Microsoft's.
        if (Matches(ReplacedBySpark))
            return false;

        if (Matches("/manage/info") && IsMutating(route.Metadata))
            return false;

        if (mode != SparkLocalCredentials.Disabled)
            return true;

        if (Matches(PasswordSignInRoutes))
            return false;

        return true;
    }

    private static bool IsMutating(IEnumerable<object> metadata) =>
        metadata.OfType<HttpMethodMetadata>().FirstOrDefault() is { } methods
        && methods.HttpMethods.Any(method =>
            !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method));

    /// <summary>
    /// Collects endpoint data sources without publishing them, so a mapper can be run for its
    /// output rather than for its effect.
    /// </summary>
    private sealed class UnpublishedEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed class FixedEndpointDataSource(IReadOnlyList<Endpoint> endpoints) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;
        public override IChangeToken GetChangeToken() => NullChangeToken.Singleton;

        private sealed class NullChangeToken : IChangeToken
        {
            public static readonly NullChangeToken Singleton = new();
            public bool HasChanged => false;
            public bool ActiveChangeCallbacks => false;
            public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => NoopDisposable.Singleton;

            private sealed class NoopDisposable : IDisposable
            {
                public static readonly NoopDisposable Singleton = new();
                public void Dispose() { }
            }
        }
    }
}
