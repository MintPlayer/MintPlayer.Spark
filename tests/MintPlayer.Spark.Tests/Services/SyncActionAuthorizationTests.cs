using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// M11.1 / F4 — cross-module sync writes go through the authorization chokepoint.
/// <para>
/// <c>/spark/sync/apply</c> used to reach <c>SaveEntityViaActionsAsync</c> directly, skipping
/// <c>DatabaseAccess</c> entirely — which is where <c>EnsureAuthorizedAsync</c> and the row-level
/// gate live. The mTLS check in front of it proved *which* module was calling and nothing then
/// consulted what that module was allowed to touch, so any authenticated module could insert,
/// update or delete any document in any collection.
/// </para>
/// <para>
/// These assert the routing, because the routing is the fix: the permission rules themselves are
/// already covered where they live. A test that only checked "an unauthorized module is refused"
/// could pass while a second, unchecked path still existed.
/// </para>
/// </summary>
public class SyncActionAuthorizationTests
{
    private static readonly Guid CarTypeId = Guid.Parse("aaaaaaaa-2222-2222-2222-222222222222");

    private readonly IDocumentStore _documentStore = Substitute.For<IDocumentStore>();
    private readonly IActionsResolver _actionsResolver = Substitute.For<IActionsResolver>();
    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly IEntityMapper _entityMapper = Substitute.For<IEntityMapper>();
    private readonly IDatabaseAccess _databaseAccess = Substitute.For<IDatabaseAccess>();
    private readonly ILogger<SyncActionHandler> _logger = Substitute.For<ILogger<SyncActionHandler>>();

    private sealed class TestCar
    {
        public string? Id { get; set; }
        public string? LicensePlate { get; set; }
    }

    public SyncActionAuthorizationTests()
    {
        // Collection-name resolution runs through the store's conventions, so a real instance is
        // needed rather than a substitute's null. TestCar therefore resolves as "TestCars".
        _documentStore.Conventions.Returns(new DocumentConventions());
    }

    private SyncActionHandler CreateHandler()
        => new(_documentStore, _actionsResolver, _modelLoader, _entityMapper, _databaseAccess, _logger);

    /// <summary>Registers TestCar as a known entity type so the schema path is taken.</summary>
    private void RegisterCar()
    {
        var definition = new EntityTypeDefinition
        {
            Id = CarTypeId,
            Name = "Car",
            ClrType = typeof(TestCar).FullName!,
        };

        _modelLoader.GetEntityTypeByClrType(typeof(TestCar).FullName!).Returns(definition);
        _modelLoader.GetEntityType(CarTypeId).Returns(definition);
        _modelLoader.GetEntityTypes().Returns([definition]);

        _entityMapper.GetPersistentObject(CarTypeId).Returns(_ => new PersistentObject
        {
            Name = "Car",
            ObjectTypeId = CarTypeId,
            Attributes = [new PersistentObjectAttribute { Name = nameof(TestCar.LicensePlate) }],
        });

        _databaseAccess.SavePersistentObjectAsync(Arg.Any<PersistentObject>())
            .Returns(call => Task.FromResult(call.Arg<PersistentObject>()));
    }

    [Fact]
    public async Task A_sync_save_goes_through_the_authorization_chokepoint()
    {
        RegisterCar();
        var handler = CreateHandler();

        await handler.HandleSaveAsync(
            "TestCars", documentId: null,
            new Dictionary<string, object?> { ["LicensePlate"] = "1-ABC-234" },
            properties: null);

        await _databaseAccess.Received(1).SavePersistentObjectAsync(
            Arg.Is<PersistentObject>(po => po.ObjectTypeId == CarTypeId));
    }

    [Fact]
    public async Task A_sync_delete_goes_through_the_authorization_chokepoint()
    {
        RegisterCar();
        var handler = CreateHandler();

        await handler.HandleDeleteAsync("TestCars", "cars/1");

        await _databaseAccess.Received(1).DeletePersistentObjectAsync(CarTypeId, "cars/1");
    }

    /// <summary>
    /// An entity type with no registered definition has no name for <c>security.json</c> to grant
    /// rights on, so no authorization decision exists to make about it. This path used to write it
    /// anyway, via a CLR-reflection fallback. Unevaluable is not permitted.
    /// </summary>
    [Fact]
    public async Task A_sync_save_against_an_unregistered_type_is_refused()
    {
        // TestCar resolves as a CLR type but is deliberately not registered as an entity.
        _modelLoader.GetEntityTypes().Returns([]);
        _modelLoader.GetEntityTypeByClrType(Arg.Any<string>()).Returns((EntityTypeDefinition?)null);

        var handler = CreateHandler();

        var act = () => handler.HandleSaveAsync(
            "TestCars", documentId: null,
            new Dictionary<string, object?> { ["LicensePlate"] = "1-ABC-234" },
            properties: null);

        await act.Should().ThrowAsync<Exception>(
            "a collection that cannot be authorized must not be written");

        await _databaseAccess.DidNotReceive().SavePersistentObjectAsync(Arg.Any<PersistentObject>());
    }
}
