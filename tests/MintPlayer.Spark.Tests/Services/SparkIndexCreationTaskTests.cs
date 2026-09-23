using MintPlayer.Assertions;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The base class exists to supply a call site a source generator cannot: a generator can add members
/// to a class but not statements to a hand-written constructor body. These pin that the call actually
/// happens, and that the guard the generator emits is what makes it safe.
/// </summary>
public class SparkIndexCreationTaskTests
{
    public class Widget
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>An index that declares its field options the way the generator emits them.</summary>
    private class GuardedWidgets : SparkIndexCreationTask<Widget>
    {
        public int ConfigureCalls { get; private set; }

        public GuardedWidgets()
        {
            Map = widgets => from w in widgets select new { w.Name, w.Notes };
        }

        protected override void ConfigureSparkFields()
        {
            ConfigureCalls++;

            if (!IndexesStrings.ContainsKey(nameof(Widget.Name)))
                Index(nameof(Widget.Name), FieldIndexing.Search);

            if (!IndexesStrings.ContainsKey(nameof(Widget.Notes)))
                Index(nameof(Widget.Notes), FieldIndexing.Search);
        }
    }

    /// <summary>The same index, but the constructor already made its own choice for one field.</summary>
    private class HandDeclaredWins : SparkIndexCreationTask<Widget>
    {
        public HandDeclaredWins()
        {
            Map = widgets => from w in widgets select new { w.Name, w.Notes };
            Index(nameof(Widget.Name), FieldIndexing.Exact);
        }

        protected override void ConfigureSparkFields()
        {
            if (!IndexesStrings.ContainsKey(nameof(Widget.Name)))
                Index(nameof(Widget.Name), FieldIndexing.Search);
        }
    }

    /// <summary>
    /// Declares the same field twice only when asked, to pin what the guard protects against.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The misbehaviour is opt-in at runtime, not baked into the type, and it has to stay that
    /// way.</b> <c>RavenIndexHelper</c> scans this whole assembly and deploys every index it finds, so
    /// a type that always threw from <c>CreateIndexDefinition()</c> would break every unrelated test
    /// that deploys indexes assembly-wide — which is exactly what happened when it did. Discovery
    /// constructs it with <see cref="Duplicate"/> false and gets a perfectly valid index.
    /// </remarks>
    private class OptionallyUnguardedWidgets : SparkIndexCreationTask<Widget>
    {
        public bool Duplicate { get; set; }

        public OptionallyUnguardedWidgets()
        {
            Map = widgets => from w in widgets select new { w.Name };
            Index(nameof(Widget.Name), FieldIndexing.Search);
        }

        protected override void ConfigureSparkFields()
        {
            if (Duplicate) Index(nameof(Widget.Name), FieldIndexing.Search);
        }
    }

    [Fact]
    public void The_definition_carries_the_declared_field_options_with_no_constructor_call()
    {
        var index = new GuardedWidgets();

        var definition = index.CreateIndexDefinition();

        index.ConfigureCalls.Should().Be(1, "building the definition is what invokes it");
        definition.Fields[nameof(Widget.Name)].Indexing.Should().Be(FieldIndexing.Search,
            "the whole point is that nothing in the constructor had to remember to apply this");
        definition.Fields[nameof(Widget.Notes)].Indexing.Should().Be(FieldIndexing.Search);
    }

    [Fact]
    public void Building_the_definition_twice_yields_the_same_definition()
    {
        var index = new GuardedWidgets();

        var first = index.CreateIndexDefinition();
        var second = index.CreateIndexDefinition();

        // IndexesStrings lives on the instance and survives between calls, so an unguarded override
        // would throw on the second build. A guard test in this repo already calls
        // CreateIndexDefinition() outside the deploy path, so this is not a hypothetical shape.
        index.ConfigureCalls.Should().Be(2, "each build invokes it; idempotency comes from the guard, not from caching");
        first.Equals(second).Should().BeTrue("IndexDefinition equality is value-based");
    }

    [Fact]
    public void A_field_the_constructor_declared_is_not_overwritten()
    {
        var definition = new HandDeclaredWins().CreateIndexDefinition();

        definition.Fields[nameof(Widget.Name)].Indexing.Should().Be(FieldIndexing.Exact,
            "the generator supplies defaults; an explicit hand-written declaration outranks them");
    }

    [Fact]
    public void An_unguarded_duplicate_declaration_throws_rather_than_overwriting()
    {
        var index = new OptionallyUnguardedWidgets { Duplicate = true };

        // Pinned because the design assumed the opposite. Index(string, FieldIndexing) is a
        // Dictionary.Add, not an indexer assignment, so this is a startup crash rather than silent
        // last-write-wins drift — which is why the generator emits the ContainsKey guard at all. If a
        // future RavenDB version makes this an assignment, this test fails and the guard's rationale
        // needs revisiting rather than quietly becoming decoration.
        var act = index.CreateIndexDefinition;

        act.Should().Throw<ArgumentException>(
            "declaring the same field twice is an Add on a dictionary that already has the key");
    }
}
