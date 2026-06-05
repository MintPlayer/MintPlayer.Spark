using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// DI-shape tests for the public <see cref="ISparkBuilder"/> wrappers in
/// <see cref="SparkBuilderAuthorizationExtensions"/>. The wrappers are thin (delegate to
/// the internal extensions) but they pin the registry side-effects: <c>IdentityUserType</c>
/// must be set, and <c>MapSparkIdentityApi</c> must be queued in the endpoint registry.
/// </summary>
public class SparkBuilderExtensionsTests
{
    [Fact]
    public void AddAuthorization_delegates_to_AddSparkAuthorization_on_the_underlying_services()
    {
        var builder = new TestBuilder();

        builder.AddAuthorization(options =>
        {
            options.SecurityFilePath = "via-builder.json";
        });

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        options.SecurityFilePath.Should().Be("via-builder.json");
    }

    [Fact]
    public void AddAuthorization_returns_the_builder_for_chaining()
    {
        var builder = new TestBuilder();

        var returned = builder.AddAuthorization();

        returned.Should().BeSameAs(builder);
    }

    [Fact]
    public void AddAuthentication_records_the_identity_user_type_in_the_registry()
    {
        var builder = new TestBuilder();

        builder.AddAuthentication<SparkUser>();

        builder.Registry.IdentityUserType.Should().Be(typeof(SparkUser));
    }

    [Fact]
    public void AddAuthentication_records_the_custom_user_subtype_in_the_registry()
    {
        var builder = new TestBuilder();

        builder.AddAuthentication<TestAppUser>();

        builder.Registry.IdentityUserType.Should().Be(typeof(TestAppUser));
    }

    [Fact]
    public void AddAuthentication_invokes_configureProviders_with_the_identity_builder()
    {
        var builder = new TestBuilder();
        var captured = false;

        builder.AddAuthentication<SparkUser>(
            configureIdentity: null,
            configureProviders: identityBuilder =>
            {
                captured = true;
                identityBuilder.Should().NotBeNull();
                identityBuilder.UserType.Should().Be(typeof(SparkUser));
            });

        captured.Should().BeTrue();
    }

    [Fact]
    public void AddAuthentication_returns_the_builder_for_chaining()
    {
        var builder = new TestBuilder();

        var returned = builder.AddAuthentication<SparkUser>();

        returned.Should().BeSameAs(builder);
    }

    private sealed class TestAppUser : SparkUser { }

    private sealed class TestBuilder : ISparkBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration? Configuration => null;
        public SparkModuleRegistry Registry { get; } = new();
    }
}
