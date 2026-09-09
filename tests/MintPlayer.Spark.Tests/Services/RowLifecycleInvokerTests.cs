using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// <c>OnNewAsync</c> and <c>OnDeleteRowAsync</c> are reached by reflection, like
/// <c>OnRefreshAsync</c> — and unlike it, they are <b>default interface methods</b>, which adds a
/// failure mode this suite exists to pin down.
/// <para>
/// A DIM means <c>GetMethod</c> can resolve to the interface's own no-op rather than to nothing at
/// all, so "the actions class has no hook" and "the actions class has a hook that does nothing" look
/// alike from the outside. Getting that distinction wrong is silent in both directions: treat the
/// no-op as a hook and every click pays a reflection dispatch to accomplish nothing; treat a real
/// override as absent and the developer's business logic simply never runs.
/// </para>
/// </summary>
public class RowLifecycleInvokerTests
{
    public class LifecycleFixtureRow
    {
        public string? Id { get; set; }
        public string? Description { get; set; }
        public bool IsLocked { get; set; }
    }

    /// <summary>Overrides both hooks, and records exactly what each was handed.</summary>
    public class LifecycleFixtureRowActions : DefaultPersistentObjectActions<LifecycleFixtureRow>
    {
        public LifecycleFixtureRowActions(IEntityMapper mapper) : base(mapper) { }

        public int NewInvocations { get; private set; }
        public PersistentObject? NewSawParent { get; private set; }
        public PersistentObject? NewSawAsDetailParent { get; private set; }
        public string? NewSawAsDetailAttribute { get; private set; }
        public IReadOnlyDictionary<string, string>? NewSawParameters { get; private set; }

        public int DeleteInvocations { get; private set; }
        public string? DeleteSawRowKey { get; private set; }
        public string? DeleteSawAsDetailAttribute { get; private set; }
        public PersistentObject? DeleteSawRow { get; private set; }
        public bool RefuseDelete { get; set; }

        public override Task OnNewAsync(SparkNewArgs<LifecycleFixtureRow> args)
        {
            NewInvocations++;
            NewSawParent = args.Parent;
            NewSawAsDetailParent = args.AsDetailParent;
            NewSawAsDetailAttribute = args.AsDetailAttribute;
            NewSawParameters = args.Parameters;

            args.PersistentObject["Description"].SetOriginalValue("defaulted by the hook");
            return Task.CompletedTask;
        }

        public override Task OnDeleteRowAsync(SparkDeleteRowArgs<LifecycleFixtureRow> args)
        {
            DeleteInvocations++;
            DeleteSawRowKey = args.RowKey;
            DeleteSawAsDetailAttribute = args.AsDetailAttribute;
            DeleteSawRow = args.Row;

            if (RefuseDelete)
                throw new SparkValidationException("This row is locked.", args.AsDetailAttribute);

            return Task.CompletedTask;
        }
    }

    /// <summary>Overrides neither. Must be recognised as having no hook of either kind.</summary>
    public class PlainLifecycleFixtureRow
    {
        public string? Id { get; set; }
    }

    public class PlainLifecycleFixtureRowActions : DefaultPersistentObjectActions<PlainLifecycleFixtureRow>
    {
        public PlainLifecycleFixtureRowActions(IEntityMapper mapper) : base(mapper) { }
    }

    private static (NewInvoker New, DeleteRowInvoker Delete, LifecycleFixtureRowActions Actions) Build()
    {
        var services = new ServiceCollection();
        var actions = new LifecycleFixtureRowActions(Substitute.For<IEntityMapper>());
        services.AddSingleton(actions);
        services.AddSingleton(new PlainLifecycleFixtureRowActions(Substitute.For<IEntityMapper>()));
        var provider = services.BuildServiceProvider();
        var resolver = new ActionsResolver(provider);

        return (new NewInvoker(resolver), new DeleteRowInvoker(resolver), actions);
    }

    private static PersistentObject Row(string? id = null, bool locked = false) => new()
    {
        Id = id,
        Name = "LifecycleFixtureRow",
        ObjectTypeId = Guid.Parse("bb000000-0000-0000-0000-000000000001"),
        Attributes =
        [
            new PersistentObjectAttribute { Name = "Description" },
            new PersistentObjectAttribute { Name = "IsLocked", DataType = "boolean", Value = locked },
        ],
    };

    // ---- New ----------------------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_RunsTheOverriddenConstructionHook()
    {
        var (newInvoker, _, actions) = Build();
        var po = Row();

        await newInvoker.InvokeAsync(
            typeof(LifecycleFixtureRow), po, parent: null, asDetailParent: null,
            asDetailAttribute: null, parameters: null, CancellationToken.None);

        actions.NewInvocations.Should().Be(1);
        po["Description"].Value.Should().Be("defaulted by the hook");
    }

