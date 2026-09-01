using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// The name-keyed successor of the index registry (issue #279): queries resolve by declared index
/// name, and the only per-collection question — which projection shapes the model file — is answered
/// by <c>[DefaultIndex]</c> at freeze time, never by ordinal tiebreak.
/// </summary>
public class IndexCatalogTests
{
    private class Car { public string? Id { get; set; } }
    private class Person { public string? Id { get; set; } }

    private abstract class Cars_Overview : AbstractIndexCreationTask<Car> { }
    private abstract class Cars_Search : AbstractIndexCreationTask<Car> { }
    private class VCarOverview { }
    private class VCarSearch { }

    [DefaultIndex]
    private abstract class Cars_MarkedOverview : AbstractIndexCreationTask<Car> { }

    [DefaultIndex]
    private abstract class Cars_MarkedSearch : AbstractIndexCreationTask<Car> { }

    [DefaultIndex]
    private abstract class Cars_MarkedWithoutProjection : AbstractIndexCreationTask<Car> { }

    private abstract class People_Overview : AbstractIndexCreationTask<Person> { }

    private static IndexCatalog Frozen(params (Type Index, Type? Projection)[] indexes)
    {
        var catalog = new IndexCatalog();
        foreach (var (index, _) in indexes)
            catalog.RegisterIndex(index);
        foreach (var (index, projection) in indexes)
        {
            if (projection is not null)
                catalog.RegisterProjection(projection, index);
        }
        catalog.Freeze();
        return catalog;
    }

    [Fact]
    public void Lookup_by_index_name_is_case_insensitive()
    {
        var catalog = Frozen((typeof(Cars_Overview), typeof(VCarOverview)));

        catalog.GetByIndexName("cars_overview").Should().NotBeNull();
        catalog.GetByIndexName("Cars_Overview")!.IndexType.Should().Be(typeof(Cars_Overview));
        catalog.GetByIndexName("Cars_Unknown").Should().BeNull();
    }

    [Fact]
    public void Registering_the_same_index_type_twice_is_idempotent()
    {
        var catalog = new IndexCatalog();
        catalog.RegisterIndex(typeof(Cars_Overview));
        catalog.RegisterIndex(typeof(Cars_Overview));
        catalog.Freeze();

        catalog.GetAllEntries().Should().ContainSingle();
    }

    [Fact]
    public void A_single_projection_bearing_index_is_the_implicit_default()
    {
        var catalog = Frozen(
            (typeof(Cars_Overview), typeof(VCarOverview)),
            (typeof(Cars_Search), null));

        var entry = catalog.GetDefaultForCollectionType(typeof(Car));
        entry.Should().NotBeNull();
        entry!.IndexName.Should().Be("Cars_Overview");
        entry.IsDefault.Should().BeTrue();
        catalog.GetByIndexName("Cars_Search")!.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void With_multiple_projection_bearing_indexes_the_marked_one_wins()
    {
        var catalog = Frozen(
            (typeof(Cars_MarkedOverview), typeof(VCarOverview)),
            (typeof(Cars_Search), typeof(VCarSearch)));

        catalog.GetDefaultForCollectionType(typeof(Car))!.IndexName.Should().Be("Cars_MarkedOverview");
    }

    [Fact]
    public void Multiple_projection_bearing_indexes_without_a_marker_fail_freeze_naming_the_candidates()
    {
        var catalog = new IndexCatalog();
        catalog.RegisterIndex(typeof(Cars_Overview));
        catalog.RegisterIndex(typeof(Cars_Search));
        catalog.RegisterProjection(typeof(VCarOverview), typeof(Cars_Overview));
        catalog.RegisterProjection(typeof(VCarSearch), typeof(Cars_Search));

        var act = catalog.Freeze;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cars_Overview*")
            .And.WithMessage("*Cars_Search*")
            .And.WithMessage("*[DefaultIndex]*");
    }

    [Fact]
    public void Two_markers_over_one_collection_fail_freeze()
    {
        var catalog = new IndexCatalog();
        catalog.RegisterIndex(typeof(Cars_MarkedOverview));
        catalog.RegisterIndex(typeof(Cars_MarkedSearch));
        catalog.RegisterProjection(typeof(VCarOverview), typeof(Cars_MarkedOverview));
        catalog.RegisterProjection(typeof(VCarSearch), typeof(Cars_MarkedSearch));

        var act = catalog.Freeze;
        act.Should().Throw<InvalidOperationException>().WithMessage("*2 carry [DefaultIndex]*");
    }

    [Fact]
    public void A_marker_on_a_projection_less_index_fails_freeze()
    {
        var catalog = new IndexCatalog();
        catalog.RegisterIndex(typeof(Cars_MarkedWithoutProjection));

        var act = catalog.Freeze;
        act.Should().Throw<InvalidOperationException>().WithMessage("*no*[FromIndex]*");
    }

    [Fact]
    public void A_projection_less_collection_has_no_default()
    {
        var catalog = Frozen((typeof(Cars_Overview), null));

        catalog.GetDefaultForCollectionType(typeof(Car)).Should().BeNull();
        catalog.GetByIndexName("Cars_Overview").Should().NotBeNull("the entry itself stays resolvable by name");
    }

    [Fact]
    public void Defaults_are_per_collection_type()
    {
        var catalog = Frozen(
            (typeof(Cars_Overview), typeof(VCarOverview)),
            (typeof(People_Overview), typeof(VCarSearch)));

        catalog.GetDefaultForCollectionType(typeof(Car))!.IndexName.Should().Be("Cars_Overview");
        catalog.GetDefaultForCollectionType(typeof(Person))!.IndexName.Should().Be("People_Overview");
    }

    [Fact]
    public void Registration_after_freeze_throws()
    {
        var catalog = Frozen((typeof(Cars_Overview), typeof(VCarOverview)));

        var act = () => catalog.RegisterIndex(typeof(Cars_Search));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Default_resolution_before_freeze_throws()
    {
        var catalog = new IndexCatalog();
        catalog.RegisterIndex(typeof(Cars_Overview));

        var act = () => catalog.GetDefaultForCollectionType(typeof(Car));
        act.Should().Throw<InvalidOperationException>().WithMessage("*frozen*");
    }
}
