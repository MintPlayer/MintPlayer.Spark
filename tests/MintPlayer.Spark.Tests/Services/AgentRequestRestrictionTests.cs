using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// MCP PRD D13: an agent request's grant narrows the caller's rights inside
/// <see cref="IPermissionService"/>, so every tool honours it. Pins the three properties the hook must
/// keep: no restriction registered changes nothing, a restriction narrows, and a restriction never
/// widens what the caller's own rights deny.
/// </summary>
public class AgentRequestRestrictionTests
{
    private readonly IAccessControl _accessControl = Substitute.For<IAccessControl>();

    /// <summary>A restriction that allows exactly the listed <c>action/target</c> pairs.</summary>
    private sealed class Allowing(params string[] allowed) : IAgentRequestRestriction
    {
        public List<string> Asked { get; } = [];

        public ValueTask<bool> AllowsAsync(string action, string target, CancellationToken cancellationToken = default)
        {
            Asked.Add($"{action}/{target}");
            return ValueTask.FromResult(allowed.Contains($"{action}/{target}"));
        }
    }

    [Fact]
    public async Task Without_a_restriction_the_callers_rights_decide_alone()
    {
        _accessControl.IsAllowedAsync("Read/Person", Arg.Any<CancellationToken>()).Returns(true);
        var service = new PermissionService(_accessControl, agentRestrictions: []);

        (await service.IsAllowedAsync("Read", "Person")).Should().BeTrue();
    }

    [Fact]
    public async Task A_restriction_narrows_an_allowed_right()
    {
        _accessControl.IsAllowedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = new PermissionService(_accessControl, agentRestrictions: [new Allowing("Read/Car")]);

        (await service.IsAllowedAsync("Read", "Car")).Should().BeTrue();
        (await service.IsAllowedAsync("Read", "Person")).Should().BeFalse("the agent's grant leaves Person out");

        var act = async () => await service.EnsureAuthorizedAsync("Query", "Person");
        (await act.Should().ThrowAsync<SparkAccessDeniedException>()).Which.Message.Should().Contain("Query/Person");
    }

    [Fact]
    public async Task A_restriction_never_widens_a_denied_right()
    {
        _accessControl.IsAllowedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        var restriction = new Allowing("Read/Person");
        var service = new PermissionService(_accessControl, agentRestrictions: [restriction]);

        (await service.IsAllowedAsync("Read", "Person")).Should().BeFalse("security.json denies it, whatever the grant says");
        restriction.Asked.Should().BeEmpty("a restriction is asked only about what the caller's rights already allow");
    }

    [Fact]
    public async Task Every_restriction_must_allow()
    {
        _accessControl.IsAllowedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = new PermissionService(_accessControl,
            agentRestrictions: [new Allowing("Read/Car", "Read/Person"), new Allowing("Read/Car")]);

        (await service.IsAllowedAsync("Read", "Car")).Should().BeTrue();
        (await service.IsAllowedAsync("Read", "Person")).Should().BeFalse();
    }

    [Fact]
    public async Task The_restriction_receives_the_action_and_the_target_apart()
    {
        _accessControl.IsAllowedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var restriction = Substitute.For<IAgentRequestRestriction>();
        restriction.AllowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = new PermissionService(_accessControl, agentRestrictions: [restriction]);

        await service.IsAllowedAsync("Archive", "Car");

        await restriction.Received(1).AllowsAsync("Archive", "Car", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The DI shape: AddSpark registers no restriction, so an application without the MCP package resolves
    /// an empty set and keeps its behaviour; one registered by a package is picked up per request.
    /// </summary>
    [Fact]
    public async Task A_restriction_registered_in_DI_is_applied()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _accessControl);
        services.AddScoped<IAgentRequestRestriction>(_ => new Allowing("Read/Car"));
        services.AddScoped<IPermissionService, PermissionService>();
        _accessControl.IsAllowedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPermissionService>();

        (await service.IsAllowedAsync("Read", "Car")).Should().BeTrue();
        (await service.IsAllowedAsync("Read", "Person")).Should().BeFalse();
    }
}
