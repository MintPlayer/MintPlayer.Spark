using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// DI-shape tests for <see cref="SparkAuthenticationExtensions.AddSparkAuthentication{TUser}"/>.
/// Asserts registration outcomes against a built provider — no host pipeline needed. The
/// endpoint-mapping side (<c>MapSparkIdentityApi</c>) requires <c>IEndpointRouteBuilder</c>
/// and is intentionally excluded; it's covered by integration tests.
/// </summary>
public class SparkAuthenticationExtensionsTests
{
    [Fact]
    public void AddSparkAuthentication_registers_RavenDb_user_and_role_stores()
    {
        // Inspect the descriptor rather than resolving — UserStore<T> needs IDocumentStore
        // to construct, and registering a real Raven instance is the SparkTestDriver's job.
        // The shape contract here is "the right implementation type is bound to the right
        // interface as Scoped".
        var services = new ServiceCollection();

        services.AddSparkAuthentication<SparkUser>();

        services.Should().ContainSingle(d => d.ServiceType == typeof(IUserStore<SparkUser>))
            .Which.Should().BeEquivalentTo(new
            {
                ImplementationType = typeof(UserStore<SparkUser>),
                Lifetime = ServiceLifetime.Scoped,
            });
        services.Should().ContainSingle(d => d.ServiceType == typeof(IRoleStore<SparkRole>))
            .Which.Should().BeEquivalentTo(new
            {
                ImplementationType = typeof(RoleStore),
                Lifetime = ServiceLifetime.Scoped,
            });
    }

    [Fact]
    public void AddSparkAuthentication_supports_custom_user_subtypes()
    {
        var services = new ServiceCollection();

        services.AddSparkAuthentication<TestAppUser>();

        services.Should().ContainSingle(d => d.ServiceType == typeof(IUserStore<TestAppUser>))
            .Which.ImplementationType.Should().Be(typeof(UserStore<TestAppUser>));
    }

    [Fact]
    public void AddSparkAuthentication_invokes_configureIdentity_callback()
    {
        var services = new ServiceCollection();
        var captured = false;

        services.AddSparkAuthentication<SparkUser>(options =>
        {
            captured = true;
            options.Password.RequireDigit = true;
            options.Lockout.MaxFailedAccessAttempts = 7;
        });

        using var provider = services.BuildServiceProvider();
        var identityOptions = provider.GetRequiredService<IOptions<IdentityOptions>>().Value;

        captured.Should().BeTrue("the callback should run as part of options binding");
        identityOptions.Password.RequireDigit.Should().BeTrue();
        identityOptions.Lockout.MaxFailedAccessAttempts.Should().Be(7);
    }

    [Fact]
    public void AddSparkAuthentication_pins_antiforgery_header_to_X_XSRF_TOKEN()
    {
        // The Angular client (and the SparkClient HTTP layer) reads/writes XSRF via this
        // header name. A regression here silently breaks every authenticated client.
        var services = new ServiceCollection();

        services.AddSparkAuthentication<SparkUser>();

        using var provider = services.BuildServiceProvider();
        var antiforgeryOptions = provider.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;

        antiforgeryOptions.HeaderName.Should().Be("X-XSRF-TOKEN");
    }

    [Fact]
    public void AddSparkAuthentication_returns_IdentityBuilder_for_chaining_external_providers()
    {
        var services = new ServiceCollection();

        var builder = services.AddSparkAuthentication<SparkUser>();

        builder.Should().NotBeNull();
        builder.UserType.Should().Be(typeof(SparkUser));
    }

    private sealed class TestAppUser : SparkUser { }
}
