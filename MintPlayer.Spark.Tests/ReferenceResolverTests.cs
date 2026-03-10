using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests;

public class ReferenceResolverTests
{
    private readonly ReferenceResolver _resolver = new();

    [Fact]
    public void GetReferenceProperties_FindsReferenceAttributes()
    {
        var props = _resolver.GetReferenceProperties(typeof(TestPerson));

        Assert.Single(props);
        Assert.Equal("Company", props[0].Property.Name);
        Assert.Equal(typeof(TestCompany), props[0].Attribute.TargetType);
    }

    [Fact]
    public void GetReferenceProperties_ReturnsEmptyForTypeWithoutReferences()
    {
        var props = _resolver.GetReferenceProperties(typeof(TestCompany));

        Assert.Empty(props);
    }

    [Fact]
    public void GetReferenceProperties_ProjectionFallsBackToBaseType()
    {
        // VTestPerson has no [Reference] on Company, but TestPerson does.
        // The fallback should return VTestPerson's PropertyInfo + TestPerson's ReferenceAttribute.
        var props = _resolver.GetReferenceProperties(typeof(VTestPerson), typeof(TestPerson));

        Assert.Single(props);
        Assert.Equal("Company", props[0].Property.Name);
        Assert.Equal(typeof(TestCompany), props[0].Attribute.TargetType);
        // PropertyInfo must be from the projection type (VTestPerson), not the base type
        Assert.Equal(typeof(VTestPerson), props[0].Property.DeclaringType);
    }

    [Fact]
    public void GetReferenceProperties_ProjectionWithOwnReferenceDoesNotFallBack()
    {
        // If the projection type has its own [Reference], use it directly.
        var props = _resolver.GetReferenceProperties(typeof(TestPerson), typeof(TestPerson));

        Assert.Single(props);
        Assert.Equal(typeof(TestPerson), props[0].Property.DeclaringType);
    }
}

// Test entity types for reference resolution tests
public class TestCompany
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class TestPerson
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;

    [Reference(typeof(TestCompany))]
    public string? Company { get; set; }
}

/// <summary>
/// Simulates a projection type (like VPerson) that has the same property names
/// but lacks [Reference] attributes.
/// </summary>
public class VTestPerson
{
    public string? Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Company { get; set; }
}
