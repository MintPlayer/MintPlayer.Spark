using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// The name-keyed successor of the index registry (issue #279): queries resolve by declared index
/// name, and the only per-type question — which index is an entity type's default — is answered at
/// freeze time by <c>[DefaultIndex]</c> claims and the sole-viewed-index rule, never by ordinal tiebreak
/// (<c>docs/default_index_PRD.md</c>, D1–D4).
/// </summary>
public class IndexCatalogTests
{
    private class Car { public string? Id { get; set; } }
    private class Bike { public string? Id { get; set; } }
    private class Person { public string? Id { get; set; } }
    private class VehicleEntry { public string? Id { get; set; } }

    private abstract class Cars_Overview : AbstractIndexCreationTask<Car> { }
    private abstract class Cars_Search : AbstractIndexCreationTask<Car> { }
    private class VCarOverview { }
    private class VCarSearch { }
    private class VVehicle { }

    [DefaultIndex]
    private abstract class Cars_MarkedOverview : AbstractIndexCreationTask<Car> { }

    [DefaultIndex]
    private abstract class Cars_MarkedSearch : AbstractIndexCreationTask<Car> { }

    [DefaultIndex]
    private abstract class Cars_MarkedWithoutProjection : AbstractIndexCreationTask<Car> { }

    [DefaultIndex(typeof(Car))]
    private abstract class Cars_MarkedExplicitly : AbstractIndexCreationTask<Car> { }

    [DefaultIndex(typeof(Bike))]
    private abstract class Cars_ClaimingBike : AbstractIndexCreationTask<Car> { }

    private abstract class People_Overview : AbstractIndexCreationTask<Person> { }

    [DefaultIndex(typeof(Car), typeof(Bike))]
    private abstract class Vehicles_Default : SparkMultiMapIndexCreationTask<VehicleEntry> { }

    [DefaultIndex(typeof(Bike))]
    private abstract class Bikes_MultiDefault : SparkMultiMapIndexCreationTask<VehicleEntry> { }

    private abstract class Vehicles_Unmarked : SparkMultiMapIndexCreationTask<VehicleEntry> { }

    [DefaultIndex]
    private abstract class Vehicles_BareMarker : SparkMultiMapIndexCreationTask<VehicleEntry> { }

    [DefaultIndex(typeof(Car))]
    private abstract class Vehicles_RavenBase : AbstractMultiMapIndexCreationTask<VehicleEntry> { }

    [DefaultIndex(typeof(Car))]
    private abstract class Vehicles_Js : AbstractJavaScriptIndexCreationTask { }

    private static IndexCatalog Frozen(params (Type Index, Type? Projection)[] indexes)
    {
        var catalog = Unfrozen(indexes);
        catalog.Freeze();
        return catalog;
    }

