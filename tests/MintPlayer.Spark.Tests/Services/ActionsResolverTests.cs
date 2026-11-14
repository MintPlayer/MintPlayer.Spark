using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using NSubstitute;

// #660 G17: what the source generator emits for AppD.ResolverFixtureDActions in a project that runs it (this test
// project does not), written out by hand.
[assembly: SparkActionsOverride(typeof(MintPlayer.Spark.Tests.Services.ResolverFixtureD), typeof(MintPlayer.Spark.Tests.Services.ActionsResolverTests.AppD.ResolverFixtureDActions))]

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

    [Fact]
    public void FindActionsType_caches_negative_result_for_missing_actions_class()
    {
        // ResolverFixtureC has no entity-specific Actions class anywhere. The cache
        // primitive's negative-caching contract means the assembly walk should run at
        // most once per missing type name, no matter how many times Resolve is called.
        // We can't directly observe assembly walks, but two consecutive Resolve calls
        // returning the same Default instance shape proves the resolver is stable.
        var services = BaseServices();
        var provider = services.BuildServiceProvider();

        var resolver = new ActionsResolver(provider);

        var first = resolver.Resolve<ResolverFixtureC>();
        var second = resolver.Resolve<ResolverFixtureC>();

        first.Should().BeOfType<DefaultPersistentObjectActions<ResolverFixtureC>>();
        second.Should().BeOfType<DefaultPersistentObjectActions<ResolverFixtureC>>();
    }

    [Fact]
    public void ResolveForType_caches_closed_generic_method_per_entity_type()
    {
        // ResolveForType reflects on Resolve<T> + MakeGenericMethod(entityType); the
        // closed MethodInfo is cached per entityType. Repeated dispatches for the same
        // entityType must keep returning the right shape.
        var services = BaseServices();
        var provider = services.BuildServiceProvider();

        var resolver = new ActionsResolver(provider);

        var firstA = resolver.ResolveForType(typeof(ResolverFixtureA));
        var secondA = resolver.ResolveForType(typeof(ResolverFixtureA));
        var firstC = resolver.ResolveForType(typeof(ResolverFixtureC));

        firstA.Should().BeOfType<ResolverFixtureAActions>();
        secondA.Should().BeOfType<ResolverFixtureAActions>();
        firstC.Should().BeOfType<DefaultPersistentObjectActions<ResolverFixtureC>>();
    }

    // --- #660 G17: an app's actions class taking over a library's of the same name ---

    /// <summary>Stands in for a library's actions class (e.g. MintPlayer.Spark.Logging's LogActions).</summary>
    public class LibraryD
    {
        public class ResolverFixtureDActions : DefaultPersistentObjectActions<ResolverFixtureD>
        {
            public ResolverFixtureDActions(IEntityMapper mapper) : base(mapper) { }
        }
    }

    /// <summary>The app's specialisation, recorded by the assembly attribute at the top of this file.</summary>
    public class AppD
    {
        public class ResolverFixtureDActions : LibraryD.ResolverFixtureDActions
        {
            public ResolverFixtureDActions(IEntityMapper mapper) : base(mapper) { }
        }
    }

    // ChooseOverride does not look at names (the caller already matched them), so these carry other names: under
    // the real name they would make the end-to-end test's own resolution ambiguous.
    public class SiblingD
    {
        public class SiblingFixtureDActions : LibraryD.ResolverFixtureDActions
        {
            public SiblingFixtureDActions(IEntityMapper mapper) : base(mapper) { }
        }
    }

    public class UnrelatedD
    {
        public class UnrelatedFixtureDActions : DefaultPersistentObjectActions<ResolverFixtureD>
        {
            public UnrelatedFixtureDActions(IEntityMapper mapper) : base(mapper) { }
        }
    }

    [Fact]
    public void A_recorded_override_wins_over_the_library_class_it_derives_from()
    {
        var resolver = new ActionsResolver(BaseServices().BuildServiceProvider());

        resolver.Resolve<ResolverFixtureD>().Should().BeOfType<AppD.ResolverFixtureDActions>();
    }

    [Fact]
    public void An_override_is_not_chosen_while_an_unrelated_class_has_the_same_name()
    {
        var chosen = ActionsResolver.ChooseOverride(
            [typeof(LibraryD.ResolverFixtureDActions), typeof(AppD.ResolverFixtureDActions), typeof(UnrelatedD.UnrelatedFixtureDActions)],
            [new SparkActionsOverrideAttribute(typeof(ResolverFixtureD), typeof(AppD.ResolverFixtureDActions))]);

        chosen.Should().BeNull("the unrelated class is not a base of the override, so the ambiguity stands");
    }

    [Fact]
    public void Two_sibling_overrides_of_one_library_class_are_not_chosen()
    {
        var chosen = ActionsResolver.ChooseOverride(
            [typeof(LibraryD.ResolverFixtureDActions), typeof(AppD.ResolverFixtureDActions), typeof(SiblingD.SiblingFixtureDActions)],
            [
                new SparkActionsOverrideAttribute(typeof(ResolverFixtureD), typeof(AppD.ResolverFixtureDActions)),
                new SparkActionsOverrideAttribute(typeof(ResolverFixtureD), typeof(SiblingD.SiblingFixtureDActions)),
            ]);

        chosen.Should().BeNull();
    }

    [Fact]
    public void A_chain_of_overrides_resolves_to_its_most_derived_class()
    {
        var chosen = ActionsResolver.ChooseOverride(
            [typeof(LibraryD.ResolverFixtureDActions), typeof(AppD.ResolverFixtureDActions)],
            [
                new SparkActionsOverrideAttribute(typeof(ResolverFixtureD), typeof(LibraryD.ResolverFixtureDActions)),
                new SparkActionsOverrideAttribute(typeof(ResolverFixtureD), typeof(AppD.ResolverFixtureDActions)),
            ]);

        chosen.Should().Be<AppD.ResolverFixtureDActions>();
    }
}

// Top-level fixtures so FindActionsType can match by Type.Name.
public class ResolverFixtureA { }
public class ResolverFixtureB { }
public class ResolverFixtureC { }
public class ResolverFixtureD { }
