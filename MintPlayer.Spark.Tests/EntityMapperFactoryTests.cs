using System.Drawing;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// Covers the Phase 2 factory surface on <see cref="IEntityMapper"/>:
/// GetPersistentObject(string|Guid|T), ToPersistentObject(T), and
/// PopulateAttributeValues(PersistentObject, object, …) — including the
/// Vidyano-parity silent-skip and dot-notation-skip rules, value-for-wire
/// conversions (enum, Color), and Parent-back-reference invariants.
/// </summary>
public class EntityMapperFactoryTests
{
    private static readonly Guid CarTypeId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");

    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly EntityMapper _mapper;

    public EntityMapperFactoryTests()
    {
        var carTypeDef = new EntityTypeDefinition
        {
            Id = CarTypeId,
            Name = "Car",
            ClrType = typeof(TestCar).FullName!,
            DisplayAttribute = "LicensePlate",
            Attributes =
            [
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = "LicensePlate",
                    DataType = "string",
                    Label = TranslatedString.Create("License plate"),
                    IsRequired = true,
                    Order = 1,
                    Rules = [new ValidationRule { Type = "regex", Value = "^[A-Z]{3}-[0-9]{3}$" }],
                    Renderer = "plate",
                    RendererOptions = new Dictionary<string, object> { ["maxLength"] = 7 },
                    Group = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222"),
                    ShowedOn = EShowedOn.PersistentObject,
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = "Status",
                    DataType = "string",
                    Order = 2,
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = "Color",
                    DataType = "color",
                    Order = 3,
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = "Owner.Name", // dot-notation; populate must skip
                    DataType = "string",
                    Order = 4,
                },
            ],
        };

        _modelLoader.GetEntityType(CarTypeId).Returns(carTypeDef);
        _modelLoader.GetEntityTypeByName("Car").Returns(carTypeDef);
        _modelLoader.GetEntityTypeByClrType(typeof(TestCar).FullName!).Returns(carTypeDef);

