using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Tests.Indexes;

/// <summary>
/// <see cref="SparkMapScanner"/> over maps RavenDB actually rendered (<c>CreateIndexDefinition()</c>, no
/// server): the SP1 shapes of <c>docs/default_index_PRD.md</c> §7. RavenDB normalizes a C# map before Spark
/// sees it, so these tests render real index classes rather than hand-writing map strings — a hand-written
/// string would test the scanner against a shape RavenDB never produces. The server-side compile of the
/// rewritten maps is in <c>MultiMapDefaultIndexTests</c>.
/// </summary>
public class SparkMapScannerTests
{
    public class SmCar
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public string Owner { get; set; } = "";
        public SmPart[] Parts { get; set; } = [];
        public bool Active { get; set; } = true;
    }

    public class SmBike
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public string Owner { get; set; } = "";
    }

    public class SmPart
    {
        public string Code { get; set; } = "";
    }

    public class SmEntry
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public string Owner { get; set; } = "";
        public object? Parts { get; set; }
        public string? SparkCollection { get; set; }
    }

    /// <summary>Unmarked, so <c>CreateIndexDefinition</c> leaves the maps as RavenDB rendered them.</summary>
    public class SmPlainAndLet : SparkMultiMapIndexCreationTask<SmEntry>
    {
        public SmPlainAndLet()
        {
            AddMap<SmCar>(cars => from c in cars select new { c.Name, c.Owner });
            AddMap<SmBike>(bikes => from b in bikes
                                    let upper = b.Name.ToUpper()
                                    select new { Name = upper, b.Owner });
        }
    }

    public class SmNestedAndFanOut : SparkMultiMapIndexCreationTask<SmEntry>
    {
        public SmNestedAndFanOut()
        {
            // Nested anonymous objects, and a string literal holding the characters the scanner balances on.
            AddMap<SmCar>(cars => from c in cars
                                  where c.Active
                                  select new { c.Name, Parts = c.Parts.Select(p => new { p.Code, Label = "})\"(" + p.Code }) });
            // Fan-out: query syntax over a nested collection renders as SelectMany with a result selector.
            AddMap<SmBike>(bikes => from b in bikes
                                    from ch in b.Name
                                    select new { b.Name, Parts = ch });
        }
    }

    public class SmMethodSyntaxAndWhereAfterLet : SparkMultiMapIndexCreationTask<SmEntry>
    {
        public SmMethodSyntaxAndWhereAfterLet()
        {
            // Method syntax with a typed result: rendered as an anonymous object all the same.
            AddMap<SmCar>(cars => cars.Select(c => new SmEntry { Name = c.Name, Owner = c.Owner }));
            AddMap<SmBike>(bikes => from b in bikes
                                    let o = b.Owner
                                    where o != null
                                    select new { b.Name, Owner = o });
        }
    }

    // Every map of one index returns the same fields: RavenIndexHelperSmokeTests deploys every index in this
    // assembly, and RavenDB refuses to compile a multi-map whose maps differ ("must return identical types").
    public class SmConditional : SparkMultiMapIndexCreationTask<SmEntry>
    {
        public SmConditional()
        {
            AddMap<SmCar>(cars => from c in cars
                                  select c.Active
                                      ? new { Name = c.Name, Owner = c.Owner }
                                      : new { Name = "", Owner = "" });
            AddMap<SmBike>(bikes => from b in bikes select new { b.Name, b.Owner });
        }
    }

    public class SmHandWritten : SparkMultiMapIndexCreationTask<SmEntry>
    {
        public SmHandWritten()
        {
            // The author emits the discriminator (PRD D4's fallback A shape): left alone.
            AddMap<SmCar>(cars => from c in cars select new { c.Name, SparkCollection = "SmCars" });
            AddMap<SmBike>(bikes => from b in bikes select new { b.Name, SparkCollection = "SmBikes" });
        }
    }

    [DefaultIndex(typeof(SmCar), typeof(SmBike))]
    public class SmMarked : SparkMultiMapIndexCreationTask<SmEntry>
    {
        public SmMarked()
        {
            AddMap<SmCar>(cars => from c in cars select new { c.Name, c.Owner });
            AddMap<SmBike>(bikes => from b in bikes let upper = b.Name.ToUpper() select new { Name = upper, b.Owner });
        }
    }

    public class SmTruck
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public bool Active { get; set; } = true;
    }

    /// <summary>
    /// Valid as constructed — test hosts scan this assembly and deploy what they find, so a fixture that
    /// throws from <c>CreateIndexDefinition</c> would break unrelated tests. The unreadable map is opt-in.
    /// Claims its own type for the same reason: two claims on one type fail every host's catalog.
    /// </summary>
    [DefaultIndex(typeof(SmTruck))]
    public class SmMarkedConditional : SparkMultiMapIndexCreationTask<SmEntry>
    {
        public SmMarkedConditional()
        {
            AddMap<SmTruck>(trucks => from t in trucks select new { t.Name });
        }

        public SmMarkedConditional WithConditionalMap()
        {
            AddMap<SmTruck>(trucks => from t in trucks select t.Active ? new { t.Name } : new { Name = "" });
            return this;
        }
    }

    private static string MapOver(AbstractIndexCreationTask index, string collection)
        => index.CreateIndexDefinition().Maps.Single(m => SparkMapScanner.CollectionOf(m) == collection);

    public static TheoryData<string, string, string[]> Shapes() => new()
    {
        { nameof(SmPlainAndLet), "SmCars", ["Name", "Owner"] },
        { nameof(SmPlainAndLet), "SmBikes", ["Name", "Owner"] },
        { nameof(SmNestedAndFanOut), "SmCars", ["Name", "Parts"] },
        { nameof(SmNestedAndFanOut), "SmBikes", ["Name", "Parts"] },
        { nameof(SmMethodSyntaxAndWhereAfterLet), "SmCars", ["Name", "Owner"] },
        { nameof(SmMethodSyntaxAndWhereAfterLet), "SmBikes", ["Name", "Owner"] },
    };

    private static AbstractIndexCreationTask Index(string name) => name switch
    {
        nameof(SmPlainAndLet) => new SmPlainAndLet(),
        nameof(SmNestedAndFanOut) => new SmNestedAndFanOut(),
        nameof(SmMethodSyntaxAndWhereAfterLet) => new SmMethodSyntaxAndWhereAfterLet(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Reads_the_collection_and_the_emitted_fields(string index, string collection, string[] fields)
    {
        var map = MapOver(Index(index), collection);

        SparkMapScanner.CollectionOf(map).Should().Be(collection);
        SparkMapScanner.EmittedFields(map).Should().BeEquivalentTo(fields);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Injects_the_discriminator_into_the_final_result_object(string index, string collection, string[] fields)
    {
        var map = MapOver(Index(index), collection);

        var rewritten = SparkMapScanner.InjectCollectionField(map);

        rewritten.Should().Contain($"SparkCollection = \"{collection}\"");
        SparkMapScanner.EmittedFields(rewritten).Should().BeEquivalentTo([SparkCollectionRow.FieldName, .. fields],
            because: "the field joins the outermost result object, never a nested one");
        SparkMapScanner.CollectionOf(rewritten).Should().Be(collection);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_rewrite_is_idempotent(string index, string collection, string[] fields)
    {
        _ = fields;
        var once = SparkMapScanner.InjectCollectionField(MapOver(Index(index), collection));

        SparkMapScanner.InjectCollectionField(once).Should().Be(once);
    }

    [Fact]
    public void A_map_that_already_emits_the_field_is_left_alone()
    {
        var map = MapOver(new SmHandWritten(), "SmBikes");

        SparkMapScanner.EmitsCollectionField(map).Should().BeTrue();
        SparkMapScanner.InjectCollectionField(map).Should().Be(map);
    }

    [Fact]
    public void A_conditional_result_body_is_refused_with_the_manual_fix()
    {
        var map = MapOver(new SmConditional(), "SmCars");

        var act = () => SparkMapScanner.InjectCollectionField(map);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*does not return 'new { … }'*")
            .And.WithMessage("*Emit it yourself*SparkCollection = \"<Collection>\"*");
        SparkMapScanner.EmittedFields(map).Should().BeNull("an unreadable shape has no known surface");
    }

    [Theory]
    [InlineData("map('Cars', function (c) { return { Name: c.Name }; })")]
    [InlineData("from c in docs.Cars select new { c.Name }")]
    public void Maps_that_are_not_rendered_CSharp_chains_are_not_read(string map)
    {
        SparkMapScanner.EmittedFields(map).Should().BeNull();
        var act = () => SparkMapScanner.InjectCollectionField(map);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void A_quoted_collection_name_is_read_and_reused_verbatim()
    {
        const string map = "docs[\"Line-Items\"].Select(x => new {\n    Name = x.Name\n})";

        SparkMapScanner.CollectionOf(map).Should().Be("Line-Items");
        SparkMapScanner.InjectCollectionField(map).Should().Contain("SparkCollection = \"Line-Items\",");
    }

    [Fact]
    public void A_marked_multi_map_gets_the_discriminator_in_every_map()
    {
        var maps = new SmMarked().CreateIndexDefinition().Maps;

        maps.Should().HaveCount(2);
        maps.Should().OnlyContain(m => SparkMapScanner.EmitsCollectionField(m));
        maps.Single(m => SparkMapScanner.CollectionOf(m) == "SmCars").Should().Contain("SparkCollection = \"SmCars\"");
        maps.Single(m => SparkMapScanner.CollectionOf(m) == "SmBikes").Should().Contain("SparkCollection = \"SmBikes\"");
    }

    [Fact]
    public void An_unmarked_multi_map_is_deployed_as_declared()
    {
        new SmPlainAndLet().CreateIndexDefinition().Maps
            .Should().OnlyContain(m => !SparkMapScanner.EmitsCollectionField(m));
    }

    [Fact]
    public void A_marked_multi_map_with_an_unreadable_map_fails_at_definition_time()
    {
        var act = () => new SmMarkedConditional().WithConditionalMap().CreateIndexDefinition();

        act.Should().Throw<NotSupportedException>().WithMessage("*SparkCollection*");
    }
}
