using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests;

public class PersistentObjectAttributeTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var attr = new PersistentObjectAttribute { Name = "Test" };

        attr.DataType.Should().Be("string");
        attr.IsVisible.Should().BeTrue();
        attr.IsReadOnly.Should().BeFalse();
        attr.IsRequired.Should().BeFalse();
        attr.IsArray.Should().BeFalse();
        attr.IsValueChanged.Should().BeFalse();
        attr.Order.Should().Be(0);
        attr.Value.Should().BeNull();
        attr.Breadcrumb.Should().BeNull();
        attr.Query.Should().BeNull();
        attr.Group.Should().BeNull();
        attr.Renderer.Should().BeNull();
        attr.RendererOptions.Should().BeNull();
        attr.ShowedOn.Should().Be(EShowedOn.Query | EShowedOn.PersistentObject);
        attr.Rules.Should().BeEmpty();
    }

    [Fact]
    public void GetValue_ReturnsDefault_WhenValueIsNull()
    {
        var attr = new PersistentObjectAttribute { Name = "Test", Value = null };

        attr.GetValue<string>().Should().BeNull();
        attr.GetValue<int>().Should().Be(0);
        attr.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void GetValue_ReturnsTypedValue_WhenValueIsSet()
    {
        var attr = new PersistentObjectAttribute { Name = "Test", Value = 42 };

        attr.GetValue<int>().Should().Be(42);
    }

    [Fact]
    public void GetValue_ConvertsTypes()
    {
        var attr = new PersistentObjectAttribute { Name = "Test", Value = 42 };

        attr.GetValue<long>().Should().Be(42L);
        attr.GetValue<double>().Should().Be(42.0);
    }

    [Fact]
    public void SetValue_StoresValue()
    {
        var attr = new PersistentObjectAttribute { Name = "Test" };

        attr.SetValue("hello");

        attr.Value.Should().Be("hello");
    }

    [Fact]
    public void SetValue_CanSetNull()
    {
        var attr = new PersistentObjectAttribute { Name = "Test", Value = "existing" };

        attr.SetValue<string?>(null);

        attr.Value.Should().BeNull();
    }

    [Fact]
    public void SetValue_ThenGetValue_Roundtrips()
    {
        var attr = new PersistentObjectAttribute { Name = "Test" };

        attr.SetValue(3.14m);

        attr.GetValue<decimal>().Should().Be(3.14m);
    }
}

public class PersistentObjectTests
{
    [Fact]
    public void Attributes_DefaultsToEmptyArray()
    {
        var po = new PersistentObject
        {
            Name = "Test",
            ObjectTypeId = Guid.NewGuid()
        };

        po.Attributes.Should().BeEmpty();
    }

    [Fact]
    public void Properties_CanBeSet()
    {
        var typeId = Guid.NewGuid();
        var po = new PersistentObject
        {
            Id = "Test/1",
            Name = "Test",
            ObjectTypeId = typeId,
            Breadcrumb = "Test #1"
        };

        po.Id.Should().Be("Test/1");
        po.Name.Should().Be("Test");
        po.ObjectTypeId.Should().Be(typeId);
        po.Breadcrumb.Should().Be("Test #1");
    }

    [Fact]
    public void InitSetter_RoutesThroughAddAttribute_SettingParent()
    {
        var po = new PersistentObject
        {
            Name = "Car",
            ObjectTypeId = Guid.NewGuid(),
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Plate", Value = "ABC-123" },
                new PersistentObjectAttribute { Name = "Model" },
            ],
        };

