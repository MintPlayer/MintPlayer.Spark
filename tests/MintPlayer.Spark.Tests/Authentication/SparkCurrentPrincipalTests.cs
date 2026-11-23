using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Authentication;

/// <summary>#460: the current-user seam stamping, moderation and audit read.</summary>
public class SparkCurrentPrincipalTests
{
    private static HttpContextSparkCurrentPrincipal For(ClaimsPrincipal? principal)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(principal is null ? null : new DefaultHttpContext { User = principal });
        return new HttpContextSparkCurrentPrincipal(accessor, Visitors());
    }

    private static readonly IDataProtectionProvider DataProtection = new EphemeralDataProtectionProvider();

    private static SparkVisitorCookies Visitors(SparkVisitorOptions? options = null)
        => new(DataProtection, Options.Create(options ?? new SparkVisitorOptions()));

    private static (HttpContextSparkCurrentPrincipal Principal, DefaultHttpContext Context) Anonymous(string? cookieHeader = null, SparkVisitorOptions? options = null)
    {
        var context = new DefaultHttpContext();
        if (cookieHeader is not null)
            context.Request.Headers.Cookie = cookieHeader;
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return (new HttpContextSparkCurrentPrincipal(accessor, Visitors(options)), context);
    }

    private static string SetCookie(HttpContext context, string name)
        => context.Response.Headers.SetCookie.Single(c => c!.StartsWith(name + "=", StringComparison.Ordinal))!;

    private static string CookieValue(HttpContext context, string name)
        => SetCookie(context, name).Split(';')[0][(name.Length + 1)..];

    // --- Visitors (#710, docs/notification_bell_PRD.md R1) ---------------------------------------

    [Fact]
    public void A_plain_visit_has_no_visitor_and_gets_no_cookie()
    {
        var (principal, context) = Anonymous();

        principal.VisitorId.Should().BeNull();
        context.Response.Headers.SetCookie.Should().BeEmpty();
    }

    [Fact]
    public void EnsureVisitorId_issues_the_protected_cookie_and_the_readable_presence_flag()
    {
        var (principal, context) = Anonymous();

        var id = principal.EnsureVisitorId();

        id.Should().StartWith(SparkVisitorOptions.IdPrefix).And.HaveLength(SparkVisitorOptions.IdPrefix.Length + 32);
        principal.VisitorId.Should().Be(id, "the id issued in this request is seen in this request");
        var visitor = SetCookie(context, SparkVisitorOptions.CookieName).ToLowerInvariant();
        visitor.Should().Contain("httponly").And.Contain("samesite=lax").And.Contain("expires=").And.Contain("path=/");
        visitor.Should().NotContain(id![SparkVisitorOptions.IdPrefix.Length..], "the cookie carries the protected id, not the id");
        var presence = SetCookie(context, SparkVisitorOptions.PresenceCookieName).ToLowerInvariant();
        presence.Should().StartWith(SparkVisitorOptions.PresenceCookieName + "=1;").And.NotContain("httponly");
    }

    [Fact]
    public void The_issued_cookie_identifies_the_same_visitor_on_the_next_request_and_EnsureVisitorId_keeps_it()
    {
        var (first, firstContext) = Anonymous();
        var id = first.EnsureVisitorId();
        var cookie = $"{SparkVisitorOptions.CookieName}={CookieValue(firstContext, SparkVisitorOptions.CookieName)}";

        var (second, secondContext) = Anonymous(cookie);

        second.VisitorId.Should().Be(id);
        second.EnsureVisitorId().Should().Be(id, "renewing keeps the id");
        SetCookie(secondContext, SparkVisitorOptions.CookieName).Should().Contain("expires=", "renewed: the lifetime slides");
    }

    [Theory]
    [InlineData("not-a-protected-payload")]
    [InlineData("CfDJ8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void A_tampered_or_foreign_cookie_is_no_visitor(string value)
    {
        var (principal, _) = Anonymous($"{SparkVisitorOptions.CookieName}={value}");

        principal.VisitorId.Should().BeNull();
    }

    [Fact]
    public void A_payload_protected_for_another_purpose_is_no_visitor()
    {
        var foreign = DataProtection.CreateProtector("Something.Else").ToTimeLimitedDataProtector()
            .Protect(new string('a', 32), TimeSpan.FromHours(1));

        var (principal, _) = Anonymous($"{SparkVisitorOptions.CookieName}={foreign}");

        principal.VisitorId.Should().BeNull();
    }

    [Fact]
    public void A_signed_in_caller_gets_no_visitor_id_and_no_cookie()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var context = new DefaultHttpContext { User = SignedIn(new Claim(ClaimTypes.NameIdentifier, "users/1-A")) };
        accessor.HttpContext.Returns(context);
        var principal = new HttpContextSparkCurrentPrincipal(accessor, Visitors());

        principal.EnsureVisitorId().Should().BeNull();
        context.Response.Headers.SetCookie.Should().BeEmpty();
    }

    [Fact]
    public void SessionCookie_mode_issues_cookies_without_an_expiry()
    {
        var (principal, context) = Anonymous(options: new SparkVisitorOptions { SessionCookie = true });

        principal.EnsureVisitorId().Should().NotBeNull();

        SetCookie(context, SparkVisitorOptions.CookieName).ToLowerInvariant().Should().NotContain("expires=");
        SetCookie(context, SparkVisitorOptions.PresenceCookieName).ToLowerInvariant().Should().NotContain("expires=");
    }

    [Fact]
    public void ForgetVisitor_clears_both_cookies()
    {
        var (first, firstContext) = Anonymous();
        first.EnsureVisitorId();
        var (second, secondContext) = Anonymous($"{SparkVisitorOptions.CookieName}={CookieValue(firstContext, SparkVisitorOptions.CookieName)}");

        second.ForgetVisitor();

        second.VisitorId.Should().BeNull();
        SetCookie(secondContext, SparkVisitorOptions.CookieName).ToLowerInvariant().Should().Contain("expires=thu, 01 jan 1970");
        SetCookie(secondContext, SparkVisitorOptions.PresenceCookieName).ToLowerInvariant().Should().Contain("expires=thu, 01 jan 1970");
    }

    private static ClaimsPrincipal SignedIn(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "Test"));

    [Fact]
    public void A_signed_in_user_is_identified_by_the_name_identifier_claim()
    {
        var user = For(SignedIn(new Claim(ClaimTypes.NameIdentifier, "users/1-A"), new Claim(ClaimTypes.Name, "alice")));

        user.IsAuthenticated.Should().BeTrue();
        user.Id.Should().Be("users/1-A");
    }

    [Fact]
    public void An_anonymous_caller_has_no_id_even_if_the_principal_carries_one()
    {
        // An unauthenticated identity with a NameIdentifier is not a user; stamping it would let a
        // forged or half-built principal author content.
        var user = For(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "users/1-A")])));

        user.IsAuthenticated.Should().BeFalse();
        user.Id.Should().BeNull();
    }

    [Fact]
    public void An_authenticated_principal_without_an_id_claim_has_no_id()
    {
        var user = For(SignedIn(new Claim(ClaimTypes.Name, "module-certificate")));

        user.IsAuthenticated.Should().BeTrue();
        user.Id.Should().BeNull();
    }

    private sealed class SchemeFeature(SparkCredentialScheme scheme) : ISparkAuthenticatedSchemeFeature
    {
        public SparkCredentialScheme Scheme { get; } = scheme;
    }

    private static HttpContextSparkCurrentPrincipal ForBearer(ClaimsPrincipal principal, SparkCredentialScheme? scheme)
    {
        var context = new DefaultHttpContext { User = principal };
        if (scheme is not null)
            context.Features.Set<ISparkAuthenticatedSchemeFeature>(new SchemeFeature(scheme));
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return new HttpContextSparkCurrentPrincipal(accessor, Visitors());
    }

    /// <summary>MCP PRD D4, §4.6 (spike S2): a resource-server principal carries <c>sub</c>, not NameIdentifier.</summary>
    [Fact]
    public void A_bearer_principal_of_a_scheme_whose_subject_is_local_is_identified_by_sub()
    {
        var user = ForBearer(SignedIn(new Claim("sub", "SparkUsers/7-A")), new SparkCredentialScheme("Mcp", IsAmbient: false, SubjectIsLocalUserId: true));

        user.Id.Should().Be("SparkUsers/7-A", "an agent acting for a user must carry that user's id, or row security fails closed for it");
    }

    [Fact]
    public void A_bearer_principal_of_another_issuer_has_no_id()
    {
        // Fleet validating SparkId's tokens: their sub names a SparkId user, not a Fleet one.
        var foreign = ForBearer(SignedIn(new Claim("sub", "SparkUsers/7-A")), new SparkCredentialScheme("Spark:JwtBearer", IsAmbient: false));
        var unrecorded = ForBearer(SignedIn(new Claim("sub", "SparkUsers/7-A")), scheme: null);

        foreign.Id.Should().BeNull("another issuer's subject is not a local user id");
        unrecorded.Id.Should().BeNull("without the scheme there is no way to know whose subject it is");
    }

    [Fact]
    public void NameIdentifier_wins_over_sub()
    {
        var user = ForBearer(
            SignedIn(new Claim(ClaimTypes.NameIdentifier, "users/1-A"), new Claim("sub", "users/2-A")),
            new SparkCredentialScheme("Mcp", IsAmbient: false, SubjectIsLocalUserId: true));

        user.Id.Should().Be("users/1-A");
    }

    [Fact]
    public void No_request_is_an_anonymous_caller()
    {
        var user = For(principal: null);

        user.IsAuthenticated.Should().BeFalse();
        user.Id.Should().BeNull();
    }

    [Fact]
    public void AddSpark_registers_it_scoped()
    {
        var services = new ServiceCollection();
        services.AddSpark(_ => { });

        var descriptor = services.Should().ContainSingle(d => d.ServiceType == typeof(ISparkCurrentPrincipal)).Which;
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be(typeof(HttpContextSparkCurrentPrincipal));
    }
}
