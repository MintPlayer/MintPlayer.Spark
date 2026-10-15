using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Tests._Infrastructure;
using System.Text.Json;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #284 — a query's <c>columnOrder</c> reorders and hides its grid columns inside
/// <c>BuildColumns</c>, the one place every wire surface takes its columns from (R3, D8). It only
/// narrows and reorders the query surface (R4), and an absent list changes nothing (R2).
/// </summary>
public class QueryResultProjectorColumnOrderTests
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static EntityAttributeDefinition Attribute(string name, int order, EShowedOn showedOn = EShowedOn.Query | EShowedOn.PersistentObject) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Label = TranslatedString.Create(name),
        Order = order,
        ShowedOn = showedOn,
    };

    /// <summary>
    /// Name/Account/Owner/Size on the surface; Notes is PO-only. Owner and Size share an order, so the
    /// (order, name) tiebreak is observable, and the declaration order (Size before Owner) differs from
    /// the name order.
    /// </summary>
    private static EntityTypeDefinition Definition() => new()
    {
        Id = Guid.Parse("eeee1111-1111-1111-1111-eeee11111111"),
        Name = "Repository",
        Attributes =
        [
            Attribute("Account", 1),
            Attribute("Name", 2),
            Attribute("Size", 3),
            Attribute("Owner", 3),
            Attribute("Notes", 4, EShowedOn.PersistentObject),
        ],
    };

    private static SparkQuery Query(string[]? columnOrder) => new()
    {
        Id = Guid.Parse("eeee2222-2222-2222-2222-eeee22222222"),
        Name = "GetRepositories",
        Source = "Database.Repositories",
        ColumnOrder = columnOrder,
    };

    private static string[] Names(EntityTypeDefinition definition, string[]? columnOrder)
        => [.. QueryResultProjector.BuildColumns(definition, Query(columnOrder)).Select(c => c.Name)];

    [Fact]
    public void Without_a_column_order_the_columns_are_the_surface_by_order_unchanged()
    {
        // R2: the baseline keeps its stable sort by order alone, so Size stays ahead of Owner.
        var definition = Definition();

        Names(definition, null).Should().Equal("Account", "Name", "Size", "Owner");

        var withQuery = JsonSerializer.Serialize(QueryResultProjector.BuildColumns(definition, Query(null)), WireOptions);
        var withoutQuery = JsonSerializer.Serialize(QueryResultProjector.BuildColumns(definition), WireOptions);
        withQuery.Should().Be(withoutQuery);
    }

    [Fact]
    public void Listed_columns_are_drawn_in_list_order()
        => Names(Definition(), ["Name", "Owner", "Size", "Account"]).Should().Equal("Name", "Owner", "Size", "Account");

    [Fact]
    public void A_bang_entry_hides_the_column()
        => Names(Definition(), ["Name", "Owner", "Size", "!Account"]).Should().Equal("Name", "Owner", "Size");

    [Fact]
    public void Unlisted_surface_attributes_are_appended_by_order_then_name()
        // Owner and Size tie on order 3: name breaks the tie, unlike the unlisted baseline.
        => Names(Definition(), ["Name"]).Should().Equal("Name", "Account", "Owner", "Size");

    [Fact]
    public void An_entry_for_an_attribute_off_the_query_surface_is_ignored()
        // R4: Notes is PO-only. Naming it does not surface it.
        => Names(Definition(), ["Notes", "Name"]).Should().Equal("Name", "Account", "Owner", "Size");

    [Fact]
    public void An_entry_for_a_rights_denied_attribute_is_ignored()
    {
        // The caller's surface arrives with Query-denied attributes already removed
        // (AttributeRightsEnforcement.ForQueryAsync), so a listed name it lacks matches nothing.
        var definition = Definition();
        definition.Attributes = [.. definition.Attributes.Where(a => a.Name != "Account")];

        Names(definition, ["Account", "Name"]).Should().Equal("Name", "Owner", "Size");
    }

    [Fact]
    public void Entries_match_attribute_names_case_insensitively()
        => Names(Definition(), ["name", "!account"]).Should().Equal("Name", "Owner", "Size");

    [Fact]
    public void A_hidden_column_stays_on_the_query_surface_for_sort_and_filter()
    {
        // D7: presentation only. The sort/filter/search/distinct gates key on ShowedOn.Query, so a hidden
        // column is still sortable and filterable through them.
        var definition = Definition();
        var query = Query(["Name", "!Account"]);

        QueryResultProjector.BuildColumns(definition, query).Select(c => c.Name).Should().NotContain("Account");
        var account = ColumnCapabilities.FindQuerySurfaceAttribute(definition, "Account");
        account.Should().NotBeNull();
        ColumnCapabilities.CanSort(account!, query).Should().BeTrue();
        ColumnCapabilities.CanFilter(account!, query).Should().BeTrue();
    }

    [Fact]
    public void Only_the_drawn_columns_carry_values()
    {
        // #264 G-Q4 still holds: a hidden column ships no value.
        var columns = QueryResultProjector.BuildColumns(Definition(), Query(["Owner", "Name", "!Account", "!Size"]));
        var row = new PersistentObject
        {
            Id = "Repositories/1",
            Name = "spark",
            ObjectTypeId = Guid.Parse("eeee1111-1111-1111-1111-eeee11111111"),
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Account", DataType = "string", Value = "mintplayer" },
                new PersistentObjectAttribute { Name = "Name", DataType = "string", Value = "spark" },
                new PersistentObjectAttribute { Name = "Size", DataType = "string", Value = "large" },
                new PersistentObjectAttribute { Name = "Owner", DataType = "string", Value = "pieter" },
            ],
        };

        var item = QueryResultProjector.ToItems(TestSecuredRows.Of(row), columns, "GetRepositories").Single();

        columns.Select(c => c.Name).Should().Equal("Owner", "Name");
        item.Values.Select(v => v.Key).Should().Equal("Owner", "Name");
    }
}
