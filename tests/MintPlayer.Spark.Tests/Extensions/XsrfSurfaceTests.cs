using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Extensions;

/// <summary>
/// The CSRF surface of the framework itself: every endpoint Spark maps that answers a mutating HTTP
/// method, and what each one says about antiforgery.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this reads the route table rather than driving HTTP.</b> A status code only proves an
/// endpoint behaved one way on one path through one pipeline; the requirement is a property of the
/// endpoint's metadata, which is what both routing and Spark's gate actually consult. The
/// behavioural half — that the gate really answers 400, and really lets a correctly-tokened request
/// through — is pinned end-to-end in <c>MintPlayer.Spark.E2E.Tests/Security/XsrfEnforcementTests</c>
/// against a real Kestrel host. Neither test replaces the other.
/// </para>
/// <para>
/// ⚠️ <b>The assertions are exact sets.</b> A bound would pass when a new unprotected mutating
/// endpoint is added, which is the only failure mode worth having a test for. Adding an entry to
/// <see cref="CoreUnprotected"/> or <see cref="AuthUnprotected"/> is a decision that a forged
/// cross-site request may reach that endpoint — make it deliberately, and put the reason next to the
/// line.
/// </para>
/// <para>
/// <b>Three positions, not two.</b> <c>Required</c> is an explicit
/// <c>RequireAntiforgeryTokenAttribute(true)</c>; <c>Exempt</c> is an explicit
/// <c>(false)</c>, which wins over the inverted default; <c>Unstated</c> is no metadata at all,
/// which since 11.0.0 means "checked when the caller has an ambient credential inside
/// <c>SparkAntiforgeryOptions.PathPrefixes</c>, and not otherwise". Unstated is therefore no longer
/// a synonym for unprotected — but it does mean the answer lives in configuration rather than in the
/// endpoint, which is why the reads below carry a written reason.
/// </para>
/// </remarks>
public class XsrfSurfaceTests : SparkTestDriver
{
    private static readonly string[] MutatingMethods = ["POST", "PUT", "PATCH", "DELETE"];

    /// <summary>Core endpoints that demand a token.</summary>
    private static readonly string[] CoreRequired =
    [
        "DELETE /spark/lookupref/{name}/{key}",
        "POST /spark/actions/execute",
        "POST /spark/lookupref/{name}",
        "POST /spark/po/create",
        "POST /spark/po/delete",
        "POST /spark/po/delete-row",
        "POST /spark/po/new",
        "POST /spark/po/refresh",
        "POST /spark/po/update",
        "PUT /spark/lookupref/{name}/{key}",
    ];

    /// <summary>
    /// Core endpoints that state nothing. Every one of these is a <b>read</b> that is a POST only
    /// because it needs a request body; none of them changes state.
    /// <para>
    /// CSRF is not a meaningful threat to a read: the same-origin policy stops the attacker's page
    /// from seeing the response, so forging one achieves nothing the attacker could not achieve by
    /// fetching the endpoint themselves. They are still <em>covered</em> in practice, because since
    /// 11.0.0 an ambient-credentialed POST under <c>/spark</c> is checked by default — but an
    /// anonymous read stays reachable, which is the behaviour the public-data apps rely on.
    /// </para>
    /// ⚠️ If any of these ever gains a side effect it needs <c>RequireAntiforgeryTokenAttribute</c>
    /// in the same commit, and this list is where that gets noticed.
    /// </summary>
    private static readonly string[] CoreUnprotected =
    [
        "POST /spark/actions/list",
        "POST /spark/po/load",
        "POST /spark/queries/distinct-values",
        "POST /spark/queries/execute",
        "POST /spark/queries/get",
    ];

    /// <summary>
    /// Auth endpoints that demand a token.
    /// <para>
    /// ⚠️ <c>/spark/auth/login</c> is on this list as of 11.0.0 and did not used to be. It was
    /// excluded on the reasoning that an anonymous caller has no XSRF-TOKEN cookie to validate,
    /// which is simply not how Spark behaves — the cookie is minted on every response, anonymous
    /// ones included. What the exclusion left open was login CSRF: an attacker page logs the victim
    /// into the <em>attacker's</em> account, and everything the victim does next is captured there.
    /// </para>
    /// </summary>
    private static readonly string[] AuthRequired =
    [
        "POST /spark/auth/forgotPassword",
        "POST /spark/auth/login",
        "POST /spark/auth/logout",
        "POST /spark/auth/manage/2fa",
        "POST /spark/auth/manage/info",
        "POST /spark/auth/resetPassword",
    ];

    /// <summary>
    /// ⚠️ Exactly one endpoint may be explicitly exempt, and it has to be: an antiforgery token is
    /// bound to a principal, so the endpoint whose job is to re-mint one after the principal changes
    /// can never require a current one. Demanding it would deadlock every client whose identity just
    /// changed — which is every client that just signed in or out.
    /// </summary>
    private static readonly string[] AuthExempt =
    [
        "POST /spark/auth/csrf-refresh",
    ];

