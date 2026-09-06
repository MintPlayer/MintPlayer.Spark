using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// CustomActionResolver discovers <see cref="ICustomAction"/> implementations across loaded
/// assemblies and resolves them by friendly name. The discovery is cached statically per
/// process — these tests pin the contract: the suffix-stripping naming rule, the DI-first /
/// ActivatorUtilities-fallback construction order, case-insensitive lookup, and graceful
/// failure modes (null on miss, null on ctor exception). A regression here silently breaks
/// the <c>/spark/actions/{type}/{name}</c> endpoint.
/// </summary>
public class CustomActionResolverTests
{
    private static CustomActionResolver CreateResolver(IServiceProvider provider) =>
        new(provider, NullLogger<CustomActionResolver>.Instance);

    private static IServiceProvider EmptyProvider() => new ServiceCollection().BuildServiceProvider();

    [Fact]
    public void Resolve_strips_optional_Action_suffix_to_derive_friendly_name()
    {
        // CustomActionResolverFixtureEchoAction → friendly name "CustomActionResolverFixtureEcho"
        var resolver = CreateResolver(EmptyProvider());

        var resolved = resolver.Resolve("CustomActionResolverFixtureEcho");

        resolved.Should().BeOfType<CustomActionResolverFixtureEchoAction>();
    }

    [Fact]
    public void Resolve_uses_full_class_name_when_it_does_not_end_with_Action()
    {
        var resolver = CreateResolver(EmptyProvider());

        var resolved = resolver.Resolve("CustomActionResolverFixtureLogout");

        resolved.Should().BeOfType<CustomActionResolverFixtureLogout>();
    }

    [Fact]
    public void Resolve_is_case_insensitive()
    {
        var resolver = CreateResolver(EmptyProvider());

        resolver.Resolve("customactionresolverfixtureecho").Should().NotBeNull();
        resolver.Resolve("CUSTOMACTIONRESOLVERFIXTUREECHO").Should().NotBeNull();
    }

    [Fact]
    public void Resolve_returns_null_for_unknown_action_name()
    {
        var resolver = CreateResolver(EmptyProvider());

        resolver.Resolve("NoSuchActionExists").Should().BeNull();
    }

    [Fact]
    public void Resolve_returns_DI_registered_instance_when_type_is_registered()
    {
        var preBuilt = new CustomActionResolverFixtureEchoAction();
        var services = new ServiceCollection();
        services.AddSingleton(preBuilt);
        var provider = services.BuildServiceProvider();

        var resolver = CreateResolver(provider);

        var resolved = resolver.Resolve("CustomActionResolverFixtureEcho");

        resolved.Should().BeSameAs(preBuilt);
    }

    [Fact]
    public void Resolve_falls_back_to_ActivatorUtilities_when_type_is_not_in_DI()
    {
        // Type is not registered — resolver must construct via ActivatorUtilities. The fixture
        // has a parameterless ctor so no DI is required for construction itself.
        var resolver = CreateResolver(EmptyProvider());

        var first = resolver.Resolve("CustomActionResolverFixtureEcho");
        var second = resolver.Resolve("CustomActionResolverFixtureEcho");

        // Each call yields a fresh instance (no DI lifetime to dedupe through).
        first.Should().NotBeSameAs(second);
    }

    /// <summary>
    /// Construction failure THROWS; it does not return null.
    /// <para>
    /// This test previously pinned the opposite, and the opposite was a trap. Null means "no such
    /// action" to the only caller that constructs one (<c>ExecuteCustomAction</c>), so a dependency
    /// the container could not satisfy was reported to the client as a 404 saying the action does
    /// not exist. That sends whoever is debugging to the action name and to customActions.json,
    /// neither of which is wrong, while the actual cause -- a missing registration -- was written
    /// only to the log, which nobody reading a 404 has any reason to open.
    /// </para>
    /// <para>
    /// Throwing is safe here precisely because construction has exactly one caller. The listing
    /// endpoint uses <see cref="ICustomActionResolver.GetRegisteredActionNames"/> and never
    /// constructs anything, so one unconstructable action cannot take the whole action bar down
    /// with it -- only the single request that was already going to fail.
    /// </para>
    /// </summary>
    [Fact]
    public void Resolve_throws_with_an_actionable_message_when_construction_throws()
    {
        var resolver = CreateResolver(EmptyProvider());

        var act = () => resolver.Resolve("CustomActionResolverFixtureBoom");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*could not be constructed*");
    }

    [Fact]
    public void GetRegisteredActionNames_returns_discovered_friendly_names()
    {
        var resolver = CreateResolver(EmptyProvider());

        var names = resolver.GetRegisteredActionNames();

        names.Should().Contain("CustomActionResolverFixtureEcho");
        names.Should().Contain("CustomActionResolverFixtureLogout");
        names.Should().Contain("CustomActionResolverFixtureBoom");
    }
}

// Top-level fixtures so DiscoverActionTypes finds them via assembly.GetTypes().

public class CustomActionResolverFixtureEchoAction : ICustomAction
{
    public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public class CustomActionResolverFixtureLogout : ICustomAction
{
    public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public class CustomActionResolverFixtureBoomAction : ICustomAction
{
    public CustomActionResolverFixtureBoomAction()
        => throw new InvalidOperationException("intentional ctor failure");

    public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
