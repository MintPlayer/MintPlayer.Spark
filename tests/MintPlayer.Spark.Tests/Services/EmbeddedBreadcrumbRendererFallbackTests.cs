using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services.Breadcrumb;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The branches of <see cref="EmbeddedBreadcrumbRenderer"/> that <see cref="EmbeddedBreadcrumbRendererTests"/>
/// does not reach: the <c>[Breadcrumb]</c>-marker fallback, embedded complex tokens (#273), the
/// recursion cap, and the reference-id shapes.
/// </summary>
public class EmbeddedBreadcrumbRendererFallbackTests
{
    public sealed class Marked
    {
        [Breadcrumb, IgnoreProperty] public string? Crumb { get; set; }
    }

    public sealed class MarkedComplex
    {
        [Breadcrumb, IgnoreProperty] public Address? Where { get; set; }
    }

    public sealed class Unmarked
    {
        public string? Name { get; set; }
    }

    public sealed class Address
    {
        public string City { get; set; } = "";
        public string Street { get; set; } = "";
    }

    public sealed class Order
    {
        public string OrderNo { get; set; } = "";
        public Address? Ship { get; set; }
        public object? Owners { get; set; }
    }

    /// <summary>A type whose breadcrumb names itself, so rendering never bottoms out on its own.</summary>
    public sealed class Loop
    {
        public Loop? Next { get; set; }
    }

    private static readonly BreadcrumbResult Empty = new(new Dictionary<string, string>(StringComparer.Ordinal));

    private static EntityTypeDefinition Def<T>(string? breadcrumb, params EntityAttributeDefinition[] attrs) =>
        new() { Id = Guid.NewGuid(), Name = typeof(T).Name, ClrType = typeof(T).FullName!, Breadcrumb = breadcrumb, Attributes = attrs };

    private static EntityAttributeDefinition AsDetail<T>(string name) =>
        new() { Id = Guid.NewGuid(), Name = name, DataType = "AsDetail", AsDetailType = typeof(T).FullName };

    private static EntityAttributeDefinition Ref(string name) =>
        new() { Id = Guid.NewGuid(), Name = name, DataType = "Reference", ReferenceType = "Demo.Owner" };

    // --- marker fallback -----------------------------------------------------

    [Fact]
    public void Without_a_template_the_marked_property_is_the_breadcrumb()
    {
        EmbeddedBreadcrumbRenderer.Render(new Marked { Crumb = "Marked!" }, def: null, Empty, ", ")
            .Should().Be("Marked!");
    }

    [Fact]
    public void A_null_marked_value_renders_empty_rather_than_null()
    {
        // Null would mean "declares no breadcrumb"; this type declares one that is currently empty.
        EmbeddedBreadcrumbRenderer.Render(new Marked(), def: null, Empty, ", ").Should().BeEmpty();
    }

    [Fact]
    public void A_type_with_neither_template_nor_marker_declares_no_breadcrumb()
    {
        EmbeddedBreadcrumbRenderer.Render(new Unmarked { Name = "x" }, Def<Unmarked>(""), Empty, ", ").Should().BeNull();
    }

    [Fact]
    public void A_complex_marked_value_renders_through_its_own_definition()
    {
        var entity = new MarkedComplex { Where = new Address { City = "Ghent", Street = "Main" } };
        var addressDef = Def<Address>("{Street}, {City}");

        EmbeddedBreadcrumbRenderer.Render(entity, def: null, Empty, ", ",
                defByClrType: name => name == typeof(Address).FullName ? addressDef : null)
            .Should().Be("Main, Ghent");
    }

    [Fact]
    public void A_complex_marked_value_with_no_breadcrumb_of_its_own_renders_empty()
    {
        var entity = new MarkedComplex { Where = new Address { City = "Ghent" } };

        EmbeddedBreadcrumbRenderer.Render(entity, def: null, Empty, ", ").Should().BeEmpty();
    }

    // --- embedded complex tokens (#273) ---------------------------------------

    [Fact]
    public void An_embedded_token_renders_the_child_breadcrumb_not_its_type_name()
    {
        var order = new Order { OrderNo = "O-1", Ship = new Address { City = "Ghent", Street = "Main" } };
        var def = Def<Order>("{OrderNo} to {Ship}", new() { Id = Guid.NewGuid(), Name = "OrderNo", DataType = "string" }, AsDetail<Address>("Ship"));
        var addressDef = Def<Address>("{City}");

        EmbeddedBreadcrumbRenderer.Render(order, def, Empty, ", ", name => name == typeof(Address).FullName ? addressDef : null)
            .Should().Be("O-1 to Ghent");
    }

    [Fact]
    public void An_embedded_token_with_no_child_renders_nothing_for_it()
    {
        var def = Def<Order>("{OrderNo}{Ship}", new() { Id = Guid.NewGuid(), Name = "OrderNo", DataType = "string" }, AsDetail<Address>("Ship"));

        EmbeddedBreadcrumbRenderer.Render(new Order { OrderNo = "O-2" }, def, Empty, ", ").Should().Be("O-2");
    }

    [Fact]
    public void Recursion_is_capped_rather_than_following_a_cycle_forever()
    {
        var loopDef = Def<Loop>("<{Next}>", AsDetail<Loop>("Next"));
        var chain = new Loop();
        for (var i = 0; i < 20; i++) chain = new Loop { Next = chain };

        var rendered = EmbeddedBreadcrumbRenderer.Render(chain, loopDef, Empty, ", ", _ => loopDef);

        rendered.Should().Be(string.Concat(Enumerable.Repeat("<", 8)) + string.Concat(Enumerable.Repeat(">", 8)),
            "the eighth level renders empty instead of descending further");
    }

    // --- reference id shapes ----------------------------------------------------

    public static TheoryData<object?, string> ReferenceValues() => new()
    {
        { null, "" },
        { "", "" },
        { "owners/1", "Alice" },
        { new[] { "owners/1", null, "", "owners/2" }, "Alice + Bob" },
        { 42, "" },
    };

    [Theory]
    [MemberData(nameof(ReferenceValues))]
    public void A_reference_token_reads_ids_from_every_shape(object? owners, string expected)
    {
        var def = Def<Order>("{Owners}", Ref("Owners"));
        var result = new BreadcrumbResult(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["owners/1"] = "Alice",
            ["owners/2"] = "Bob",
        });

        EmbeddedBreadcrumbRenderer.Render(new Order { Owners = owners }, def, result, " + ").Should().Be(expected);
    }

    [Fact]
    public void A_token_naming_no_property_renders_empty()
    {
        EmbeddedBreadcrumbRenderer.Render(new Order { OrderNo = "O-3" }, Def<Order>("[{Missing}]"), Empty, ", ")
            .Should().Be("[]");
    }
}
