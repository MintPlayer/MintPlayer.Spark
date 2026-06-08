using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests;

public class ReferenceResolverTests
{
    // R2-H10's row-level Read hook now goes through IRowSecurity. These tests
    // exercise only the reflection-based property scan, so a stubbed gate is fine.
    private readonly ReferenceResolver _resolver = new(Substitute.For<IRowSecurity>());

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

    [Fact]
    public void GetReferenceProperties_ReturnsFreshList_OnEachCall()
    {
        // Each call returns a fresh List wrapper around the cached array. Mutating one
        // result must not affect subsequent callers — otherwise the projection-fallback
        // overload (which Adds to the result) would poison the per-Type cache.
        var first = _resolver.GetReferenceProperties(typeof(TestPerson));
        first.Add((null!, null!));

        var second = _resolver.GetReferenceProperties(typeof(TestPerson));
        second.Should().ContainSingle("each call returns a fresh List, not the cached one");
    }

    [Fact]
    public void GetReferenceProperties_RepeatCalls_ReturnSamePropertyInfoReferences()
    {
        // Cache hit: the underlying PropertyInfo references should be reference-equal
        // across calls (proves the cache is doing its job).
        var first = _resolver.GetReferenceProperties(typeof(TestPerson));
        var second = _resolver.GetReferenceProperties(typeof(TestPerson));

        ReferenceEquals(first[0].Property, second[0].Property).Should().BeTrue();
        ReferenceEquals(first[0].Attribute, second[0].Attribute).Should().BeTrue();
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
