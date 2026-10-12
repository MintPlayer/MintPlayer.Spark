using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Authorization;

using Po = Abstractions.PersistentObject;

// Fixture names start with "Rt": ActionsResolver matches actions classes by simple name across the
// whole assembly. Top-level, so the right's type name is the CLR name's last segment.

public class RtDriver
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
}

public class RtCar
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string? Driver { get; set; }
    public List<string> Passengers { get; set; } = [];
    public string? Owner { get; set; }
}

public class RtCarActions(IEntityMapper mapper)
    : DefaultPersistentObjectActions<RtCar>(mapper), Abstractions.Authorization.ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: every row is visible; the subject is reference target rights.";
}

public class RtDriverActions(IEntityMapper mapper)
    : DefaultPersistentObjectActions<RtDriver>(mapper), Abstractions.Authorization.ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: every row is visible; the subject is reference target rights.";
}

public class RtContext : SparkContext
{
    public IRavenQueryable<RtCar> Cars => Session.Query<RtCar>();
    public IRavenQueryable<RtDriver> Drivers => Session.Query<RtDriver>();
}

/// <summary>
/// MCP PRD D19, superseded by the owner decision of 2026-10-12: a reference attribute (single or
/// array) is shown only to a caller who may <c>Read</c> the reference's target type. Without that right
/// it is treated exactly as an attribute-level <c>Read</c>/<c>Query</c> denial — absent from the type
/// metadata, the query metadata, detail and query rows, sort, filter and distincts — and a save keeps its
/// stored value. A breadcrumb template of the owning type that interpolates it renders the placeholder.
/// </summary>
/// <remarks>
/// Before: <c>BreadcrumbResolver</c> rendered the placeholder, but the attribute and the target id were
/// still sent (<c>EntityMapper</c> kept <c>Value</c>, <c>QueryResultProjector</c> sent Value/ObjectId).
/// </remarks>
public class ReferenceTargetRightsTests : SparkTestDriver
{
    private static readonly Guid CarTypeId = Guid.Parse("a77c0000-0000-4000-8000-00000000d001");
    private static readonly Guid DriverTypeId = Guid.Parse("a77c0000-0000-4000-8000-00000000d002");
    private static readonly Guid CarsQueryId = Guid.Parse("a77c0000-0000-4000-8000-00000000d003");

    private static readonly string[] CarOnly = ["QueryReadEditNew/RtCar"];
    private static readonly string[] CarAndDriver = ["QueryReadEditNew/RtCar", "Read/RtDriver"];

    private readonly List<IAsyncDisposable> factories = [];
    private readonly List<IDisposable> disposables = [];

