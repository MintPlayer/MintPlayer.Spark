using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Pins the per-request accumulator that action endpoints drain into the response envelope.
/// Each public method appends one or more <see cref="ClientOperation"/> instances; a
/// regression here silently drops side-effects (notifications, navigations, refresh hints)
/// from the envelope. The Id-bearing overloads guard against operations that point at
/// unsaved POs — the frontend can't act on them.
/// </summary>
public class ClientAccessorTests
{
    private static readonly Guid CarTypeId = Guid.Parse("c0c0c0c0-0000-0000-0000-000000000000");

    private static PersistentObject NewPo(string? id = "cars/1") => new()
    {
        Id = id,
        Name = "Car",
        ObjectTypeId = CarTypeId,
    };

    [Fact]
    public void Operations_is_empty_initially()
    {
        var accessor = new ClientAccessor();

        accessor.Operations.Should().BeEmpty();
    }

    #region Navigate

    [Fact]
    public void Navigate_with_PersistentObject_appends_NavigateOperation_with_type_and_id()
    {
        var accessor = new ClientAccessor();

        accessor.Navigate(NewPo("cars/42"));

        var op = accessor.Operations.Should().ContainSingle().Which.Should().BeOfType<NavigateOperation>().Which;
        op.ObjectTypeId.Should().Be(CarTypeId);
        op.Id.Should().Be("cars/42");
        op.RouteName.Should().BeNull();
    }

