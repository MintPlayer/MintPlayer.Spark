using System.Text.Json;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Covers <see cref="SyncActionHandler.BuildPersistentObject"/> after the Phase 3
/// migration: the schema branch routes through <see cref="IEntityMapper.GetPersistentObject(Guid)"/>
/// so attributes get the full 14-field metadata scaffold, and the CLR-reflection
/// fallback stays inline for entity types without a registered definition.
/// </summary>
public class SyncActionHandlerBuildPersistentObjectTests
{
    private static readonly Guid CarTypeId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");

    private readonly IDocumentStore _documentStore = Substitute.For<IDocumentStore>();
    private readonly IActionsResolver _actionsResolver = Substitute.For<IActionsResolver>();
    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly IEntityMapper _entityMapper = Substitute.For<IEntityMapper>();
    private readonly ILogger<SyncActionHandler> _logger = Substitute.For<ILogger<SyncActionHandler>>();

    private SyncActionHandler CreateHandler()
        => new(_documentStore, _actionsResolver, _modelLoader, _entityMapper, _logger);

    private sealed class TestCar { public string? Id { get; set; } public string? LicensePlate { get; set; } public int Year { get; set; } }
    private sealed class UnregisteredEntity { public string? Id { get; set; } public string? Name { get; set; } }

    // --- Schema path: via IEntityMapper.GetPersistentObject ----------------

