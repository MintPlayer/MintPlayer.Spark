using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints.EntityTypes;

/// <summary>
/// #385 — an AsDetail row type's definition must reach the client even when the caller holds no
/// rights on that row type.
/// </summary>
/// <remarks>
/// A row is edited through its parent, so nobody grants <c>Query/{RowType}</c> — of the six
/// array-AsDetail attributes in this workspace, three have row types with no rights at all. The
/// client resolved the row type from the entity-type catalogue, which is <c>Query</c>-gated, so it
/// found nothing; <c>asDetailColumns</c> then returned <c>[]</c> and the table rendered with **no
/// columns**, headers included. The missing Add button that #385 is named after is the smaller half
/// of that.
/// <para>
/// The gate is the <b>parent's</b> right, which is the same one that already ships every row's
/// attribute schema — labels, data types and validation rules — inside <c>attr.Objects</c>, with no
/// check on the row type. These tests pin both halves: that the definition arrives, and that the
/// projection/query surface does not come with it.
/// </para>
/// </remarks>
public class EmbeddedDetailTypeTests : SparkTestDriver
{
    private static readonly Guid ProbeTypeId = Guid.Parse("aa11bb22-cc33-4d44-9e55-ff6677889900");
    private static readonly Guid LineTypeId = Guid.Parse("bb22cc33-dd44-4e55-af66-001122334455");
    private static readonly Guid ProbeQueryId = Guid.Parse("cc33dd44-ee55-4f66-b077-112233445566");

    public sealed class DetailProbeLine { public string Id { get; set; } = ""; public string Sku { get; set; } = ""; }
    public sealed record DetailProbeRow(string Id, string Reference, List<DetailProbeLine> Lines);

    /// <summary>The row type — no query of its own, and granted nothing in the test's security file.</summary>
    private static EntityTypeFile LineModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = LineTypeId,
            Name = "DetailProbeLine",
            ClrType = typeof(DetailProbeLine).FullName,
            Breadcrumb = "{Sku}",
            // Present so the pruning assertions have something to prune.
            QueryType = "Demo.VDetailProbeLine",
            IndexName = "DetailProbeLines/Search",
            Alias = "orderline",
            Attributes =
            [
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Sku", DataType = "string",
                    ShowedOn = EShowedOn.Query | EShowedOn.PersistentObject,
                },
            ],
        },
    };

    private static EntityTypeFile ProbeModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ProbeTypeId,
            Name = "DetailProbe",
            Breadcrumb = "{Reference}",
            Attributes =
            [
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Reference", DataType = "string",
                    ShowedOn = EShowedOn.Query | EShowedOn.PersistentObject,
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Lines", DataType = "AsDetail",
                    AsDetailType = typeof(DetailProbeLine).FullName, IsArray = true,
                    ShowedOn = EShowedOn.Query | EShowedOn.PersistentObject,
                },
            ],
        },
        Queries =
        [
            new SparkQuery { Id = ProbeQueryId, Name = "DetailProbeRows", Source = "Custom.GetRows", EntityType = "DetailProbe" },
        ],
    };

    /// <summary>Grants the parent and nothing else — the shape that broke.</summary>
    private static SparkTestSecurity ParentOnly() => SparkTestSecurity.Empty.Granting("QueryRead/DetailProbe");

    private static async Task<EntityTypeDefinition?> GetProbeTypeAsync(SparkEndpointFactory factory)
    {
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        var types = await client.ListEntityTypesAsync();
        return types.FirstOrDefault(t => t.Id == ProbeTypeId);
    }

    [Fact]
    public async Task The_row_type_definition_ships_with_its_parent()
    {
        await using var factory = new SparkEndpointFactory(
            Store, [ProbeModel(), LineModel()], security: ParentOnly());

        var order = await GetProbeTypeAsync(factory);

        order.Should().NotBeNull();
        var line = order!.DetailTypes?.SingleOrDefault(t => t.Id == LineTypeId);
        line.Should().NotBeNull(
            "the caller holds no right on DetailProbeLine, so it is absent from the catalogue — which is "
            + "exactly when the client cannot resolve its columns any other way");
        line!.Attributes.Select(a => a.Name).Should().Contain("Sku");
    }

    [Fact]
    public async Task The_row_type_is_still_absent_from_the_catalogue_itself()
    {
        // FR3: the Query-scoping of the catalogue is deliberate and is NOT widened by this.
        await using var factory = new SparkEndpointFactory(
            Store, [ProbeModel(), LineModel()], security: ParentOnly());

        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        var types = await client.ListEntityTypesAsync();

        types.Should().NotContain(t => t.Id == LineTypeId);
    }

    [Fact]
    public async Task The_embedded_copy_carries_no_projection_or_query_surface()
    {
        // The whole authorization argument rests on this: the embedded definition must disclose
        // less than the row data already does. QueryType/IndexName/Queries/Alias are what
        // PRD-SecurityAudit names as worth withholding, and no client needs them to draw a table.
        await using var factory = new SparkEndpointFactory(
            Store, [ProbeModel(), LineModel()], security: ParentOnly());

        var line = (await GetProbeTypeAsync(factory))!.DetailTypes!.Single(t => t.Id == LineTypeId);

        line.QueryType.Should().BeNull();
        line.IndexName.Should().BeNull();
        line.Alias.Should().BeNull();
        (line.Queries ?? []).Should().BeEmpty();
    }

    [Fact]
    public async Task A_caller_with_no_right_on_the_parent_gets_nothing()
    {
        // The gate is the parent's right. Without it there is no parent to hang the row type on.
        await using var factory = new SparkEndpointFactory(
            Store, [ProbeModel(), LineModel()], security: SparkTestSecurity.Empty.Granting("QueryRead/Unrelated"));

        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        var types = await client.ListEntityTypesAsync();

        types.Should().NotContain(t => t.Id == ProbeTypeId);
        types.SelectMany(t => t.DetailTypes ?? []).Should().NotContain(t => t.Id == LineTypeId);
    }
}

/// <summary>Found by name — the composed-query seam. Rows are computed; nothing is stored.</summary>
public sealed class DetailProbeActions : ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Test fixture: the rows are literals with no per-caller data, so there is nothing to scope.";

    public IEnumerable<EmbeddedDetailTypeTests.DetailProbeRow> GetRows() =>
    [
        new("orders/1", "SO-1", [new EmbeddedDetailTypeTests.DetailProbeLine { Id = "l1", Sku = "ABC" }]),
    ];
}