        po.Attributes.Should().HaveCount(2);
        po.Attributes.Should().OnlyContain(a => a.Parent == po);
    }

    [Fact]
    public void AddAttribute_SetsParent()
    {
        var po = new PersistentObject { Name = "Car", ObjectTypeId = Guid.NewGuid() };
        var attr = new PersistentObjectAttribute { Name = "Plate" };

        po.AddAttribute(attr);

        attr.Parent.Should().BeSameAs(po);
        po.Attributes.Should().ContainSingle().Which.Should().BeSameAs(attr);
    }

    [Fact]
    public void Indexer_ReturnsAttributeByName()
    {
        var plate = new PersistentObjectAttribute { Name = "Plate", Value = "ABC" };
        var po = new PersistentObject
        {
            Name = "Car",
            ObjectTypeId = Guid.NewGuid(),
            Attributes = [plate, new PersistentObjectAttribute { Name = "Model" }],
        };

        po["Plate"].Should().BeSameAs(plate);
    }

    [Fact]
    public void Indexer_Throws_WhenAttributeMissing()
    {
        var po = new PersistentObject
        {
            Name = "Car",
            ObjectTypeId = Guid.NewGuid(),
            Attributes = [new PersistentObjectAttribute { Name = "Plate" }],
        };

        var act = () => po["Unknown"];

        act.Should().Throw<KeyNotFoundException>()
            .WithMessage("*Unknown*Car*");
    }

    [Fact]
    public void Attributes_PublicSurface_IsIReadOnlyList()
    {
        // Compile-time contract — the declared property type must be IReadOnlyList<T>
        // so framework consumers cannot mutate Attributes directly. The runtime value
        // is a List<T> (implementation detail), but that's not exposed publicly.
        var propertyType = typeof(PersistentObject).GetProperty(nameof(PersistentObject.Attributes))!.PropertyType;

        propertyType.Should().Be<IReadOnlyList<PersistentObjectAttribute>>();
    }
}

public class PersistentObjectAttributeCloneAndAddTests
{
    private static PersistentObject BuildCarWithLicensePlate(out PersistentObjectAttribute plate)
    {
        plate = new PersistentObjectAttribute
        {
            Name = "LicensePlate",
            Label = TranslatedString.Create("License plate"),
            Value = "ABC-123",
            DataType = "String",
            IsRequired = true,
            Rules = [new ValidationRule { Type = "regex", Value = "^[A-Z]{3}-[0-9]{3}$" }],
            RendererOptions = new Dictionary<string, object> { ["maxLength"] = 7 },
        };
        return new PersistentObject
        {
            Name = "Car",
            ObjectTypeId = Guid.NewGuid(),
            Attributes = [plate],
        };
    }

    [Fact]
    public void CloneAndAdd_AddsCloneToSameParent_WithNewName()
    {
        var po = BuildCarWithLicensePlate(out var plate);

        var confirmation = plate.CloneAndAdd("Confirmation");

        confirmation.Name.Should().Be("Confirmation");
        confirmation.Parent.Should().BeSameAs(po);
        po.Attributes.Should().HaveCount(2).And.Contain(confirmation);
    }

    [Fact]
    public void CloneAndAdd_NullsValueAndIdAndChangedFlag()
    {
        var po = BuildCarWithLicensePlate(out var plate);
        plate.Id = "src-id";
        plate.IsValueChanged = true;

        var clone = plate.CloneAndAdd("Confirmation");

        clone.Value.Should().BeNull();
        clone.Id.Should().BeNull();
        clone.IsValueChanged.Should().BeFalse();
    }

    [Fact]
    public void CloneAndAdd_DeepCopiesRulesAndRendererOptions()
    {
        BuildCarWithLicensePlate(out var plate);

        var clone = plate.CloneAndAdd("Confirmation");

        clone.Rules.Should().NotBeSameAs(plate.Rules);
        clone.RendererOptions.Should().NotBeSameAs(plate.RendererOptions);

        clone.RendererOptions!["maxLength"] = 99;
        plate.RendererOptions!["maxLength"].Should().Be(7, "mutations on the clone must not bleed to the source");
    }

    [Fact]
    public void CloneAndAdd_OverridesLabel_WhenProvided()
    {
        BuildCarWithLicensePlate(out var plate);
        var newLabel = TranslatedString.Create("Type the plate to confirm");

        var clone = plate.CloneAndAdd("Confirmation", newLabel);

        clone.Label.Should().BeSameAs(newLabel);
    }

    [Fact]
    public void CloneAndAdd_Throws_WhenSourceNotAttached()
    {
        var orphan = new PersistentObjectAttribute { Name = "Orphan" };

        var act = () => orphan.CloneAndAdd("Clone");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*attached*");
    }
}