    private static IndexCatalog Unfrozen(params (Type Index, Type? Projection)[] indexes)
    {
        var catalog = new IndexCatalog();
        foreach (var (index, _) in indexes)
            catalog.RegisterIndex(index);
        foreach (var (index, projection) in indexes)
        {
            if (projection is not null)
                catalog.RegisterProjection(projection, index);
        }
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

        var entry = catalog.GetDefaultFor(typeof(Car));
        entry.Should().NotBeNull();
        entry!.IndexName.Should().Be("Cars_Overview");
        entry.IsDefault.Should().BeTrue();
        entry.DefaultFor.Should().BeEquivalentTo([typeof(Car)]);
        entry.ClaimedTypes.Should().BeEmpty("an implicit default claims nothing");
        catalog.GetByIndexName("Cars_Search")!.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void With_multiple_projection_bearing_indexes_the_marked_one_wins()
    {
        var catalog = Frozen(
            (typeof(Cars_MarkedOverview), typeof(VCarOverview)),
            (typeof(Cars_Search), typeof(VCarSearch)));

        catalog.GetDefaultFor(typeof(Car))!.IndexName.Should().Be("Cars_MarkedOverview");
    }

    [Fact]
    public void A_single_map_marker_naming_its_own_type_is_the_same_as_a_bare_one()
    {
        var catalog = Frozen(
            (typeof(Cars_MarkedExplicitly), null),
            (typeof(Cars_Search), typeof(VCarSearch)));

        var entry = catalog.GetDefaultFor(typeof(Car))!;
        entry.IndexName.Should().Be("Cars_MarkedExplicitly");
        entry.ClaimedTypes.Should().BeEquivalentTo([typeof(Car)]);
    }

    [Fact]
    public void Multiple_projection_bearing_indexes_without_a_marker_fail_freeze_naming_the_candidates()
    {
        var catalog = Unfrozen(
            (typeof(Cars_Overview), typeof(VCarOverview)),
            (typeof(Cars_Search), typeof(VCarSearch)));

        var act = catalog.Freeze;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cars_Overview*")
            .And.WithMessage("*Cars_Search*")
            .And.WithMessage("*[DefaultIndex]*");
    }

    [Fact]
    public void Two_markers_over_one_collection_fail_freeze()
    {
        var catalog = Unfrozen(
            (typeof(Cars_MarkedOverview), typeof(VCarOverview)),
            (typeof(Cars_MarkedSearch), typeof(VCarSearch)));

        var act = catalog.Freeze;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*2 indexes claim [DefaultIndex] for entity type Car*");
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().NotContain("IsDefault", "GenerateIndexAttribute.IsDefault no longer exists (D5)");
    }

    // ---- A viewless default (D3, SP4) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_marked_index_without_a_view_is_the_default(bool withViewedSibling)
    {
        var catalog = withViewedSibling
            ? Frozen((typeof(Cars_MarkedWithoutProjection), null), (typeof(Cars_Overview), typeof(VCarOverview)))
            : Frozen((typeof(Cars_MarkedWithoutProjection), null));

        var entry = catalog.GetDefaultFor(typeof(Car))!;
        entry.IndexName.Should().Be("Cars_MarkedWithoutProjection");
        entry.ProjectionType.Should().BeNull("a viewless default shapes nothing; its queries return entity documents");
        catalog.GetByIndexName("Cars_Overview")?.IsDefault.Should().BeFalse("the claim beats the implicit viewed index");
    }

    [Fact]
    public void An_unmarked_index_without_a_view_is_never_a_default()
    {
        var catalog = Frozen((typeof(Cars_Overview), null));

        catalog.GetDefaultFor(typeof(Car)).Should().BeNull("only an index with a view is a default implicitly");
        catalog.GetByIndexName("Cars_Overview").Should().NotBeNull("the entry itself stays resolvable by name");
    }

    [Fact]
    public void Defaults_are_per_entity_type()
    {
        var catalog = Frozen(
            (typeof(Cars_Overview), typeof(VCarOverview)),
            (typeof(People_Overview), typeof(VCarSearch)));

        catalog.GetDefaultFor(typeof(Car))!.IndexName.Should().Be("Cars_Overview");
        catalog.GetDefaultFor(typeof(Person))!.IndexName.Should().Be("People_Overview");
    }

    // ---- Multi-map defaults (D1, D4) ----

    [Fact]
    public void A_multi_map_is_the_default_of_exactly_the_types_it_lists()
    {
        var catalog = Frozen(
            (typeof(Vehicles_Default), typeof(VVehicle)),
            (typeof(People_Overview), typeof(VCarSearch)));

        var entry = catalog.GetByIndexName("Vehicles_Default")!;
        entry.CollectionType.Should().BeNull("a multi-map names no collection of its own");
        entry.ClaimedTypes.Should().BeEquivalentTo([typeof(Car), typeof(Bike)]);
        entry.DefaultFor.Should().BeEquivalentTo([typeof(Car), typeof(Bike)]);
        entry.EntityTypes.Should().BeEquivalentTo([typeof(Car), typeof(Bike)]);
        catalog.GetDefaultFor(typeof(Car)).Should().BeSameAs(entry);
        catalog.GetDefaultFor(typeof(Bike)).Should().BeSameAs(entry);
        catalog.GetDefaultFor(typeof(Person))!.IndexName.Should().Be("People_Overview");
    }

    [Fact]
    public void A_multi_map_default_filters_by_collection_only_for_the_types_it_claims()
    {
        var catalog = Frozen((typeof(Vehicles_Default), null), (typeof(Cars_Overview), typeof(VCarOverview)), (typeof(People_Overview), null));

        var multiMap = catalog.GetByIndexName("Vehicles_Default")!;
        multiMap.FiltersByCollection(typeof(Car)).Should().BeTrue();
        multiMap.FiltersByCollection(typeof(Bike)).Should().BeTrue();
        multiMap.FiltersByCollection(typeof(Person)).Should().BeFalse("Person is not listed");

        catalog.GetByIndexName("Cars_Overview")!.FiltersByCollection(typeof(Car))
            .Should().BeFalse("a single-map holds only its own collection");
    }

    [Fact]
    public void A_multi_map_claim_beats_an_implicit_viewed_single_map()
    {
        var catalog = Frozen(
            (typeof(Vehicles_Default), typeof(VVehicle)),
            (typeof(Cars_Overview), typeof(VCarOverview)));

        catalog.GetDefaultFor(typeof(Car))!.IndexName.Should().Be("Vehicles_Default");
        catalog.GetByIndexName("Cars_Overview")!.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void An_unmarked_multi_map_is_nobodys_default()
    {
        var catalog = Frozen((typeof(Vehicles_Unmarked), typeof(VVehicle)));

        catalog.GetByIndexName("Vehicles_Unmarked")!.IsDefault.Should().BeFalse();
        catalog.GetDefaultFor(typeof(Car)).Should().BeNull();
        catalog.GetDefaultFor(typeof(VehicleEntry)).Should().BeNull("the reduce result is not a mapped collection");
    }

    [Theory]
    [InlineData(typeof(Cars_MarkedWithoutProjection))]
    [InlineData(typeof(Bikes_MultiDefault))]
    public void A_multi_map_and_another_index_claiming_the_same_type_fail_freeze(Type rival)
    {
        var catalog = Unfrozen((typeof(Vehicles_Default), null), (rival, null));

        var act = catalog.Freeze;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*2 indexes claim [DefaultIndex]*")
            .And.WithMessage("*Vehicles_Default*")
            .And.WithMessage($"*{rival.Name}*");
    }

    [Fact]
    public void A_bare_marker_on_a_multi_map_fails_freeze()
    {
        var act = Unfrozen((typeof(Vehicles_BareMarker), null)).Freeze;

        act.Should().Throw<InvalidOperationException>().WithMessage("*lists no entity type*[DefaultIndex(typeof(A), typeof(B))]*");
    }

    [Fact]
    public void A_multi_map_default_must_derive_from_the_Spark_base()
    {
        var act = Unfrozen((typeof(Vehicles_RavenBase), null)).Freeze;

        act.Should().Throw<InvalidOperationException>().WithMessage("*SparkMultiMapIndexCreationTask*");
    }

    [Fact]
    public void A_single_map_marker_naming_another_type_fails_freeze()
    {
        var act = Unfrozen((typeof(Cars_ClaimingBike), null)).Freeze;

        act.Should().Throw<InvalidOperationException>().WithMessage("*single-map index is the default of the type it maps, Car*");
    }

    [Fact]
    public void A_marker_on_a_JavaScript_index_fails_freeze()
    {
        var act = Unfrozen((typeof(Vehicles_Js), null)).Freeze;

        act.Should().Throw<InvalidOperationException>().WithMessage("*JavaScript index is never a default*");
    }

    // ---- Lifecycle ----

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

        var act = () => catalog.GetDefaultFor(typeof(Car));
        act.Should().Throw<InvalidOperationException>().WithMessage("*frozen*");
    }
}
