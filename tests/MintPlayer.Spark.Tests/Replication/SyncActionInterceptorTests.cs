using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Replication;

public class SyncActionInterceptorTests : SparkTestDriver
{
    private SyncActionInterceptor NewInterceptor(string moduleName = "Fleet") =>
        new(Store, Options.Create(new SparkReplicationOptions
        {
            ModuleName = moduleName,
            ModuleUrl = "https://localhost:5001",
        }), NullLogger<SyncActionInterceptor>.Instance);

    [Fact]
    public void IsReplicated_returns_true_for_types_with_Replicated_attribute_and_false_otherwise()
    {
        var interceptor = NewInterceptor();

        interceptor.IsReplicated(typeof(ReplicatedCarFromFleet)).Should().BeTrue();
        interceptor.IsReplicated(typeof(NonReplicated)).Should().BeFalse();
    }

    [Fact]
    public async Task HandleSaveAsync_with_null_Id_records_an_Insert_action()
    {
        var interceptor = NewInterceptor(moduleName: "Fleet");
        var po = new MintPlayer.Spark.Abstractions.PersistentObject
        {
            Id = null, // Insert
            Name = "new",
            ObjectTypeId = Guid.NewGuid(),
            Attributes = [new() { Name = "Plate", Value = "ABC-123", IsValueChanged = true }],
        };

        await interceptor.HandleSaveAsync(typeof(ReplicatedCarFromFleet), po);
        WaitForIndexing(Store);

        using var session = Store.OpenAsyncSession();
        var stored = await session.Query<SparkSyncAction>().SingleAsync();
        stored.Actions.Should().ContainSingle();
        var action = stored.Actions[0];
        action.ActionType.Should().Be(SyncActionType.Insert);
        action.DocumentId.Should().BeNull();
        action.Collection.Should().Be("Cars");
        action.Properties.Should().Equal("Plate");
        action.Data!["Plate"].Should().Be("ABC-123");
    }

    [Fact]
    public async Task HandleSaveAsync_with_existing_Id_records_an_Update_action_carrying_the_Id_in_the_data()
    {
        var interceptor = NewInterceptor();
        var po = new MintPlayer.Spark.Abstractions.PersistentObject
        {
            Id = "cars/1",
            Name = "existing",
            ObjectTypeId = Guid.NewGuid(),
            Attributes = [new() { Name = "Plate", Value = "XYZ-999", IsValueChanged = true }],
        };

        await interceptor.HandleSaveAsync(typeof(ReplicatedCarFromFleet), po);
        WaitForIndexing(Store);

        using var session = Store.OpenAsyncSession();
        var stored = await session.Query<SparkSyncAction>().SingleAsync();
        var action = stored.Actions[0];
        action.ActionType.Should().Be(SyncActionType.Update);
        action.DocumentId.Should().Be("cars/1");
        action.Data!["Id"].Should().Be("cars/1");
        action.Data["Plate"].Should().Be("XYZ-999");
    }

    [Fact]
    public async Task HandleDeleteAsync_records_a_Delete_action_with_no_Data_or_Properties()
    {
        var interceptor = NewInterceptor();

        await interceptor.HandleDeleteAsync(typeof(ReplicatedCarFromFleet), "cars/42");
        WaitForIndexing(Store);

        using var session = Store.OpenAsyncSession();
        var stored = await session.Query<SparkSyncAction>().SingleAsync();
        var action = stored.Actions[0];
        action.ActionType.Should().Be(SyncActionType.Delete);
        action.DocumentId.Should().Be("cars/42");
        action.Data.Should().BeNull();
        action.Properties.Should().BeNull();
    }

    [Fact]
    public async Task HandleSaveAsync_without_IsValueChanged_flags_falls_back_to_all_property_names()
    {
        var interceptor = NewInterceptor();
        var po = new MintPlayer.Spark.Abstractions.PersistentObject
        {
            Id = "cars/1",
            Name = "unchanged",
            ObjectTypeId = Guid.NewGuid(),
            Attributes = [
                new() { Name = "Plate", Value = "ABC", IsValueChanged = false },
                new() { Name = "Color", Value = "red", IsValueChanged = false },
            ],
        };

        await interceptor.HandleSaveAsync(typeof(ReplicatedCarFromFleet), po);
        WaitForIndexing(Store);

        using var session = Store.OpenAsyncSession();
        var stored = await session.Query<SparkSyncAction>().SingleAsync();
        // Falls back to GetPropertyNames(ReplicatedCarFromFleet) which is [Plate] (Id excluded)
        stored.Actions[0].Properties.Should().Equal("Plate");
    }

    [Fact]
    public async Task HandleSaveAsync_normalizes_JsonElement_values_to_plain_DotNet_types()
    {
        var interceptor = NewInterceptor();
        var doc = JsonDocument.Parse("""{ "s": "hi", "i": 42, "b": true }""");

        var po = new MintPlayer.Spark.Abstractions.PersistentObject
        {
            Id = "cars/1",
            Name = "json",
            ObjectTypeId = Guid.NewGuid(),
            Attributes = [
                new() { Name = "S", Value = doc.RootElement.GetProperty("s"), IsValueChanged = true },
                new() { Name = "I", Value = doc.RootElement.GetProperty("i"), IsValueChanged = true },
                new() { Name = "B", Value = doc.RootElement.GetProperty("b"), IsValueChanged = true },
            ],
        };

        await interceptor.HandleSaveAsync(typeof(ReplicatedCarFromFleet), po);
        WaitForIndexing(Store);

        using var session = Store.OpenAsyncSession();
        var stored = await session.Query<SparkSyncAction>().SingleAsync();
        var data = stored.Actions[0].Data!;
        data["S"].Should().Be("hi");
        data["I"].Should().Be(42);
        data["B"].Should().Be(true);
    }

    [Fact]
    public async Task HandleSaveAsync_throws_for_non_replicated_entity_types()
    {
        var interceptor = NewInterceptor();
        var po = new MintPlayer.Spark.Abstractions.PersistentObject
        {
            Name = "x",
            ObjectTypeId = Guid.NewGuid(),
            Attributes = [],
        };

        var act = async () => await interceptor.HandleSaveAsync(typeof(NonReplicated), po);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a replicated entity*");
    }

    [Fact]
    public async Task DispatchAsync_writes_both_OwnerModuleName_from_attribute_and_RequestingModule_from_options()
    {
        var interceptor = NewInterceptor(moduleName: "CurrentModule");

        await interceptor.HandleDeleteAsync(typeof(ReplicatedCarFromFleet), "cars/1");
        WaitForIndexing(Store);

        using var session = Store.OpenAsyncSession();
        var stored = await session.Query<SparkSyncAction>().SingleAsync();
        stored.OwnerModuleName.Should().Be("Fleet");
        stored.RequestingModule.Should().Be("CurrentModule");
        stored.Status.Should().Be(ESyncActionStatus.Pending);
    }

    private class NonReplicated
    {
        public string? Id { get; set; }
    }
}