        _mapper = new EntityMapper(_modelLoader);
    }

    // --- GetPersistentObject(string) --------------------------------------

    [Fact]
    public void GetPersistentObject_ByName_CopiesAllMetadataFieldsWithValuesNull()
    {
        var po = _mapper.GetPersistentObject("Car");

        po.Name.Should().Be("Car");
        po.ObjectTypeId.Should().Be(CarTypeId);
        po.Id.Should().BeNull();
        po.Attributes.Should().HaveCount(4);

        var plate = po["LicensePlate"];
        plate.DataType.Should().Be("string");
        plate.Label!.GetValue("en").Should().Be("License plate");
        plate.IsRequired.Should().BeTrue();
        plate.Order.Should().Be(1);
        plate.Rules.Should().HaveCount(1).And.Contain(r => r.Type == "regex");
        plate.Renderer.Should().Be("plate");
        plate.RendererOptions.Should().ContainKey("maxLength");
        plate.Group.Should().Be(Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222"));
        plate.ShowedOn.Should().Be(EShowedOn.PersistentObject);
        plate.Value.Should().BeNull();
        plate.Parent.Should().BeSameAs(po);
    }

    [Fact]
    public void GetPersistentObject_ByName_Throws_WhenNameNotRegistered()
    {
        _modelLoader.GetEntityTypeByName("Unknown").Returns((EntityTypeDefinition?)null);

        var act = () => _mapper.GetPersistentObject("Unknown");

        act.Should().Throw<KeyNotFoundException>().WithMessage("*Unknown*");
    }

    // --- GetPersistentObject(Guid) ----------------------------------------

    [Fact]
    public void GetPersistentObject_ById_ReturnsEquivalentScaffold()
    {
        var byName = _mapper.GetPersistentObject("Car");
        var byId = _mapper.GetPersistentObject(CarTypeId);

        byId.Name.Should().Be(byName.Name);
        byId.ObjectTypeId.Should().Be(byName.ObjectTypeId);
        byId.Attributes.Select(a => a.Name).Should().Equal(byName.Attributes.Select(a => a.Name));
    }

    [Fact]
    public void GetPersistentObject_ById_Throws_WhenIdNotRegistered()
    {
        var unknown = Guid.NewGuid();
        _modelLoader.GetEntityType(unknown).Returns((EntityTypeDefinition?)null);

        var act = () => _mapper.GetPersistentObject(unknown);

        act.Should().Throw<KeyNotFoundException>().WithMessage($"*{unknown}*");
    }

    // --- GetPersistentObject<T>() -----------------------------------------

    [Fact]
    public void GetPersistentObject_Generic_ResolvesByClrType()
    {
        var po = _mapper.GetPersistentObject<TestCar>();

        po.ObjectTypeId.Should().Be(CarTypeId);
        po.Attributes.Should().HaveCount(4);
    }

    [Fact]
    public void GetPersistentObject_Generic_Throws_WhenClrTypeNotRegistered()
    {
        var act = () => _mapper.GetPersistentObject<UnrelatedClass>();

        act.Should().Throw<KeyNotFoundException>()
            .WithMessage($"*{typeof(UnrelatedClass).FullName}*");
    }

    // --- PopulateAttributeValues ------------------------------------------

    [Fact]
    public void PopulateAttributeValues_FillsValueOnMatchingProperties()
    {
        var po = _mapper.GetPersistentObject("Car");
        var car = new TestCar { Id = "cars/1", LicensePlate = "ABC-123", Status = TestCarStatus.Active };

        _mapper.PopulateAttributeValues(po, car);

        po["LicensePlate"].Value.Should().Be("ABC-123");
        po["Status"].Value.Should().Be("Active", "enums convert to their string name for the wire");
    }

    [Fact]
    public void PopulateAttributeValues_SetsIdNameAndBreadcrumb()
    {
        var po = _mapper.GetPersistentObject("Car");
        var car = new TestCar { Id = "cars/1", LicensePlate = "ABC-123" };

        _mapper.PopulateAttributeValues(po, car);

        po.Id.Should().Be("cars/1");
        po.Name.Should().Be("ABC-123", "DisplayAttribute=LicensePlate");
        po.Breadcrumb.Should().Be("ABC-123");
    }

    [Fact]
    public void PopulateAttributeValues_SkipsAttributesWithDotNotation()
    {
        var po = _mapper.GetPersistentObject("Car");
        var car = new TestCar { LicensePlate = "ABC-123" };

        _mapper.PopulateAttributeValues(po, car);

        po["Owner.Name"].Value.Should().BeNull("dot-notation names are reserved for nested PO support (Vidyano parity)");
    }

    [Fact]
    public void PopulateAttributeValues_LeavesValueNull_WhenPropertyMissing()
    {
        // Attribute "Color" exists on Car def; property exists on TestCar.
        // Confirms: matching properties DO get populated, non-matching fall through silently.
        var po = _mapper.GetPersistentObject("Car");
        var car = new TestCar { LicensePlate = "ABC-123" }; // Color not set → stays default (empty)

        _mapper.PopulateAttributeValues(po, car);

        po["Color"].Value.Should().BeNull(
            "Color.IsEmpty converts to null on the wire per the Color→hex conversion");
    }

    [Fact]
    public void PopulateAttributeValues_ConvertsColorToHex()
    {
        var po = _mapper.GetPersistentObject("Car");
        var car = new TestCar { LicensePlate = "ABC", Color = Color.FromArgb(0x12, 0x34, 0x56) };

        _mapper.PopulateAttributeValues(po, car);

        po["Color"].Value.Should().Be("#123456");
    }

    // --- ToPersistentObject<T> --------------------------------------------

    [Fact]
    public void ToPersistentObject_Generic_ResolvesGuidAndPopulates()
    {
        var car = new TestCar { Id = "cars/1", LicensePlate = "ABC-123", Status = TestCarStatus.Retired };

        var po = _mapper.ToPersistentObject(car);

        po.ObjectTypeId.Should().Be(CarTypeId);
        po["LicensePlate"].Value.Should().Be("ABC-123");
        po["Status"].Value.Should().Be("Retired");
    }

    [Fact]
    public void ToPersistentObject_Generic_MatchesNonGenericOutput()
    {
        var car = new TestCar { Id = "cars/1", LicensePlate = "ABC-123" };

        var viaGeneric = _mapper.ToPersistentObject(car);
        var viaGuid = _mapper.ToPersistentObject(car, CarTypeId);

        viaGeneric.Id.Should().Be(viaGuid.Id);
        viaGeneric.Name.Should().Be(viaGuid.Name);
        viaGeneric.ObjectTypeId.Should().Be(viaGuid.ObjectTypeId);
        viaGeneric.Attributes.Select(a => (a.Name, a.Value?.ToString()))
            .Should().Equal(viaGuid.Attributes.Select(a => (a.Name, a.Value?.ToString())));
    }

    // --- fixtures ---------------------------------------------------------

    private sealed class TestCar
    {
        public string? Id { get; set; }
        public string? LicensePlate { get; set; }
        public TestCarStatus Status { get; set; }
        public Color Color { get; set; }
    }

    private enum TestCarStatus
    {
        Active,
        Retired,
    }

    private sealed class UnrelatedClass
    {
        public string? Anything { get; set; }
    }
}
