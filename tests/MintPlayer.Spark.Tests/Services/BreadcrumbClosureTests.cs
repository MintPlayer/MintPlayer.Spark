using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Pins the static breadcrumb reference-graph analysis: which reference placeholders an entity's
/// breadcrumb depends on, the reachable depth, and cycle detection. Pure metadata — no database.
/// </summary>
public class BreadcrumbClosureTests
{
    private static EntityAttributeDefinition Scalar(string name) =>
        new() { Id = Guid.NewGuid(), Name = name, DataType = "string" };

    private static EntityAttributeDefinition Ref(string name, string targetClrType, bool isArray = false) =>
        new() { Id = Guid.NewGuid(), Name = name, DataType = "Reference", ReferenceType = targetClrType, IsArray = isArray };

    private static EntityTypeDefinition Def(string clrType, string name, string breadcrumb, params EntityAttributeDefinition[] attrs) =>
        new() { Id = Guid.NewGuid(), Name = name, ClrType = clrType, Breadcrumb = breadcrumb, Attributes = attrs };

    private static IBreadcrumbClosure ClosureFor(params EntityTypeDefinition[] defs)
    {
        var loader = Substitute.For<IModelLoader>();
        loader.GetEntityTypes().Returns(defs);
        foreach (var d in defs)
            loader.GetEntityTypeByClrType(d.ClrType).Returns(d);
        return new BreadcrumbClosure(loader);
    }

    // ParkingSpot -> Car -> Person, a 3-level chain.
    private static (EntityTypeDefinition spot, EntityTypeDefinition car, EntityTypeDefinition person) Chain()
    {
        var person = Def("T.Person", "Person", "{FirstName} {LastName}", Scalar("FirstName"), Scalar("LastName"));
        var car = Def("T.Car", "Car", "{LicensePlate} ({Driver})", Scalar("LicensePlate"), Ref("Driver", "T.Person"));
        var spot = Def("T.ParkingSpot", "ParkingSpot", "{ParkedCar} ({Coordinates})", Scalar("Coordinates"), Ref("ParkedCar", "T.Car"));
        return (spot, car, person);
    }

