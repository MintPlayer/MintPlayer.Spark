using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

/// <summary>
/// #384 end to end: a query column holding a <b>keyed</b> embedded object must ship that object's
/// rendered <c>[Breadcrumb]</c>, not its CLR type name.
/// </summary>
/// <remarks>
/// S2 of <c>docs/issue_384_plan.md</c>. The unit fixture beside `EntityMapper` proves the mapper
/// renders the template; it cannot prove the value survives `RowSecurityGate`, the projector's
/// <c>asDetail.Object?.Breadcrumb ?? asDetail.Breadcrumb</c> choice, and serialization to the wire.
/// That gap is not hypothetical here — the bug being fixed lived for three months behind tests that
/// were green one layer away from where it showed.
/// <para>
/// It also stands up a shape the workspace does not currently ship. Every embedded type displayed
/// today is keyless, and every keyed one sits in an AsDetail array whose row breadcrumbs the client
/// discards — which is why nothing reproduced #384 on a screen. This is that missing intersection:
/// single, keyed, on the query surface, with no renderer to bypass the pipe.
/// </para>
/// </remarks>
public class KeyedEmbeddedBreadcrumbColumnTests : SparkTestDriver
{
    private static readonly Guid ServiceTypeId = Guid.Parse("6c1f0a52-3d94-4a1e-8b77-2f5c9d0e4a11");
    private static readonly Guid GateTypeId = Guid.Parse("7d2e1b63-4ea5-4b2f-9c88-3a6d0e1f5b22");
    private static readonly Guid ServiceQueryId = Guid.Parse("8e3f2c74-5fb6-4c3a-ad99-4b7e1f2a6c33");

    /// <summary>Keyed exactly as <c>ValueObjectKeyGenerator</c> keys a <c>[ValueObject]</c>.</summary>
    public sealed class GateSnapshot
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Mode { get; set; } = string.Empty;
    }

    public sealed record ServiceRow(string Id, string Name, GateSnapshot? Gate);

    static KeyedEmbeddedBreadcrumbColumnTests()
        => SparkValueObjects.Register(typeof(GateSnapshot), nameof(GateSnapshot.Id), row => ((GateSnapshot)row).Id);

    private static EntityTypeFile GateModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = GateTypeId,
            Name = "GateSnapshot",
            ClrType = typeof(GateSnapshot).FullName,
            Breadcrumb = "{Mode}",
            Attributes =
            [
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Mode", DataType = "string",
                    ShowedOn = EShowedOn.Query | EShowedOn.PersistentObject,
                },
            ],
        },
    };

    private static EntityTypeFile ServiceModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ServiceTypeId,
            Name = "Service",
            // No ClrType: a composed type, so the actions class resolves by name and the rows come
            // from the query source. The EMBEDDED type still declares one -- the mapper finds its
            // breadcrumb template via GetEntityTypeByClrType, which is the lookup #384 is about.
            Breadcrumb = "{Name}",
            Attributes =
            [
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Name", DataType = "string",
                    ShowedOn = EShowedOn.Query | EShowedOn.PersistentObject,
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Gate", DataType = "AsDetail",
                    AsDetailType = typeof(GateSnapshot).FullName, IsArray = false,
                    ShowedOn = EShowedOn.Query | EShowedOn.PersistentObject,
                },
            ],
        },
        Queries =
        [
            new SparkQuery
            {
                Id = ServiceQueryId,
                Name = "ServiceRows",
                Source = "Custom.GetRows",
                EntityType = "Service",
            },
        ],
    };

    private static async Task<QueryResult> ExecuteAsync(SparkEndpointFactory factory)
    {
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        return await client.ExecuteQueryAsync(ServiceQueryId);
    }

    private static string? BreadcrumbOf(QueryResult result, string rowId)
        => result.Items.Single(i => i.Id == rowId).Values.Single(v => v.Key == "Gate").Breadcrumb;

    [Fact]
    public async Task A_keyed_embedded_cell_ships_its_rendered_template()
    {
        await using var factory = new SparkEndpointFactory(Store, [ServiceModel(), GateModel()]);

        var result = await ExecuteAsync(factory);

        // Before the #384 fix this was "GateSnapshot": the row key made po.Id non-empty, so the
        // mapper skipped the embedded renderer and substituted the CLR type name, and the projector
        // shipped that verbatim for the client's cell pipe to print.
        BreadcrumbOf(result, "services/1").Should().Be("auto");
        BreadcrumbOf(result, "services/2").Should().Be("strict");
    }

    [Fact]
    public async Task The_type_name_never_reaches_the_wire_for_a_populated_row()
    {
        await using var factory = new SparkEndpointFactory(Store, [ServiceModel(), GateModel()]);

        var result = await ExecuteAsync(factory);

        result.Items
            .Select(i => i.Values.Single(v => v.Key == "Gate").Breadcrumb)
            .Should().NotContain("GateSnapshot");
    }

    [Fact]
    public async Task A_null_embedded_object_carries_no_breadcrumb_rather_than_a_type_name()
    {
        // The absent case has to stay absent: a cell that invented a breadcrumb for a null object
        // would render a label for something that is not there.
        await using var factory = new SparkEndpointFactory(Store, [ServiceModel(), GateModel()]);

        var result = await ExecuteAsync(factory);

        BreadcrumbOf(result, "services/3").Should().BeNull();
    }

    [Fact]
    public async Task The_row_key_still_reaches_the_wire_beside_the_breadcrumb()
    {
        // Guards against "fixing" the breadcrumb by undoing #382's key round trip — the two have to
        // hold together, since the key is what lets a save match rows at all.
        await using var factory = new SparkEndpointFactory(Store, [ServiceModel(), GateModel()]);

        var result = await ExecuteAsync(factory);

        var cell = result.Items.Single(i => i.Id == "services/1").Values.Single(v => v.Key == "Gate");
        cell.Value.Should().NotBeNull();
    }
}

/// <summary>Found by name — the composed-query seam. Rows are computed; nothing is stored.</summary>
public sealed class ServiceActions : ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Test fixture: the rows are literals with no per-caller data, so there is nothing to scope.";

    public IEnumerable<KeyedEmbeddedBreadcrumbColumnTests.ServiceRow> GetRows() =>
    [
        new("services/1", "Alpha", new KeyedEmbeddedBreadcrumbColumnTests.GateSnapshot { Id = "k1", Mode = "auto" }),
        new("services/2", "Beta", new KeyedEmbeddedBreadcrumbColumnTests.GateSnapshot { Id = "k2", Mode = "strict" }),
        new("services/3", "Gamma", null),
    ];
}