    [Fact]
    public async Task InvokeAsync_DefaultsAppliedByAConstructionHook_DoNotMakeTheObjectDirty()
    {
        var (newInvoker, _, _) = Build();
        var po = Row();

        await newInvoker.InvokeAsync(
            typeof(LifecycleFixtureRow), po, parent: null, asDetailParent: null,
            asDetailAttribute: null, parameters: null, CancellationToken.None);

        // The whole reason SetOriginalValue exists. A hook that defaults three fields must not make
        // a form the user has not touched report unsaved changes.
        po["Description"].IsValueChanged.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_PassesTheAsDetailContextThrough()
    {
        var (newInvoker, _, actions) = Build();
        var parent = Row("Parents/1");

        await newInvoker.InvokeAsync(
            typeof(LifecycleFixtureRow), Row(), parent, asDetailParent: parent,
            asDetailAttribute: "Entries",
            parameters: new Dictionary<string, string> { ["variant"] = "warranty" },
            CancellationToken.None);

        actions.NewSawParent.Should().BeSameAs(parent);
        actions.NewSawAsDetailParent.Should().BeSameAs(parent);
        actions.NewSawAsDetailAttribute.Should().Be("Entries");
        actions.NewSawParameters!["variant"].Should().Be("warranty");
    }

    [Fact]
    public async Task InvokeAsync_NormalisesNullParametersToAnEmptyDictionary()
    {
        var (newInvoker, _, actions) = Build();

        await newInvoker.InvokeAsync(
            typeof(LifecycleFixtureRow), Row(), null, null, null, parameters: null, CancellationToken.None);

        // Documented as never null so a hook reading a parameter need not null-check first.
        actions.NewSawParameters.Should().NotBeNull();
        actions.NewSawParameters.Should().BeEmpty();
    }

    [Fact]
    public void HasNewHook_IsFalse_WhenTheActionsClassOnlyInheritsTheDefault()
    {
        var (newInvoker, _, _) = Build();

        // The DIM case: OnNewAsync exists on every actions class whether or not anyone wrote one.
        newInvoker.HasNewHook(typeof(PlainLifecycleFixtureRow)).Should().BeFalse();
        newInvoker.HasNewHook(typeof(LifecycleFixtureRow)).Should().BeTrue();
    }

    // ---- Delete -------------------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_RunsTheOverriddenRemovalHook_WithTheStoredRow()
    {
        var (_, deleteInvoker, actions) = Build();
        var row = Row("row-key-1");
        var parent = Row("Parents/1");

        await deleteInvoker.InvokeAsync(
            typeof(LifecycleFixtureRow), row, parent, "Entries", "row-key-1", null, CancellationToken.None);

        actions.DeleteInvocations.Should().Be(1);
        actions.DeleteSawRowKey.Should().Be("row-key-1");
        actions.DeleteSawAsDetailAttribute.Should().Be("Entries");
        actions.DeleteSawRow.Should().BeSameAs(row,
            "the hook must judge the stored row, not a copy the caller supplied");
    }

    [Fact]
    public async Task InvokeAsync_LetsARefusalPropagate()
    {
        var (_, deleteInvoker, actions) = Build();
        actions.RefuseDelete = true;

        var refusal = async () => await deleteInvoker.InvokeAsync(
            typeof(LifecycleFixtureRow), Row("row-key-1"), Row("Parents/1"),
            "Entries", "row-key-1", null, CancellationToken.None);

        // Swallowing it here would turn "you may not remove this" into a silent success — the row
        // would disappear from the grid and the user would never learn why.
        await refusal.Should().ThrowAsync<SparkValidationException>()
            .WithMessage("This row is locked.");
    }

    [Fact]
    public void HasDeleteRowHook_IsFalse_WhenTheActionsClassOnlyInheritsTheDefault()
    {
        var (_, deleteInvoker, _) = Build();

        deleteInvoker.HasDeleteRowHook(typeof(PlainLifecycleFixtureRow)).Should().BeFalse();
        deleteInvoker.HasDeleteRowHook(typeof(LifecycleFixtureRow)).Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_IsANoOp_WhenTheActionsClassOverridesNeitherHook()
    {
        var (newInvoker, deleteInvoker, _) = Build();
        var po = new PersistentObject
        {
            Name = "PlainLifecycleFixtureRow",
            ObjectTypeId = Guid.Parse("bb000000-0000-0000-0000-000000000002"),
            Attributes = [],
        };

        // Neither should throw, and neither should try to invoke the interface's own no-op.
        await newInvoker.InvokeAsync(typeof(PlainLifecycleFixtureRow), po, null, null, null, null, CancellationToken.None);
        await deleteInvoker.InvokeAsync(typeof(PlainLifecycleFixtureRow), po, po, "Entries", "k", null, CancellationToken.None);
    }
}
