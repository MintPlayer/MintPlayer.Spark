using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests;

public class PersistentObjectAttributeTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var attr = new PersistentObjectAttribute { Name = "Test" };

        Assert.Equal("string", attr.DataType);
        Assert.True(attr.IsVisible);
        Assert.False(attr.IsReadOnly);
        Assert.False(attr.IsRequired);
        Assert.False(attr.IsArray);
        Assert.False(attr.IsValueChanged);
        Assert.Equal(0, attr.Order);
        Assert.Null(attr.Value);
        Assert.Null(attr.Breadcrumb);
        Assert.Null(attr.Query);
        Assert.Null(attr.Group);
        Assert.Null(attr.Renderer);
        Assert.Null(attr.RendererOptions);
        Assert.Equal(EShowedOn.Query | EShowedOn.PersistentObject, attr.ShowedOn);
        Assert.Empty(attr.Rules);
    }

    [Fact]
    public void GetValue_ReturnsDefault_WhenValueIsNull()
    {
        var attr = new PersistentObjectAttribute { Name = "Test", Value = null };

        Assert.Null(attr.GetValue<string>());
        Assert.Equal(0, attr.GetValue<int>());
        Assert.False(attr.GetValue<bool>());
    }

    [Fact]
    public void GetValue_ReturnsTypedValue_WhenValueIsSet()
    {
        var attr = new PersistentObjectAttribute { Name = "Test", Value = 42 };

        Assert.Equal(42, attr.GetValue<int>());
    }

    [Fact]
    public void GetValue_ConvertsTypes()
    {
        var attr = new PersistentObjectAttribute { Name = "Test", Value = 42 };

        Assert.Equal(42L, attr.GetValue<long>());
        Assert.Equal(42.0, attr.GetValue<double>());
    }

    [Fact]
    public void SetValue_StoresValue()
    {
        var attr = new PersistentObjectAttribute { Name = "Test" };

        attr.SetValue("hello");

        Assert.Equal("hello", attr.Value);
    }

    [Fact]
    public void SetValue_CanSetNull()
    {
        var attr = new PersistentObjectAttribute { Name = "Test", Value = "existing" };

        attr.SetValue<string?>(null);

        Assert.Null(attr.Value);
    }

    [Fact]
    public void SetValue_ThenGetValue_Roundtrips()
    {
        var attr = new PersistentObjectAttribute { Name = "Test" };

        attr.SetValue(3.14m);

        Assert.Equal(3.14m, attr.GetValue<decimal>());
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

        Assert.Empty(po.Attributes);
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

        Assert.Equal("Test/1", po.Id);
        Assert.Equal("Test", po.Name);
        Assert.Equal(typeId, po.ObjectTypeId);
        Assert.Equal("Test #1", po.Breadcrumb);
    }
}
