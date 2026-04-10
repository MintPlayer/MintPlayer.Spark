using MintPlayer.Spark.Services;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Tests;

// Test entity and index types
public class TestCar { public string? Id { get; set; } }
public class VTestCar { public string? Id { get; set; } }

public class TestCar_Overview : AbstractIndexCreationTask<TestCar>
{
    public TestCar_Overview()
    {
        Map = cars => from c in cars select new { c.Id };
    }
}

public class IndexRegistryTests
{
    private readonly IndexRegistry _registry = new();

    [Fact]
    public void RegisterIndex_MakesItFindableByCollectionType()
    {
        _registry.RegisterIndex(typeof(TestCar_Overview));

        var registration = _registry.GetRegistrationForCollectionType(typeof(TestCar));

        Assert.NotNull(registration);
        Assert.Equal(typeof(TestCar), registration.CollectionType);
        Assert.Equal(typeof(TestCar_Overview), registration.IndexType);
        Assert.Equal("TestCar_Overview", registration.IndexName);
    }

    [Fact]
    public void RegisterIndex_MakesItFindableByIndexName()
    {
        _registry.RegisterIndex(typeof(TestCar_Overview));

        var registration = _registry.GetRegistrationByIndexName("TestCar_Overview");

        Assert.NotNull(registration);
    }

    [Fact]
    public void RegisterIndex_IndexNameLookup_IsCaseInsensitive()
    {
        _registry.RegisterIndex(typeof(TestCar_Overview));

        var registration = _registry.GetRegistrationByIndexName("testcar_overview");

        Assert.NotNull(registration);
    }

    [Fact]
    public void RegisterIndex_DuplicateIsIgnored()
    {
        _registry.RegisterIndex(typeof(TestCar_Overview));
        _registry.RegisterIndex(typeof(TestCar_Overview));

        var all = _registry.GetAllRegistrations().ToList();

        Assert.Single(all);
    }

    [Fact]
    public void RegisterProjection_AssociatesProjectionWithIndex()
    {
        _registry.RegisterIndex(typeof(TestCar_Overview));
        _registry.RegisterProjection(typeof(VTestCar), typeof(TestCar_Overview));

        var registration = _registry.GetRegistrationForCollectionType(typeof(TestCar));

        Assert.NotNull(registration);
        Assert.Equal(typeof(VTestCar), registration.ProjectionType);
    }

    [Fact]
    public void IsProjectionType_ReturnsTrueForRegisteredProjection()
    {
        _registry.RegisterIndex(typeof(TestCar_Overview));
        _registry.RegisterProjection(typeof(VTestCar), typeof(TestCar_Overview));

        Assert.True(_registry.IsProjectionType(typeof(VTestCar)));
    }

    [Fact]
    public void IsProjectionType_ReturnsFalseForUnknownType()
    {
        Assert.False(_registry.IsProjectionType(typeof(string)));
    }

    [Fact]
    public void GetRegistrationForCollectionType_ReturnsNull_WhenNotRegistered()
    {
        Assert.Null(_registry.GetRegistrationForCollectionType(typeof(string)));
    }

    [Fact]
    public void GetRegistrationByIndexName_ReturnsNull_WhenNotRegistered()
    {
        Assert.Null(_registry.GetRegistrationByIndexName("NonExistent"));
    }

    [Fact]
    public void GetAllRegistrations_ReturnsEmpty_WhenNoneRegistered()
    {
        Assert.Empty(_registry.GetAllRegistrations());
    }

    [Fact]
    public void RegisterProjection_BeforeIndex_DoesNotThrow()
    {
        // Registers projection for an index not yet known — should not throw
        _registry.RegisterProjection(typeof(VTestCar), typeof(TestCar_Overview));

        // Projection is not associated (index wasn't registered)
        Assert.False(_registry.IsProjectionType(typeof(VTestCar)));
    }
}