    [Fact]
    public void BuildPersistentObject_Schema_UsesEntityMapperScaffold()
    {
        var def = new EntityTypeDefinition
        {
            Id = CarTypeId,
            Name = "Car",
            ClrType = typeof(TestCar).FullName!,
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LicensePlate", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Year", DataType = "number" },
            ],
        };
        _modelLoader.GetEntityTypeByClrType(typeof(TestCar).FullName!).Returns(def);

        var scaffold = new PersistentObject
        {
            Name = "Car",
            ObjectTypeId = CarTypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = "LicensePlate", DataType = "string", Label = TranslatedString.Create("License plate") },
                new PersistentObjectAttribute { Name = "Year", DataType = "number" },
            ],
        };
        _entityMapper.GetPersistentObject(CarTypeId).Returns(scaffold);

        var data = new Dictionary<string, object?> { ["LicensePlate"] = "ABC-123", ["Year"] = 2024 };

        var po = CreateHandler().BuildPersistentObject(typeof(TestCar), "cars/1", data, properties: null);

        po.Should().BeSameAs(scaffold);
        po.Id.Should().Be("cars/1");
        po["LicensePlate"].Value.Should().Be("ABC-123");
        po["Year"].Value.Should().Be(2024);
        po["LicensePlate"].Label!.GetValue("en").Should().Be("License plate",
            "schema metadata from the mapper scaffold must survive the value overlay");
    }

    [Fact]
    public void BuildPersistentObject_Schema_IsValueChanged_FromPropertySet()
    {
        var def = new EntityTypeDefinition
        {
            Id = CarTypeId,
            Name = "Car",
            ClrType = typeof(TestCar).FullName!,
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LicensePlate", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Year", DataType = "number" },
            ],
        };
        _modelLoader.GetEntityTypeByClrType(typeof(TestCar).FullName!).Returns(def);
        _entityMapper.GetPersistentObject(CarTypeId).Returns(new PersistentObject
        {
            Name = "Car",
            ObjectTypeId = CarTypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = "LicensePlate" },
                new PersistentObjectAttribute { Name = "Year" },
            ],
        });

        var data = new Dictionary<string, object?> { ["LicensePlate"] = "ABC-123", ["Year"] = 2024 };
        var properties = new[] { "LicensePlate" };

        var po = CreateHandler().BuildPersistentObject(typeof(TestCar), "cars/1", data, properties);

        po["LicensePlate"].IsValueChanged.Should().BeTrue("explicitly listed in properties[]");
        po["Year"].IsValueChanged.Should().BeFalse("not listed in properties[] — partial update");
    }

    [Fact]
    public void BuildPersistentObject_Schema_IsValueChanged_FallsBackToHasValue_WhenNoProperties()
    {
        var def = new EntityTypeDefinition
        {
            Id = CarTypeId,
            Name = "Car",
            ClrType = typeof(TestCar).FullName!,
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LicensePlate", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Year", DataType = "number" },
            ],
        };
        _modelLoader.GetEntityTypeByClrType(typeof(TestCar).FullName!).Returns(def);
        _entityMapper.GetPersistentObject(CarTypeId).Returns(new PersistentObject
        {
            Name = "Car",
            ObjectTypeId = CarTypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = "LicensePlate" },
                new PersistentObjectAttribute { Name = "Year" },
            ],
        });

        var data = new Dictionary<string, object?> { ["LicensePlate"] = "ABC-123" };

        var po = CreateHandler().BuildPersistentObject(typeof(TestCar), "cars/1", data, properties: null);

        po["LicensePlate"].IsValueChanged.Should().BeTrue("full sync — present keys are changed");
        po["Year"].IsValueChanged.Should().BeFalse("not present in incoming data");
        po["Year"].Value.Should().BeNull();
    }

    [Fact]
    public void BuildPersistentObject_Schema_NormalizesJsonElementValues()
    {
        var def = new EntityTypeDefinition
        {
            Id = CarTypeId,
            Name = "Car",
            ClrType = typeof(TestCar).FullName!,
            Attributes = [new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LicensePlate", DataType = "string" }],
        };
        _modelLoader.GetEntityTypeByClrType(typeof(TestCar).FullName!).Returns(def);
        _entityMapper.GetPersistentObject(CarTypeId).Returns(new PersistentObject
        {
            Name = "Car",
            ObjectTypeId = CarTypeId,
            Attributes = [new PersistentObjectAttribute { Name = "LicensePlate" }],
        });

        // HTTP path delivers JsonElement values
        var json = """{"LicensePlate": "ABC-123"}""";
        var element = JsonDocument.Parse(json).RootElement.GetProperty("LicensePlate");
        var data = new Dictionary<string, object?> { ["LicensePlate"] = element };

        var po = CreateHandler().BuildPersistentObject(typeof(TestCar), "cars/1", data, properties: null);

        po["LicensePlate"].Value.Should().Be("ABC-123",
            "JsonElement → string normalization happens before value is stored");
    }

    // --- CLR-reflection fallback ------------------------------------------

    [Fact]
    public void BuildPersistentObject_UnregisteredType_UsesClrReflectionFallback()
    {
        _modelLoader.GetEntityTypeByClrType(typeof(UnregisteredEntity).FullName!)
            .Returns((EntityTypeDefinition?)null);

        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        var po = CreateHandler().BuildPersistentObject(typeof(UnregisteredEntity), "x/1", data, properties: null);

        po.Id.Should().Be("x/1");
        po.ObjectTypeId.Should().Be(Guid.Empty, "no schema → no canonical ObjectTypeId");
        po.Name.Should().Be("UnregisteredEntity");
        po.Attributes.Should().ContainSingle(a => a.Name == "Name")
            .Which.Value.Should().Be("Alice");

        // The fallback must not reach the entity mapper — schema is unavailable.
        _entityMapper.DidNotReceiveWithAnyArgs().GetPersistentObject(Arg.Any<Guid>());
    }

    [Fact]
    public void BuildPersistentObject_UnregisteredType_SkipsIdProperty()
    {
        _modelLoader.GetEntityTypeByClrType(typeof(UnregisteredEntity).FullName!)
            .Returns((EntityTypeDefinition?)null);

        var data = new Dictionary<string, object?> { ["Id"] = "should-not-appear", ["Name"] = "Alice" };

        var po = CreateHandler().BuildPersistentObject(typeof(UnregisteredEntity), "x/1", data, properties: null);

        po.Attributes.Should().NotContain(a => a.Name == "Id",
            "Id is the document identifier, not an attribute");
    }
}
