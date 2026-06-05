using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization;
using MintPlayer.Spark.Authorization.Configuration;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// DI-shape tests for <see cref="SparkAuthorizationExtensions.AddSparkAuthorization"/> and
/// <see cref="SparkAuthorizationExtensions.AddGroupMembershipProvider{TProvider}"/>. The
/// authorization options are read on every CRUD permission check, so a regression in
/// option propagation silently breaks the security model — these tests pin that path.
/// </summary>
public class SparkAuthorizationExtensionsTests
{
    [Fact]
    public void AddSparkAuthorization_propagates_all_option_fields_through_Configure()
    {
        var services = new ServiceCollection();

        services.AddSparkAuthorization(options =>
        {
            options.SecurityFilePath = "TestData/security-test.json";
            options.DefaultBehavior = DefaultAccessBehavior.AllowAll;
            options.CacheRights = false;
            options.CacheExpirationMinutes = 42;
            options.EnableHotReload = true;
        });

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        resolved.SecurityFilePath.Should().Be("TestData/security-test.json");
        resolved.DefaultBehavior.Should().Be(DefaultAccessBehavior.AllowAll);
        resolved.CacheRights.Should().BeFalse();
        resolved.CacheExpirationMinutes.Should().Be(42);
        resolved.EnableHotReload.Should().BeTrue();
    }

    [Fact]
    public void AddSparkAuthorization_uses_default_options_when_no_callback_supplied()
    {
        var services = new ServiceCollection();

        services.AddSparkAuthorization();

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        resolved.Should().NotBeNull();
        // DefaultAccessBehavior default is DenyAll — the safer choice; if this drifts,
        // every undefined right silently flips from denied to allowed.
        resolved.DefaultBehavior.Should().Be(DefaultAccessBehavior.DenyAll);
    }

    [Fact]
    public void AddSparkAuthorization_registers_HttpContextAccessor()
    {
        var services = new ServiceCollection();

        services.AddSparkAuthorization();

        using var provider = services.BuildServiceProvider();
        provider.GetService<IHttpContextAccessor>().Should().NotBeNull();
    }

    [Fact]
    public void AddSparkAuthorization_returns_the_service_collection_for_chaining()
    {
        var services = new ServiceCollection();

        var returned = services.AddSparkAuthorization();

        returned.Should().BeSameAs(services);
    }

    [Fact]
    public void AddGroupMembershipProvider_replaces_the_existing_registration()
    {
        var services = new ServiceCollection();
        services.AddSparkAuthorization(); // registers the default provider

        services.AddGroupMembershipProvider<FakeMembershipProvider>();

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetService<IGroupMembershipProvider>();

        resolved.Should().BeOfType<FakeMembershipProvider>(
            "AddGroupMembershipProvider must remove the prior registration before adding the override");
    }

    [Fact]
    public void AddGroupMembershipProvider_works_when_no_prior_registration_exists()
    {
        var services = new ServiceCollection();
        services.AddHttpContextAccessor();

        services.AddGroupMembershipProvider<FakeMembershipProvider>();

        using var provider = services.BuildServiceProvider();
        provider.GetService<IGroupMembershipProvider>().Should().BeOfType<FakeMembershipProvider>();
    }

    [Fact]
    public void AddGroupMembershipProvider_returns_the_service_collection_for_chaining()
    {
        var services = new ServiceCollection();
        services.AddSparkAuthorization();

        var returned = services.AddGroupMembershipProvider<FakeMembershipProvider>();

        returned.Should().BeSameAs(services);
    }

    private sealed class FakeMembershipProvider : IGroupMembershipProvider
    {
        public Task<IEnumerable<string>> GetCurrentUserGroupsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<string>>([]);
    }
}
