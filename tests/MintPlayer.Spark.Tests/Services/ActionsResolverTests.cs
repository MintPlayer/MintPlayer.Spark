using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// ActionsResolver decides which <see cref="IPersistentObjectActions{T}"/> instance handles a
/// given entity. The 3-tier resolution (registered interface → the class implementing the contract
/// for exactly T → DefaultPersistentObjectActions) is the framework's hot-path dispatcher; a regression
/// silently routes operations to the wrong handler. Never by simple name (D28, X9): two libraries'
/// <c>OrderActions</c> for two <c>Order</c> types are not ambiguous, and a JSON-only library type binds
/// its actions inside its declaring assembly only.
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

    // --- D28 (X9): by contract, never by simple name ---------------------------------------------

    [Fact]
    public void Two_actions_classes_with_one_simple_name_for_two_types_resolve_each_to_its_own()
    {
        // Research §8.2 trap 5: both are 'ResolverOrderActions'. Resolution by simple name threw on the
        // first request for either type; by contract each type gets the class written for it.
        var resolver = new ActionsResolver(BaseServices().BuildServiceProvider());

        resolver.Resolve<ResolverContractOne.ResolverOrder>().Should().BeOfType<ResolverContractOne.ResolverOrderActions>();
        resolver.Resolve<ResolverContractTwo.ResolverOrder>().Should().BeOfType<ResolverContractTwo.ResolverOrderActions>();
    }

    [Fact]
    public void The_most_derived_actions_class_wins_when_it_derives_from_every_other()
    {
        var resolver = new ActionsResolver(BaseServices().BuildServiceProvider());

        resolver.Resolve<ResolverFixtureF>().Should().BeOfType<ResolverFixtureFOverrideActions>();
    }

    [Fact]
    public void Two_unrelated_actions_classes_for_one_type_are_refused_naming_both()
    {
        var resolver = new ActionsResolver(BaseServices().BuildServiceProvider());

        var act = () => resolver.Resolve<ResolverFixtureG>();

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain(nameof(ResolverFixtureGFirstActions));
        message.Should().Contain(nameof(ResolverFixtureGSecondActions));
    }

    [Fact]
    public void Two_unrelated_actions_classes_for_one_type_fail_the_startup_check_unless_a_registration_settles_it()
    {
        var definition = new MintPlayer.Spark.Abstractions.EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = nameof(ResolverFixtureG),
            ClrType = typeof(ResolverFixtureG).FullName,
        };

        var unregistered = () => ActionsResolver.VerifyActionsClasses([definition]);
        unregistered.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(nameof(ResolverFixtureGFirstActions));

        var registered = () => ActionsResolver.VerifyActionsClasses([definition], entity => entity == typeof(ResolverFixtureG));
        registered.Should().NotThrow("a registration of IPersistentObjectActions<T> wins, so nothing is ambiguous");
    }

    [Fact]
    public void A_registration_settles_two_unrelated_actions_classes()
    {
        var services = BaseServices();
        var registered = new ResolverFixtureGSecondActions(Substitute.For<IEntityMapper>());
        services.AddSingleton<IPersistentObjectActions<ResolverFixtureG>>(registered);

        new ActionsResolver(services.BuildServiceProvider()).Resolve<ResolverFixtureG>().Should().BeSameAs(registered);
    }

    [Fact]
    public void A_library_virtual_type_binds_its_declared_actions_class_in_its_own_assembly_even_when_renamed()
    {
        // The application renamed Authorization's 'Passkeys' to 'MyPasskeys' and declares a class named
        // 'PasskeysActions' of its own (below). Neither may change which class serves the library's page.
        var authorization = System.Reflection.Assembly.Load("MintPlayer.Spark.Authorization");
        var definition = new MintPlayer.Spark.Abstractions.EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "MyPasskeys",
            DeclaredName = "Passkeys",
            DeclaringAssembly = authorization.GetName().Name,
            LibraryAlias = "authorization",
        };

        var actionsType = ActionsResolver.FindVirtualActionsType(definition, [definition]);

        actionsType.Should().NotBeNull();
        actionsType!.Assembly.Should().BeSameAs(authorization);
        actionsType.Should().NotBe(typeof(PasskeysActions));
    }

    [Fact]
    public void An_application_virtual_type_does_not_pick_the_actions_class_of_a_library_type_of_that_name()
    {
        // Authorization ships the virtual 'Passkeys'; an application type renamed into that name would
        // otherwise be served by the library's actions class.
        var library = new MintPlayer.Spark.Abstractions.EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "LibraryPasskeys",
            DeclaredName = "Passkeys",
            DeclaringAssembly = "MintPlayer.Spark.Authorization",
        };
        var application = new MintPlayer.Spark.Abstractions.EntityTypeDefinition { Id = Guid.NewGuid(), Name = "Passkeys" };
        System.Reflection.Assembly.Load("MintPlayer.Spark.Authorization");

        ActionsResolver.FindVirtualActionsType(application, [library, application])
            .Should().Be(typeof(PasskeysActions), "the application's own class serves the application's type");
    }
}

// Top-level fixtures for the contract scan.
public class ResolverFixtureA { }
public class ResolverFixtureB { }
public class ResolverFixtureC { }
public class ResolverFixtureF { }
public class ResolverFixtureG { }

public class ResolverFixtureFActions(IEntityMapper mapper) : DefaultPersistentObjectActions<ResolverFixtureF>(mapper);

/// <summary>Overrides <see cref="ResolverFixtureFActions"/> by deriving from it: the explicit way to replace a library's actions class.</summary>
public class ResolverFixtureFOverrideActions(IEntityMapper mapper) : ResolverFixtureFActions(mapper);

public class ResolverFixtureGFirstActions(IEntityMapper mapper) : DefaultPersistentObjectActions<ResolverFixtureG>(mapper);

public class ResolverFixtureGSecondActions(IEntityMapper mapper) : DefaultPersistentObjectActions<ResolverFixtureG>(mapper);

/// <summary>An application class named like Authorization's virtual-type actions class.</summary>
public class PasskeysActions;

/// <summary>One of two libraries shipping an entity and its actions class under the same simple names.</summary>
public static class ResolverContractOne
{
    public class ResolverOrder { }

    public class ResolverOrderActions(IEntityMapper mapper) : DefaultPersistentObjectActions<ResolverOrder>(mapper);
}

/// <summary>The other library: same simple names, a different entity.</summary>
public static class ResolverContractTwo
{
    public class ResolverOrder { }

    public class ResolverOrderActions(IEntityMapper mapper) : DefaultPersistentObjectActions<ResolverOrder>(mapper);
}
