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

        props.Should().ContainSingle();
        props[0].Property.Name.Should().Be("Company");
        props[0].Attribute.TargetType.Should().Be(typeof(TestCompany));
    }

    [Fact]
    public void GetReferenceProperties_ReturnsEmptyForTypeWithoutReferences()
    {
        _resolver.GetReferenceProperties(typeof(TestCompany)).Should().BeEmpty();
    }

    [Fact]
    public void GetReferenceProperties_ProjectionFallsBackToBaseType()
    {
        var props = _resolver.GetReferenceProperties(typeof(VTestPerson), typeof(TestPerson));

        props.Should().ContainSingle();
        props[0].Property.Name.Should().Be("Company");
        props[0].Attribute.TargetType.Should().Be(typeof(TestCompany));
        props[0].Property.DeclaringType.Should().Be(typeof(VTestPerson));
    }

    [Fact]
    public void GetReferenceProperties_ProjectionWithOwnReferenceDoesNotFallBack()
    {
        var props = _resolver.GetReferenceProperties(typeof(TestPerson), typeof(TestPerson));

        props.Should().ContainSingle();
        props[0].Property.DeclaringType.Should().Be(typeof(TestPerson));
    }
}

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

public class VTestPerson
{
    public string? Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Company { get; set; }
}
