using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Authentication;

/// <summary>#460: the current-user seam stamping, moderation and audit read.</summary>
public class SparkCurrentUserTests
{
    private static HttpContextSparkCurrentUser For(ClaimsPrincipal? principal)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(principal is null ? null : new DefaultHttpContext { User = principal });
        return new HttpContextSparkCurrentUser(accessor);
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

        var descriptor = services.Should().ContainSingle(d => d.ServiceType == typeof(ISparkCurrentUser)).Which;
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be(typeof(HttpContextSparkCurrentUser));
    }
}