    [Fact]
    public void GetReferences_returns_only_reference_placeholders()
    {
        var (spot, car, person) = Chain();
        var closure = ClosureFor(spot, car, person);

        closure.GetReferences(spot).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new BreadcrumbReference("ParkedCar", "T.Car", false));
        closure.GetReferences(person).Should().BeEmpty("a scalar-only breadcrumb has no reference edges");
    }

    [Fact]
    public void GetReferences_ignores_reference_attributes_not_named_in_the_breadcrumb()
    {
        // Owner is a reference attribute but the breadcrumb only names {LicensePlate}.
        var car = Def("T.Car2", "Car", "{LicensePlate}", Scalar("LicensePlate"), Ref("Owner", "T.Person"));
        var closure = ClosureFor(car);

        closure.GetReferences(car).Should().BeEmpty();
    }

    [Fact]
    public void GetReferences_flags_array_references()
    {
        var person = Def("T.Person", "Person", "{Professions}", Ref("Professions", "T.Profession", isArray: true));
        var closure = ClosureFor(person);

        closure.GetReferences(person).Single().IsArray.Should().BeTrue();
    }

    [Fact]
    public void GetDepth_counts_reference_hops()
    {
        var (spot, car, person) = Chain();
        var closure = ClosureFor(spot, car, person);

        closure.GetDepth(person).Should().Be(1, "scalar-only");
        closure.GetDepth(car).Should().Be(2, "Car -> Person");
        closure.GetDepth(spot).Should().Be(3, "ParkingSpot -> Car -> Person");
    }

    [Fact]
    public void GetDepth_terminates_on_a_cycle()
    {
        var a = Def("T.A", "A", "{Next}", Ref("Next", "T.B"));
        var b = Def("T.B", "B", "{Prev}", Ref("Prev", "T.A"));
        var closure = ClosureFor(a, b);

        // A -> B -> (A again, cut). Depth is finite, not a stack overflow.
        closure.GetDepth(a).Should().Be(2);
    }

    [Fact]
    public void GetCycles_detects_a_reference_loop()
    {
        var a = Def("T.A", "A", "{Next}", Ref("Next", "T.B"));
        var b = Def("T.B", "B", "{Prev}", Ref("Prev", "T.A"));
        var closure = ClosureFor(a, b);

        var cycles = closure.GetCycles();

        cycles.Should().ContainSingle("the A<->B loop is reported once, not once per rotation");
        cycles[0].Should().Contain(["T.A", "T.B"]);
    }

    [Fact]
    public void GetCycles_is_empty_for_an_acyclic_chain()
    {
        var (spot, car, person) = Chain();
        var closure = ClosureFor(spot, car, person);

        closure.GetCycles().Should().BeEmpty();
    }

    [Fact]
    public void GetCycles_detects_a_direct_self_reference()
    {
        // An org-chart style breadcrumb: Person -> Manager (another Person).
        var person = Def("T.Person", "Person", "{LastName} (mgr: {Manager})", Scalar("LastName"), Ref("Manager", "T.Person"));
        var closure = ClosureFor(person);

        closure.GetCycles().Should().ContainSingle();
        // Longest simple path: the self-edge is immediately cut, so Person is the only distinct
        // level (depth 1). The resolver bounds the actual self-ref hops by MaxBreadcrumbDepth.
        closure.GetDepth(person).Should().Be(1);
    }

    [Fact]
    public void GetDepth_takes_the_longest_simple_path_through_a_diamond()
    {
        // A -> B, A -> C, B -> D, C -> D. Both branches reconverge on D (a leaf).
        var d = Def("T.D", "D", "{Name}", Scalar("Name"));
        var b = Def("T.B", "B", "{Down}", Ref("Down", "T.D"));
        var c = Def("T.C", "C", "{Down}", Ref("Down", "T.D"));
        var a = Def("T.A", "A", "{Left} {Right}", Ref("Left", "T.B"), Ref("Right", "T.C"));
        var closure = ClosureFor(a, b, c, d);

        // Longest simple path A -> B -> D (== A -> C -> D): 3 distinct levels.
        closure.GetDepth(a).Should().Be(3);
        closure.GetDepth(d).Should().Be(1);
        closure.GetCycles().Should().BeEmpty("a diamond is a DAG, not a cycle");
    }

    [Fact]
    public void GetReferences_is_cached_and_returns_an_equivalent_result_on_repeat_calls()
    {
        var (spot, car, person) = Chain();
        var closure = ClosureFor(spot, car, person);

        var first = closure.GetReferences(car);
        var second = closure.GetReferences(car);

        // Cached by ClrType: the same edge set comes back (reference-equal, since cached).
        second.Should().BeSameAs(first);
        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public void GetCycles_is_cached_across_repeat_calls()
    {
        var a = Def("T.A", "A", "{Next}", Ref("Next", "T.B"));
        var b = Def("T.B", "B", "{Prev}", Ref("Prev", "T.A"));
        var closure = ClosureFor(a, b);

        var first = closure.GetCycles();
        var second = closure.GetCycles();

        second.Should().BeSameAs(first, "cycles are computed once and memoized");
    }

    [Fact]
    public void GetDepth_bounds_a_two_node_cycle_to_the_simple_path_length()
    {
        // Re-pins the closure guarantee that drives the resolver's MaxDepth bound: even though the
        // A<->B loop is infinite at runtime, the static longest-simple-path is 2 (A -> B, B cut).
        var a = Def("T.A", "A", "{Next}", Ref("Next", "T.B"));
        var b = Def("T.B", "B", "{Prev}", Ref("Prev", "T.A"));
        var closure = ClosureFor(a, b);

        closure.GetDepth(a).Should().Be(2);
        closure.GetDepth(b).Should().Be(2);
        closure.GetCycles().Should().ContainSingle();
        closure.GetCycles()[0].Should().Contain(["T.A", "T.B"]);
    }
}
