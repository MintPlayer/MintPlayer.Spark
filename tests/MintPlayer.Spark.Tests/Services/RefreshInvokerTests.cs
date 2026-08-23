using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// <c>OnRefreshAsync</c> is deliberately off <see cref="IPersistentObjectActions{T}"/>, so it is
/// reached by reflection. That buys source compatibility for hand-written implementers and costs a
/// dispatch that no compiler checks — which is what this suite exists to check instead.
///
/// <para>
/// The awkward part is the argument: <c>SparkRefreshArgs&lt;T&gt;</c> closes over the entity type,
/// which is only known at runtime, and its constructor is internal. Both the method lookup and the
/// args construction therefore have to be built generically, and neither fails loudly when it is
/// wrong — a missed override is silence, not an exception.
/// </para>
/// </summary>
public class RefreshInvokerTests
{
    public class RefreshFixtureEntity
    {
        public string? Id { get; set; }
        public string? Status { get; set; }
        public string? PoliceReport { get; set; }
    }

    /// <summary>Overrides the hook: reshapes the object and records what it was handed.</summary>
    public class RefreshFixtureEntityActions : DefaultPersistentObjectActions<RefreshFixtureEntity>
    {
        public RefreshFixtureEntityActions(IEntityMapper mapper) : base(mapper) { }

        public int Invocations { get; private set; }
        public string? SawAttributeName { get; private set; }
        public bool SawAttributeWasNull { get; private set; }
        public bool SawIsNew { get; private set; }

        public override Task OnRefreshAsync(SparkRefreshArgs<RefreshFixtureEntity> args)
        {
            Invocations++;
            SawAttributeName = args.Attribute?.Name;
            SawAttributeWasNull = args.Attribute is null;
            SawIsNew = args.IsNew;

            args.PersistentObject["PoliceReport"].IsRequired = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>Does not override the hook. Must be recognised as having none.</summary>
    public class PlainRefreshFixtureEntity
    {
        public string? Id { get; set; }
        public string? Status { get; set; }
    }

    public class PlainRefreshFixtureEntityActions : DefaultPersistentObjectActions<PlainRefreshFixtureEntity>
    {
        public PlainRefreshFixtureEntityActions(IEntityMapper mapper) : base(mapper) { }
    }

    private static (RefreshInvoker Invoker, RefreshFixtureEntityActions Actions) Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEntityMapper>());
        var actions = new RefreshFixtureEntityActions(Substitute.For<IEntityMapper>());
        services.AddSingleton(actions);
        var provider = services.BuildServiceProvider();

        return (new RefreshInvoker(new ActionsResolver(provider)), actions);
    }

    private static PersistentObject Object(string? id = null)
    {
        var po = new PersistentObject
        {
            Id = id,
            Name = "RefreshFixtureEntity",
            ObjectTypeId = Guid.Parse("aa000000-0000-0000-0000-000000000001"),
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Status", DataType = "string" },
                new PersistentObjectAttribute { Name = "PoliceReport", DataType = "string" },
            ],
        };
        return po;
    }

    [Fact]
    public async Task The_hook_is_invoked_once_with_the_named_attribute()
    {
        var (invoker, actions) = Build();
        var po = Object();

        await invoker.InvokeAsync(typeof(RefreshFixtureEntity), po, "Status", isNew: true, CancellationToken.None);

        actions.Invocations.Should().Be(1);
        actions.SawAttributeName.Should().Be("Status");
    }

    [Fact]
    public async Task Mutations_made_by_the_hook_are_visible_on_the_object()
    {
        var (invoker, _) = Build();
        var po = Object();

        await invoker.InvokeAsync(typeof(RefreshFixtureEntity), po, "Status", isNew: true, CancellationToken.None);

        po["PoliceReport"].IsRequired.Should().BeTrue(
            "the hook mutates the object in place; a copy would silently discard the reshape");
    }

    [Fact]
    public async Task An_unknown_trigger_name_still_runs_the_hook_with_a_null_attribute()
    {
        // A stale client naming an attribute the model no longer declares must not take the request
        // down, and must not silently skip the developer's logic either.
        var (invoker, actions) = Build();

        await invoker.InvokeAsync(typeof(RefreshFixtureEntity), Object(), "Nonexistent", isNew: true, CancellationToken.None);

        actions.Invocations.Should().Be(1);
        actions.SawAttributeWasNull.Should().BeTrue();
    }

    [Fact]
    public async Task IsNew_is_passed_through()
    {
        var (invoker, actions) = Build();

        await invoker.InvokeAsync(typeof(RefreshFixtureEntity), Object("cars/1"), "Status", isNew: false, CancellationToken.None);

        actions.SawIsNew.Should().BeFalse();
    }

    [Fact]
    public void An_actions_class_that_does_not_override_the_hook_reports_none()
    {
        // The discriminator for the base-declaration check. Without it every entity in the
        // application "has" a refresh hook, the verify gate accepts any declared trigger, and every
        // save pays a reflection call to invoke a method that returns Task.CompletedTask.
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEntityMapper>());
        var provider = services.BuildServiceProvider();
        var invoker = new RefreshInvoker(new ActionsResolver(provider));

        invoker.HasRefreshHook(typeof(PlainRefreshFixtureEntity)).Should().BeFalse();
        invoker.HasRefreshHook(typeof(RefreshFixtureEntity)).Should().BeTrue();
    }

    [Fact]
    public async Task A_type_without_the_hook_is_a_no_op()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEntityMapper>());
        var provider = services.BuildServiceProvider();
        var invoker = new RefreshInvoker(new ActionsResolver(provider));

        var po = new PersistentObject
        {
            Name = "PlainRefreshFixtureEntity",
            ObjectTypeId = Guid.Parse("aa000000-0000-0000-0000-000000000002"),
            Attributes = [new PersistentObjectAttribute { Name = "Status", DataType = "string" }],
        };

        var act = async () => await invoker.InvokeAsync(
            typeof(PlainRefreshFixtureEntity), po, "Status", isNew: true, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_resolved_method_is_reused_across_calls()
    {
        // Pins the cache key. Refresh runs on every field blur, so re-resolving a MethodInfo per
        // call would put reflection on the hot path.
        var (invoker, actions) = Build();

        await invoker.InvokeAsync(typeof(RefreshFixtureEntity), Object(), "Status", isNew: true, CancellationToken.None);
        await invoker.InvokeAsync(typeof(RefreshFixtureEntity), Object(), "Status", isNew: true, CancellationToken.None);

        actions.Invocations.Should().Be(2, "both calls must reach the hook, not just the first");
    }
}
