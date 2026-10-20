using System.Text.Json;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// M11.1 / F4 — cross-module writes go through the authorization chokepoint. A write-back now always runs through
/// <see cref="ISparkReplacedSaves"/> (rights, attribute rights, validation, interceptors) or, for a delete, through
/// <see cref="IDatabaseAccess"/>; the end-to-end routing is pinned by <see cref="SyncActionHandlerIntegrationTests"/>.
/// What stays here: an entity type with no registered definition has no name for <c>security.json</c> to grant rights
/// on, so no authorization decision exists to make about it — unevaluable is not permitted, and nothing is touched.
/// </summary>
public class SyncActionAuthorizationTests
{
    private readonly IDocumentStore _documentStore = Substitute.For<IDocumentStore>();
    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly IEntityMapper _entityMapper = Substitute.For<IEntityMapper>();
    private readonly ISparkReplacedSaves _replacedSaves = Substitute.For<ISparkReplacedSaves>();
    private readonly IDatabaseAccess _databaseAccess = Substitute.For<IDatabaseAccess>();
    private readonly ILogger<SyncActionHandler> _logger = Substitute.For<ILogger<SyncActionHandler>>();

    private sealed class TestCar
    {
        public string? Id { get; set; }
        public string? LicensePlate { get; set; }
    }

    public SyncActionAuthorizationTests()
    {
        _documentStore.Conventions.Returns(new DocumentConventions());
        _modelLoader.GetEntityTypes().Returns([]);
        _modelLoader.GetEntityTypeByClrType(Arg.Any<string>()).Returns((EntityTypeDefinition?)null);
    }

    private SyncActionHandler CreateHandler()
        => new(_documentStore, _modelLoader, _entityMapper, _replacedSaves, _databaseAccess, _logger);

    private static Dictionary<string, JsonElement> Plate(string value)
        => new() { [nameof(TestCar.LicensePlate)] = JsonSerializer.SerializeToElement(value) };

    [Fact]
    public async Task A_write_back_edit_against_an_unregistered_type_is_refused_and_nothing_is_touched()
    {
        var outcome = await CreateHandler().ApplyEditAsync(new SyncEdit
        {
            EntityType = typeof(TestCar),
            DocumentId = "cars/1",
            Values = Plate("1-ABC-234"),
            BaseValues = Plate("1-XYZ-999"),
        });

        outcome.Kind.Should().Be(SyncEditOutcomeKind.Refused);
        await _replacedSaves.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default);
        _ = _documentStore.DidNotReceive().OpenAsyncSession();
    }

    [Fact]
    public async Task A_write_back_delete_against_an_unregistered_type_is_refused_and_nothing_is_touched()
    {
        var outcome = await CreateHandler().ApplyDeleteAsync(new SyncDelete
        {
            EntityType = typeof(TestCar),
            DocumentId = "cars/1",
            BaseValues = Plate("1-ABC-234"),
        });

        outcome.Kind.Should().Be(SyncEditOutcomeKind.Refused);
        await _databaseAccess.DidNotReceiveWithAnyArgs().DeletePersistentObjectAsync(default, default!, default, default);
    }
}