    /// <summary>
    /// Auth endpoints that state nothing. All three are <b>anonymous</b>: the caller presents no
    /// ambient credential, so there is nothing for a forgery to ride and the inverted default does
    /// not fire either. Gating them would break programmatic sign-up without closing an attack —
    /// an attacker can POST these from their own server just as easily as from a victim's browser,
    /// and gains nothing by using the victim's.
    /// <para>
    /// This is the distinction that makes <c>/login</c> different and is worth keeping straight:
    /// login is the one anonymous endpoint whose <em>effect</em> is to create ambient authority in
    /// the victim's browser.
    /// </para>
    /// </summary>
    private static readonly string[] AuthUnprotected =
    [
        "POST /spark/auth/refresh",
        "POST /spark/auth/register",
        "POST /spark/auth/resendConfirmationEmail",
    ];

    [Fact]
    public async Task Core_spark_surface_matches_its_declared_antiforgery_positions()
    {
        var docType = GuardedDocModel.For(Guid.Parse("5a5a0000-1111-2222-3333-444455556666"));
        await using var factory = new SparkEndpointFactory<GuardedContext>(
            Store, [docType], security: SparkTestSecurity.Empty);

        var (required, exempt, unstated) = Classify(factory.GetService<EndpointDataSource>());

        Assert.Equal(Sorted(CoreRequired), required);
        Assert.Equal(Sorted([]), exempt);
        Assert.Equal(Sorted(CoreUnprotected), unstated);
    }

    [Fact]
    public async Task Auth_surface_matches_its_declared_antiforgery_positions()
    {
        using var host = await StartAuthHostAsync();

        var (required, exempt, unstated) = Classify(
            host.Services.GetRequiredService<EndpointDataSource>());

        Assert.Equal(Sorted(AuthRequired), required);
        Assert.Equal(Sorted(AuthExempt), exempt);
        Assert.Equal(Sorted(AuthUnprotected), unstated);
    }

    /// <summary>
    /// ⚠️ Pins the specific regression this branch exists to prevent. Stated separately from the set
    /// assertion above because a set assertion tells you "something moved", and a reviewer looking at
    /// a failing diff of twenty routes will not necessarily see which one mattered.
    /// </summary>
    [Fact]
    public async Task Login_requires_an_antiforgery_token()
    {
        using var host = await StartAuthHostAsync();

        var login = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/spark/auth/login"
                      && Methods(e).Contains("POST"));

        var metadata = login.Metadata.GetMetadata<IAntiforgeryMetadata>();

        Assert.NotNull(metadata);
        Assert.True(metadata!.RequiresValidation,
            "POST /spark/auth/login must require an antiforgery token: without it an attacker page "
            + "can sign a victim's browser into the attacker's own account (login CSRF), and every "
            + "subsequent action the victim takes lands in an account the attacker controls.");
    }

    /// <summary>
    /// ⚠️ The mirror of the test above, and just as load-bearing. csrf-refresh must stay reachable
    /// without a token or a client whose identity just changed can never obtain one.
    /// </summary>
    [Fact]
    public async Task Csrf_refresh_is_explicitly_exempt()
    {
        using var host = await StartAuthHostAsync();

        var refresh = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/spark/auth/csrf-refresh");

        var metadata = refresh.Metadata.GetMetadata<IAntiforgeryMetadata>();

        Assert.NotNull(metadata);
        Assert.False(metadata!.RequiresValidation,
            "POST /spark/auth/csrf-refresh must NOT require an antiforgery token. Its whole purpose "
            + "is to hand a fresh, correctly-bound token to a client whose old one just became "
            + "stale, so requiring a valid one first is a deadlock with no recovery.");
    }

    private Task<IHost> StartAuthHostAsync() =>
        new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.AddAuthorization();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapSparkIdentityApi<SparkUser>(SparkLocalCredentials.Full));
                }))
            .StartAsync();

    private static (string[] Required, string[] Exempt, string[] Unstated) Classify(
        EndpointDataSource source)
    {
        var mutating = source.Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => (Endpoint: endpoint, Methods: Methods(endpoint)))
            .SelectMany(
                pair => pair.Methods.Where(m => MutatingMethods.Contains(m, StringComparer.OrdinalIgnoreCase)),
                (pair, method) => (
                    Key: $"{method.ToUpperInvariant()} {pair.Endpoint.RoutePattern.RawText}",
                    Metadata: pair.Endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>()))
            .ToArray();

        return (
            Sorted([.. mutating.Where(e => e.Metadata is { RequiresValidation: true }).Select(e => e.Key)]),
            Sorted([.. mutating.Where(e => e.Metadata is { RequiresValidation: false }).Select(e => e.Key)]),
            Sorted([.. mutating.Where(e => e.Metadata is null).Select(e => e.Key)]));
    }

    private static IReadOnlyList<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];

    private static string[] Sorted(IEnumerable<string> values) =>
        [.. values.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal)];
}
