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

        registration.Should().NotBeNull();
        registration!.CollectionType.Should().Be(typeof(TestCar));
        registration.IndexType.Should().Be(typeof(TestCar_Overview));
        registration.IndexName.Should().Be("TestCar_Overview");
    }

    [Fact]
    public void RegisterIndex_MakesItFindableByIndexName()
    {
        _registry.RegisterIndex(typeof(TestCar_Overview));

        var registration = _registry.GetRegistrationByIndexName("TestCar_Overview");

        registration.Should().NotBeNull();
    }

    [Fact]
    public void RegisterIndex_IndexNameLookup_IsCaseInsensitive()
    {
        _registry.RegisterIndex(typeof(TestCar_Overview));

        var registration = _registry.GetRegistrationByIndexName("testcar_overview");

        registration.Should().NotBeNull();
    }

    [Fact]
    public void RegisterIndex_DuplicateIsIgnored()
    {
        _registry.RegisterIndex(typeof(TestCar_Overview));
        _registry.RegisterIndex(typeof(TestCar_Overview));

        _registry.GetAllRegistrations().Should().ContainSingle();
    }

    [Fact]
    public void RegisterProjection_AssociatesProjectionWithIndex()
    {
        _registry.RegisterIndex(typeof(TestCar_Overview));
        _registry.RegisterProjection(typeof(VTestCar), typeof(TestCar_Overview));

        var registration = _registry.GetRegistrationForCollectionType(typeof(TestCar));

        registration.Should().NotBeNull();
        registration!.ProjectionType.Should().Be(typeof(VTestCar));
    }

    [Fact]
    public void IsProjectionType_ReturnsTrueForRegisteredProjection()
    {
        _registry.RegisterIndex(typeof(TestCar_Overview));
        _registry.RegisterProjection(typeof(VTestCar), typeof(TestCar_Overview));

        _registry.IsProjectionType(typeof(VTestCar)).Should().BeTrue();
    }

    [Fact]
    public void IsProjectionType_ReturnsFalseForUnknownType()
    {
        _registry.IsProjectionType(typeof(string)).Should().BeFalse();
    }

    [Fact]
    public void GetRegistrationForCollectionType_ReturnsNull_WhenNotRegistered()
    {
        _registry.GetRegistrationForCollectionType(typeof(string)).Should().BeNull();
    }

    [Fact]
    public void GetRegistrationByIndexName_ReturnsNull_WhenNotRegistered()
    {
        _registry.GetRegistrationByIndexName("NonExistent").Should().BeNull();
    }

    [Fact]
    public void GetAllRegistrations_ReturnsEmpty_WhenNoneRegistered()
    {
        _registry.GetAllRegistrations().Should().BeEmpty();
    }

    [Fact]
    public void RegisterProjection_BeforeIndex_DoesNotThrow()
    {
        _registry.RegisterProjection(typeof(VTestCar), typeof(TestCar_Overview));

        _registry.IsProjectionType(typeof(VTestCar)).Should().BeFalse();
    }
}
