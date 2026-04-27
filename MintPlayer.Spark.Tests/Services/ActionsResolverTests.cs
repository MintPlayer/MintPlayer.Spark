using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// ActionsResolver decides which <see cref="IPersistentObjectActions{T}"/> instance handles a
/// given entity. The 3-tier resolution (entity-specific class → registered interface →
/// DefaultPersistentObjectActions) is the framework's hot-path dispatcher; a regression
/// silently routes operations to the wrong handler.
/// </summary>
public class ActionsResolverTests
{
    // --- Fixtures (top-level types so the assembly scan in FindActionsType finds them) ---

    /// <summary>Tier 1 fixture: actions class named after entity. Must end with "Actions".</summary>
    public class ResolverFixtureAActions : DefaultPersistentObjectActions<ResolverFixtureA>
    {
        public ResolverFixtureAActions(IEntityMapper mapper) : base(mapper) { }
    }

    /// <summary>Tier 2 fixture: no entity-specific Actions class; impl is registered as IPersistentObjectActions&lt;T&gt;.</summary>
    public class CustomResolverFixtureBHandler : DefaultPersistentObjectActions<ResolverFixtureB>
    {
        public CustomResolverFixtureBHandler(IEntityMapper mapper) : base(mapper) { }
    }

    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEntityMapper>());
        return services;
    }

    [Fact]
    public void Resolve_returns_entity_specific_actions_class_when_registered_in_DI()
    {
        var services = BaseServices();
        var registeredInstance = new ResolverFixtureAActions(Substitute.For<IEntityMapper>());
        services.AddSingleton(registeredInstance);
        var provider = services.BuildServiceProvider();

        var resolver = new ActionsResolver(provider);

        var resolved = resolver.Resolve<ResolverFixtureA>();

        resolved.Should().BeSameAs(registeredInstance);
    }

    [Fact]
    public void Resolve_creates_entity_specific_actions_via_ActivatorUtilities_when_not_in_DI()
    {
        var services = BaseServices();
        // Note: ResolverFixtureAActions is NOT registered. The resolver should still find the
        // type via the assembly scan and construct it through ActivatorUtilities, pulling
        // IEntityMapper from the service provider.
        var provider = services.BuildServiceProvider();

        var resolver = new ActionsResolver(provider);

        var resolved = resolver.Resolve<ResolverFixtureA>();

        resolved.Should().BeOfType<ResolverFixtureAActions>();
    }

    [Fact]
    public void Resolve_falls_back_to_registered_IPersistentObjectActions_when_no_entity_specific_class_exists()
    {
        // ResolverFixtureB has NO "ResolverFixtureBActions" class; the impl is named differently
        // (CustomResolverFixtureBHandler) so the assembly scan can't tier-1 match.
        var services = BaseServices();
        var registered = new CustomResolverFixtureBHandler(Substitute.For<IEntityMapper>());
        services.AddSingleton<IPersistentObjectActions<ResolverFixtureB>>(registered);
        var provider = services.BuildServiceProvider();

        var resolver = new ActionsResolver(provider);

        var resolved = resolver.Resolve<ResolverFixtureB>();

        resolved.Should().BeSameAs(registered);
    }

    [Fact]
    public void Resolve_falls_back_to_DefaultPersistentObjectActions_when_nothing_else_registered()
    {
        // ResolverFixtureC has no entity-specific Actions class anywhere AND no registration.
        var services = BaseServices();
        var provider = services.BuildServiceProvider();

        var resolver = new ActionsResolver(provider);

        var resolved = resolver.Resolve<ResolverFixtureC>();

        resolved.Should().BeOfType<DefaultPersistentObjectActions<ResolverFixtureC>>();
    }

    [Fact]
    public void ResolveForType_delegates_to_the_generic_Resolve_via_reflection()
    {
        var services = BaseServices();
        var provider = services.BuildServiceProvider();

        var resolver = new ActionsResolver(provider);

        var resolved = resolver.ResolveForType(typeof(ResolverFixtureA));

        resolved.Should().BeOfType<ResolverFixtureAActions>();
    }

    [Fact]
    public void Resolve_picks_DI_registration_over_ActivatorUtilities_when_both_paths_succeed()
    {
        // Pin the precedence: a singleton registration wins over a fresh ActivatorUtilities
        // instance. Otherwise the framework would silently re-create the actions class on
        // every request even when the app explicitly registered a single instance.
        var services = BaseServices();
        var registeredInstance = new ResolverFixtureAActions(Substitute.For<IEntityMapper>());
        services.AddSingleton(registeredInstance);
        var provider = services.BuildServiceProvider();

        var resolver = new ActionsResolver(provider);

        var first = resolver.Resolve<ResolverFixtureA>();
        var second = resolver.Resolve<ResolverFixtureA>();

        first.Should().BeSameAs(registeredInstance);
        second.Should().BeSameAs(registeredInstance);
    }
}

// Top-level fixtures so FindActionsType can match by Type.Name.
public class ResolverFixtureA { }
public class ResolverFixtureB { }
public class ResolverFixtureC { }