    [Fact]
    public void Navigate_with_null_PersistentObject_throws()
    {
        var accessor = new ClientAccessor();

        var act = () => accessor.Navigate((PersistentObject)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Navigate_with_unsaved_PersistentObject_throws_with_a_helpful_message()
    {
        var accessor = new ClientAccessor();
        var unsaved = NewPo(id: null);

        var act = () => accessor.Navigate(unsaved);

        act.Should().Throw<InvalidOperationException>().WithMessage("*hasn't been saved*");
    }

    [Fact]
    public void Navigate_by_type_and_id_appends_NavigateOperation()
    {
        var accessor = new ClientAccessor();

        accessor.Navigate(CarTypeId, "cars/99");

        var op = accessor.Operations.Should().ContainSingle().Which.Should().BeOfType<NavigateOperation>().Which;
        op.ObjectTypeId.Should().Be(CarTypeId);
        op.Id.Should().Be("cars/99");
    }

    [Fact]
    public void Navigate_by_route_name_appends_NavigateOperation_with_RouteName_only()
    {
        var accessor = new ClientAccessor();

        accessor.Navigate("home");

        var op = accessor.Operations.Should().ContainSingle().Which.Should().BeOfType<NavigateOperation>().Which;
        op.RouteName.Should().Be("home");
        op.ObjectTypeId.Should().BeNull();
        op.Id.Should().BeNull();
    }

    #endregion

    #region Notify

    [Fact]
    public void Notify_appends_NotifyOperation_with_message_kind_and_duration_in_ms()
    {
        var accessor = new ClientAccessor();

        accessor.Notify("hi", NotificationKind.Warning, TimeSpan.FromSeconds(3));

        var op = accessor.Operations.Should().ContainSingle().Which.Should().BeOfType<NotifyOperation>().Which;
        op.Message.Should().Be("hi");
        op.Kind.Should().Be(NotificationKind.Warning);
        op.DurationMs.Should().Be(3000);
    }

    [Fact]
    public void Notify_defaults_kind_to_Info_and_duration_to_null()
    {
        var accessor = new ClientAccessor();

        accessor.Notify("hi");

        var op = (NotifyOperation)accessor.Operations[0];
        op.Kind.Should().Be(NotificationKind.Info);
        op.DurationMs.Should().BeNull();
    }

    #endregion

    #region RefreshAttribute / RefreshQuery

    [Fact]
    public void RefreshAttribute_by_type_and_id_appends_with_the_supplied_value()
    {
        var accessor = new ClientAccessor();

        accessor.RefreshAttribute(CarTypeId, "cars/1", "Color", "Red");

        var op = accessor.Operations.Should().ContainSingle().Which.Should().BeOfType<RefreshAttributeOperation>().Which;
        op.ObjectTypeId.Should().Be(CarTypeId);
        op.Id.Should().Be("cars/1");
        op.AttributeName.Should().Be("Color");
        op.Value.Should().Be("Red");
    }

    [Fact]
    public void RefreshAttribute_with_unsaved_PersistentObject_throws()
    {
        var accessor = new ClientAccessor();

        var act = () => accessor.RefreshAttribute(NewPo(id: null), "Color");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RefreshAttribute_with_null_PersistentObject_throws()
    {
        var accessor = new ClientAccessor();

        var act = () => accessor.RefreshAttribute((PersistentObject)null!, "Color");

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RefreshQuery_appends_RefreshQueryOperation_with_query_id()
    {
        var accessor = new ClientAccessor();

        accessor.RefreshQuery("queries/active-cars");

        accessor.Operations.Should().ContainSingle().Which.Should().BeOfType<RefreshQueryOperation>()
            .Which.QueryId.Should().Be("queries/active-cars");
    }

    #endregion

    #region DisableActions — one operation per name × target kind

    [Fact]
    public void DisableActionsOn_PersistentObject_emits_one_op_per_name_with_PO_target()
    {
        var accessor = new ClientAccessor();

        accessor.DisableActionsOn(NewPo("cars/1"), "Edit", "Delete");

        accessor.Operations.Should().HaveCount(2);
        accessor.Operations.Should().AllBeOfType<DisableActionOperation>();
        accessor.Operations.Cast<DisableActionOperation>().Select(o => o.ActionName)
            .Should().Equal("Edit", "Delete");
        accessor.Operations.Cast<DisableActionOperation>().Should().AllSatisfy(o =>
        {
            var target = o.Target.Should().BeOfType<PersistentObjectDisableTarget>().Which;
            target.ObjectTypeId.Should().Be(CarTypeId);
            target.Id.Should().Be("cars/1");
        });
    }

    [Fact]
    public void DisableActionsOn_unsaved_PersistentObject_throws()
    {
        var accessor = new ClientAccessor();

        var act = () => accessor.DisableActionsOn(NewPo(id: null), "Edit");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void DisableActionsOn_by_type_and_id_emits_PO_disable_target()
    {
        var accessor = new ClientAccessor();

        accessor.DisableActionsOn(CarTypeId, "cars/2", "Edit");

        var op = (DisableActionOperation)accessor.Operations.Single();
        op.ActionName.Should().Be("Edit");
        op.Target.Should().BeOfType<PersistentObjectDisableTarget>()
            .Which.Id.Should().Be("cars/2");
    }

    [Fact]
    public void DisableQueryActions_emits_QueryDisableTarget()
    {
        var accessor = new ClientAccessor();

        accessor.DisableQueryActions("queries/all-cars", "Export");

        var op = (DisableActionOperation)accessor.Operations.Single();
        op.ActionName.Should().Be("Export");
        op.Target.Should().BeOfType<QueryDisableTarget>()
            .Which.QueryId.Should().Be("queries/all-cars");
    }

    [Fact]
    public void DisableActions_emits_CurrentResponseDisableTarget()
    {
        var accessor = new ClientAccessor();

        accessor.DisableActions("Edit");

        ((DisableActionOperation)accessor.Operations.Single()).Target
            .Should().BeOfType<CurrentResponseDisableTarget>();
    }

    [Fact]
    public void DisableActionsForSession_emits_SessionDisableTarget()
    {
        var accessor = new ClientAccessor();

        accessor.DisableActionsForSession("Edit");

        ((DisableActionOperation)accessor.Operations.Single()).Target
            .Should().BeOfType<SessionDisableTarget>();
    }

    [Fact]
    public void DisableActions_with_no_names_emits_nothing()
    {
        var accessor = new ClientAccessor();

        accessor.DisableActions();

        accessor.Operations.Should().BeEmpty();
    }

    #endregion

    #region PushRetry (framework-internal)

    [Fact]
    public void PushRetry_appends_RetryOperation_with_full_payload()
    {
        var accessor = new ClientAccessor();
        var po = NewPo();

        accessor.PushRetry(
            step: 2,
            title: "Confirm overwrite?",
            options: ["yes", "no"],
            defaultOption: "no",
            persistentObject: po,
            message: "Existing entity will be replaced.");

        var op = accessor.Operations.Should().ContainSingle().Which.Should().BeOfType<RetryOperation>().Which;
        op.Step.Should().Be(2);
        op.Title.Should().Be("Confirm overwrite?");
        op.Options.Should().Equal("yes", "no");
        op.DefaultOption.Should().Be("no");
        op.PersistentObject.Should().BeSameAs(po);
        op.Message.Should().Be("Existing entity will be replaced.");
    }

    #endregion

    [Fact]
    public void Operations_preserve_insertion_order_across_mixed_operations()
    {
        var accessor = new ClientAccessor();

        accessor.Notify("first");
        accessor.RefreshQuery("q1");
        accessor.DisableActions("Edit");
        accessor.Navigate("home");

        accessor.Operations.Select(o => o.GetType()).Should().Equal(
            typeof(NotifyOperation),
            typeof(RefreshQueryOperation),
            typeof(DisableActionOperation),
            typeof(NavigateOperation));
    }
}
