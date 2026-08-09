using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authentication;
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

    /// <summary>
    /// M9/F7. Spark's endpoints name no scheme, so ASP.NET runs only the default authenticate
    /// scheme — which meant a registered certificate or bearer handler never executed on a Spark
    /// endpoint at all, and its caller arrived anonymous with <c>Everyone</c> rights. Identity used
    /// to leave <c>Identity.BearerAndApplication</c> in place here and Spark never overrode it.
    /// <para>
    /// This assertion is the whole mechanism. Its failure mode is silent — the wrong default
    /// authenticates fewer callers rather than erroring — so nothing else would report it.
    /// </para>
    /// </summary>
    [Fact]
    public void AddAuthentication_makes_the_Spark_composite_the_default_authenticate_scheme()
    {
        var builder = new TestBuilder();

        builder.AddAuthentication<SparkUser>();

        var options = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<AuthenticationOptions>>().Value;

        options.DefaultAuthenticateScheme.Should().Be(SparkAuthenticationDefaults.CompositeScheme);
    }

    /// <summary>
    /// Only authenticate is redirected. The composite reads credentials and issues none, so a
    /// sign-in pointed at it would have nothing to write to and login would break.
    /// </summary>
    [Fact]
    public void AddAuthentication_leaves_sign_in_and_challenge_with_Identity()
    {
        var builder = new TestBuilder();

        builder.AddAuthentication<SparkUser>();

        var options = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<AuthenticationOptions>>().Value;

        options.DefaultSignInScheme.Should().NotBe(SparkAuthenticationDefaults.CompositeScheme);
        options.DefaultChallengeScheme.Should().NotBe(SparkAuthenticationDefaults.CompositeScheme);
    }

    /// <summary>
    /// Identity's two schemes are declared separately rather than as the combined
    /// <c>BearerAndApplication</c>, because the antiforgery gate has to know which one authenticated:
    /// the cookie is ambient and needs CSRF defence, the bearer is not and must not be obstructed
    /// by it. The combined scheme cannot answer that question.
    /// </summary>
    [Fact]
    public void AddAuthentication_registers_the_cookie_as_ambient_and_the_bearer_as_not()
    {
        var builder = new TestBuilder();

        builder.AddAuthentication<SparkUser>();

        builder.Registry.CredentialSchemes.Should().Contain(
            s => s.Name == IdentityConstants.ApplicationScheme && s.IsAmbient);
        builder.Registry.CredentialSchemes.Should().Contain(
            s => s.Name == IdentityConstants.BearerScheme && !s.IsAmbient);
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