    public override async Task DisposeAsync()
    {
        foreach (var disposable in disposables)
            disposable.Dispose();
        foreach (var factory in factories)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<(SparkClient Client, HttpClient Http)> StartAsync(string[] grants)
    {
        var factory = new SparkEndpointFactory<RtContext>(
            Store,
            [CarModel(), DriverModel()],
            configureServices: services =>
            {
                services.AddScoped<RtCarActions>();
                services.AddScoped<RtDriverActions>();
            },
            security: SparkTestSecurity.Empty.Granting(grants));
        factories.Add(factory);
        var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        var http = factory.CreateClient();
        disposables.Add(client);
        disposables.Add(http);

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new RtDriver { Name = "Dora" }, "rtdrivers/1");
            await session.StoreAsync(new RtDriver { Name = "Zeno" }, "rtdrivers/2");
            await session.StoreAsync(new RtCar { Name = "Alpha", Driver = "rtdrivers/2", Passengers = ["rtdrivers/1"], Owner = "rtdrivers/1" }, "rtcars/1");
            await session.StoreAsync(new RtCar { Name = "Beta", Driver = "rtdrivers/1", Passengers = ["rtdrivers/2"], Owner = "rtdrivers/2" }, "rtcars/2");
        });

        return (client, http);
    }

    private static readonly string[] References = ["Driver", "Passengers", "Owner"];

    // ---- detail -----------------------------------------------------------------------------------

    [Fact]
    public async Task Detail_omits_references_to_a_type_the_caller_may_not_Read()
    {
        var (client, http) = await StartAsync(CarOnly);

        var po = await client.GetPersistentObjectAsync(CarTypeId, "rtcars/1");
        using var load = await http.PostAsync("/spark/po/load",
            System.Net.Http.Json.JsonContent.Create(new { objectTypeId = CarTypeId.ToString(), id = "rtcars/1" }));
        var raw = await load.Content.ReadAsStringAsync();
        load.StatusCode.Should().Be(HttpStatusCode.OK);

        po!.Attributes.Select(a => a.Name).Should().Contain("Name");
        po!.Attributes.Select(a => a.Name).Intersect(References).Should().BeEmpty();
        raw.Should().NotContain("rtdrivers/", "the target id is the value the caller may not see");
    }

    [Fact]
    public async Task Detail_keeps_references_when_the_caller_may_Read_the_target()
    {
        var (client, _) = await StartAsync(CarAndDriver);

        var po = await client.GetPersistentObjectAsync(CarTypeId, "rtcars/1");

        References.Except(po!.Attributes.Select(a => a.Name)).Should().BeEmpty();
        po.Attributes.Single(a => a.Name == "Driver").Value?.ToString().Should().Be("rtdrivers/2");
    }

    [Fact]
    public async Task A_breadcrumb_template_that_interpolates_a_hidden_reference_renders_the_placeholder()
    {
        var (client, _) = await StartAsync(CarOnly);

        var po = await client.GetPersistentObjectAsync(CarTypeId, "rtcars/1");

        po!.Breadcrumb.Should().Be("Alpha by —");
    }

    // ---- query ------------------------------------------------------------------------------------

    [Fact]
    public async Task Query_rows_and_columns_omit_references_to_an_unreadable_type()
    {
        var (client, http) = await StartAsync(CarOnly);

        var result = await client.ExecuteQueryAsync(CarsQueryId);
        using var response = await http.PostAsync("/spark/queries/execute",
            System.Net.Http.Json.JsonContent.Create(new { queryId = CarsQueryId.ToString() }));
        var raw = await response.Content.ReadAsStringAsync();

        result.Columns.Select(c => c.Name).Should().Contain("Name");
        result.Columns.Select(c => c.Name).Intersect(References).Should().BeEmpty();
        result.Items.Should().OnlyContain(i => i.Values.All(v => !References.Contains(v.Key)));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        raw.Should().NotContain("rtdrivers/");
    }

    [Fact]
    public async Task Query_rows_keep_references_when_the_caller_may_Read_the_target()
    {
        var (client, _) = await StartAsync(CarAndDriver);

        var result = await client.ExecuteQueryAsync(CarsQueryId);

        result.Items.Single(i => i.Id == "rtcars/1").Values.Single(v => v.Key == "Driver").ObjectId.Should().Be("rtdrivers/2");
    }

    [Fact]
    public async Task Sorting_by_a_hidden_reference_is_ignored()
    {
        var (client, _) = await StartAsync(CarOnly);

        var asc = await client.ExecuteQueryAsync(CarsQueryId, sortColumns: [new SortColumn { Property = "Driver", Direction = "asc" }]);
        var desc = await client.ExecuteQueryAsync(CarsQueryId, sortColumns: [new SortColumn { Property = "Driver", Direction = "desc" }]);

        desc.Items.Select(i => i.Id).Should().Equal(asc.Items.Select(i => i.Id),
            "a sort that reorders the rows is a comparison oracle on the hidden target id");
    }

    [Fact]
    public async Task Filtering_on_a_hidden_reference_is_ignored_and_its_distincts_are_empty()
    {
        var (client, _) = await StartAsync(CarOnly);

        var filtered = await client.ExecuteQueryAsync(CarsQueryId,
            columns: [new QueryColumnFilter { Name = "Driver", Includes = ["rtdrivers/1"] }]);
        var distincts = await client.GetDistinctValuesAsync(CarsQueryId, "Driver");

        filtered.TotalItems.Should().Be(2, "an equality filter on the hidden id would confirm it through the count");
        distincts.Matching.Should().BeEmpty();
    }

    // ---- metadata ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_type_metadata_omits_hidden_references_and_keeps_them_with_Read()
    {
        var (denied, _) = await StartAsync(CarOnly);
        var (granted, _) = await StartAsync(CarAndDriver);

        var hidden = await denied.GetEntityTypeAsync("RtCar");
        var listed = (await denied.ListEntityTypesAsync()).Single(t => t.Name == "RtCar");
        var shown = await granted.GetEntityTypeAsync("RtCar");

        hidden!.Attributes.Select(a => a.Name).Intersect(References).Should().BeEmpty();
        listed.Attributes.Select(a => a.Name).Intersect(References).Should().BeEmpty();
        hidden.Breadcrumb.Should().NotContain("{Driver}", "the template must not name an attribute the caller cannot see");
        References.Except(shown!.Attributes.Select(a => a.Name)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_query_metadata_omits_a_hidden_reference_column_and_sort()
    {
        var (client, _) = await StartAsync(CarOnly);

        var query = await client.GetQueryAsync(CarsQueryId);

        query!.Columns!.Select(c => c.Name).Should().NotContain("Driver");
        query.SortColumns.Select(s => s.Property).Should().NotContain("Driver");
    }

    // ---- save -------------------------------------------------------------------------------------

    [Fact]
    public async Task Save_preserves_a_hidden_reference_even_when_the_caller_posts_one()
    {
        var (client, _) = await StartAsync(CarOnly);

        var po = await client.GetPersistentObjectAsync(CarTypeId, "rtcars/1");
        Set(po!, "Name", "Alpha renamed");
        Set(po!, "Driver", "rtdrivers/1");
        Set(po!, "Owner", null);

        await client.UpdatePersistentObjectAsync(po!);

        var stored = await LoadAsync("rtcars/1");
        stored.Name.Should().Be("Alpha renamed");
        stored.Driver.Should().Be("rtdrivers/2", "a hidden reference keeps its stored value, as any denied attribute");
        stored.Passengers.Should().Equal("rtdrivers/1");
        stored.Owner.Should().Be("rtdrivers/1");
    }

    /// <summary>
    /// <c>Owner</c> is required and refers to <c>RtDriver</c>: a caller without <c>Read/RtDriver</c> can open
    /// the create form but never fill it. The save fails clearly instead of storing a car without owner.
    /// </summary>
    [Fact]
    public async Task Creating_with_a_hidden_required_reference_fails_with_a_clear_error()
    {
        var (client, _) = await StartAsync(CarOnly);

        var po = await client.NewPersistentObjectAsync(CarTypeId);
        Set(po, "Name", "Gamma");

        var act = () => client.CreatePersistentObjectAsync(po);

        var ex = (await act.Should().ThrowAsync<SparkClientException>()).Which;
        ex.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ex.ResponseBody.Should().Contain("RtDriver", "the error names the type the caller needs Read on");
    }

    [Fact]
    public async Task Creating_with_Read_on_the_target_fills_the_required_reference()
    {
        var (client, _) = await StartAsync(CarAndDriver);

        var po = await client.NewPersistentObjectAsync(CarTypeId);
        Set(po, "Name", "Gamma");
        Set(po, "Owner", "rtdrivers/2");

        var created = await client.CreatePersistentObjectAsync(po);

        (await LoadAsync(created.Id!)).Owner.Should().Be("rtdrivers/2");
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task<RtCar> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<RtCar>(id))!;
    }

    private static void Set(Po po, string name, object? value)
    {
        var attribute = po.Attributes.FirstOrDefault(a => a.Name == name);
        if (attribute is null)
        {
            attribute = new PersistentObjectAttribute { Name = name, DataType = name == "Name" ? "string" : "Reference" };
            po.AddAttribute(attribute);
        }
        attribute.Value = value;
        attribute.IsValueChanged = true;
    }

    private static EntityTypeFile CarModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = CarTypeId,
            Name = "RtCar",
            ClrType = typeof(RtCar).FullName!,
            Breadcrumb = "{Name} by {Driver}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string", Order = 1 },
                new() { Id = Guid.NewGuid(), Name = "Driver", DataType = "Reference", ReferenceType = typeof(RtDriver).FullName, Order = 2 },
                new() { Id = Guid.NewGuid(), Name = "Passengers", DataType = "Reference", IsArray = true, ReferenceType = typeof(RtDriver).FullName, Order = 3 },
                new() { Id = Guid.NewGuid(), Name = "Owner", DataType = "Reference", ReferenceType = typeof(RtDriver).FullName, IsRequired = true, Order = 4 },
            ],
        },
        Queries =
        [
            new SparkQuery
            {
                Id = CarsQueryId,
                Name = "RtCars",
                Source = "Database.Cars",
                EntityType = "RtCar",
                SortColumns = [new SortColumn { Property = "Driver", Direction = "asc" }],
                Columns = [new() { Name = "Name" }, new() { Name = "Driver" }],
            },
        ],
    };

    private static EntityTypeFile DriverModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = DriverTypeId,
            Name = "RtDriver",
            ClrType = typeof(RtDriver).FullName!,
            Breadcrumb = "{Name}",
            Attributes = [new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
        },
    };
}
