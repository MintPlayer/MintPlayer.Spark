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
}
