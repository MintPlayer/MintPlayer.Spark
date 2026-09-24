using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
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
    /// <summary>Routes that exist only to serve local (password) credentials.</summary>
    private static readonly string[] PasswordRecoveryRoutes =
        ["/confirmEmail", "/resendConfirmationEmail", "/forgotPassword", "/resetPassword"];

    private static readonly string[] PasswordSignInRoutes = ["/login", "/refresh"];

    /// <summary>
    /// Mutating Identity endpoints that Spark defends with double-submit CSRF. Microsoft's defaults
    /// attach no <see cref="IAntiforgeryMetadata"/>, so Spark stamps it on. <c>/login</c> is excluded
    /// deliberately: there is no session yet, so there is no XSRF-TOKEN cookie to validate.
    /// </summary>
    private static readonly string[] AntiforgeryGatedRoutes =
        ["/manage/2fa", "/manage/info", "/resetPassword", "/forgotPassword", "/logout"];

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
        {
            // The default takes the original code path verbatim — no throwaway builder, no
            // re-publication. Whatever the filter does or does not preserve cannot affect the
            // behaviour of an application that never opted in.
            StampAntiforgery(endpoints.MapGroup("/spark/auth").MapIdentityApi<TUser>());
            return;
        }

        if (mode == SparkLocalCredentials.Disabled)
            GuardAgainstUnreachableSignIn(endpoints.ServiceProvider);

        var throwaway = new UnpublishedEndpointRouteBuilder(endpoints.ServiceProvider);
        StampAntiforgery(throwaway.MapGroup("/spark/auth").MapIdentityApi<TUser>());

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

        if (services.GetService<ISparkLinkConfirmationSender<TUser>>() is not null)
            return;

        throw new InvalidOperationException(
            "Spark authentication is configured with ExternalLoginLinking = ConfirmByEmail, but no "
            + $"{nameof(ISparkLinkConfirmationSender<TUser>)} is registered, so no confirmation "
            + "would ever be sent and no external login would ever be linked. Register one, or use "
            + "SparkExternalLoginLinking.WhenSignedIn or SparkExternalLoginLinking.Disabled "
            + "instead.");
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

    private static bool IsAllowed(Endpoint endpoint, SparkLocalCredentials mode)
    {
        if (endpoint is not RouteEndpoint route || route.RoutePattern.RawText is not { } raw)
            return true;

        bool Matches(params string[] suffixes) =>
            suffixes.Any(suffix => raw.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        // Self-service registration goes in both non-default modes. resendConfirmationEmail goes
        // with it: an account nobody can create has nothing to confirm, and it is an unauthenticated
        // mail-send trigger keyed on an email address.
        if (Matches("/register", "/resendConfirmationEmail"))
            return false;

        if (mode != SparkLocalCredentials.Disabled)
            return true;

        if (Matches(PasswordSignInRoutes) || Matches(PasswordRecoveryRoutes))
            return false;

        // GET and POST /manage/info share one route pattern, so this has to discriminate on method.
        // The POST rotates the email address that the external login was provisioned against, which
        // would desynchronize it from the issuer-attested claim; the GET only reads.
        if (Matches("/manage/info") && IsMutating(route.Metadata))
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
