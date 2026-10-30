using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Prerendering;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Prerendering;

// Fixture names start with "Pe": ActionsResolver matches actions classes by simple name across the assembly.

public class PeDriver
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
}

public class PeCar
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string? Driver { get; set; }
}

public class PeCarActions(IEntityMapper mapper)
    : DefaultPersistentObjectActions<PeCar>(mapper), Abstractions.Authorization.ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: every row is visible; the subject is server-rendering equivalence.";
}

public class PeDriverActions(IEntityMapper mapper)
    : DefaultPersistentObjectActions<PeDriver>(mapper), Abstractions.Authorization.ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: every row is visible; the subject is server-rendering equivalence.";
}

public class PeContext : SparkContext
{
    public IRavenQueryable<PeCar> Cars => Session.Query<PeCar>();
    public IRavenQueryable<PeDriver> Drivers => Session.Query<PeDriver>();
}

/// <summary>
/// The permanent security matrix of server rendering (docs/spark_ssr_PRD.md SP4, M5, D4): what the in-process
/// supplier hands a page is exactly what the same visitor would fetch over HTTP — status and body, byte for byte —
/// because it runs the same endpoints, authorization and serializer. Anything a visitor may not read is therefore
/// absent from the page, `ng-state` included, as it is from the API.
/// </summary>
public class SparkPrerenderEquivalenceTests : SparkTestDriver
{
    private static readonly Guid CarTypeId = Guid.Parse("a77e0000-0000-4000-8000-00000000e001");
    private static readonly Guid DriverTypeId = Guid.Parse("a77e0000-0000-4000-8000-00000000e002");
    private static readonly Guid CarsQueryId = Guid.Parse("a77e0000-0000-4000-8000-00000000e003");

    // Anonymous visitors may list and open cars; drivers are denied, so a car's Driver reference is redacted.
    private static readonly string[] Grants = ["QueryRead/PeCar"];

    private SparkEndpointFactory<PeContext> factory = null!;
    private HttpClient http = null!;
    private SparkPrerenderDispatcher dispatcher = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        factory = new SparkEndpointFactory<PeContext>(
            Store,
            [CarModel(), DriverModel()],
            configureServices: services =>
            {
                services.AddScoped<PeCarActions>();
                services.AddScoped<PeDriverActions>();
            },
            configureSpark: spark => spark.AddPrerendering(),
            security: SparkTestSecurity.Empty.Granting(Grants));
        http = factory.CreateClient();

        var services = factory.GetService<IServiceProvider>();
        dispatcher = factory.GetService<SparkPrerenderDispatcher>();
        dispatcher.Attach(new ApplicationBuilder(services));

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new PeDriver { Name = "Zeno" }, "pedrivers/1");
            await session.StoreAsync(new PeCar { Name = "Alpha", Driver = "pedrivers/1" }, "pecars/1");
        });
    }

    public override async Task DisposeAsync()
    {
        http.Dispose();
        await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    public static TheoryData<string, string, object?> Reads => new()
    {
        { "GET", "/spark/types", null },
        { "POST", "/spark/po/load", new { objectTypeId = CarTypeId.ToString(), id = "pecars/1" } },
        { "POST", "/spark/po/load", new { objectTypeId = DriverTypeId.ToString(), id = "pedrivers/1" } },
        { "POST", "/spark/po/load", new { objectTypeId = CarTypeId.ToString(), id = "pecars/999" } },
        { "POST", "/spark/queries/execute", new { queryId = CarsQueryId.ToString(), skip = 0, take = 10 } },
        { "POST", "/spark/queries/get", new { queryId = CarsQueryId.ToString() } },
    };

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task A_supplied_response_is_the_http_response(string method, string path, object? body)
    {
        var (httpStatus, httpBody) = await SendHttpAsync(method, path, body);

        var supplied = await SupplyAsync(method, path, body);

        supplied.Status.Should().Be((int)httpStatus);
        supplied.Body.Should().Be(httpBody);
    }

    [Fact]
    public async Task A_missing_object_and_a_denied_one_answer_alike()
    {
        // D4: an anonymous visitor learns nothing from the difference between "missing" and "denied".
        var missing = await SupplyAsync("POST", "/spark/po/load", new { objectTypeId = CarTypeId.ToString(), id = "pecars/999" });
        var denied = await SupplyAsync("POST", "/spark/po/load", new { objectTypeId = DriverTypeId.ToString(), id = "pedrivers/1" });

        denied.Status.Should().Be(missing.Status);
        denied.Body.Should().Be(missing.Body);
    }

    [Fact]
    public async Task A_redacted_reference_is_absent_from_what_the_page_receives()
    {
        var context = NewPage();
        await context.PostAsync("/spark/po/load", new { objectTypeId = CarTypeId.ToString(), id = "pecars/1" });
        await context.PostAsync("/spark/queries/execute", new { queryId = CarsQueryId.ToString(), skip = 0, take = 10 });

        var page = JsonSerializer.Serialize(await context.ResponsesAsync());

        page.Should().Contain("Alpha");
        page.Should().NotContain("Zeno");
        page.Should().NotContain("pedrivers/1");
    }

    [Fact]
    public async Task An_endpoint_not_marked_prerenderable_is_refused_and_not_supplied()
    {
        var context = NewPage();

        var response = await context.PostAsync("/spark/po/create", new { objectTypeId = CarTypeId.ToString(), persistentObject = new { name = "Gamma" } });

        response.Status.Should().Be(StatusCodes.Status404NotFound);
        (await context.ResponsesAsync()).Should().BeEmpty();
        using var session = Store.OpenAsyncSession();
        (await Raven.Client.Documents.LinqExtensions.CountAsync(session.Query<PeCar>())).Should().Be(1);
    }

    private SparkPrerenderContext NewPage()
    {
        var page = new DefaultHttpContext { RequestServices = factory.GetService<IServiceProvider>() };
        page.Request.Scheme = "http";
        page.Request.Host = new HostString("localhost");
        page.Request.Path = "/po/pecar/pecars%2F1";
        return new SparkPrerenderContext(dispatcher, page, "en", "UTC");
    }

    private Task<Abstractions.Prerendering.SparkPrerenderResponse> SupplyAsync(string method, string path, object? body)
        => method == "GET" ? NewPage().GetAsync(path) : NewPage().PostAsync(path, body);

    private async Task<(HttpStatusCode Status, string Body)> SendHttpAsync(string method, string path, object? body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            var (cookie, token) = await factory.MintAntiforgeryAsync();
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add("X-XSRF-TOKEN", token);
            request.Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), System.Text.Encoding.UTF8, "application/json");
        }
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static EntityTypeFile CarModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = CarTypeId,
            Name = "PeCar",
            ClrType = typeof(PeCar).FullName!,
            Breadcrumb = "{Name}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string", Order = 1 },
                new() { Id = Guid.NewGuid(), Name = "Driver", DataType = "Reference", ReferenceType = typeof(PeDriver).FullName, Order = 2 },
            ],
        },
        Queries =
        [
            new SparkQuery
            {
                Id = CarsQueryId,
                Name = "PeCars",
                Source = "Database.Cars",
                EntityType = "PeCar",
                Columns = [new() { Name = "Name" }, new() { Name = "Driver" }],
            },
        ],
    };

    private static EntityTypeFile DriverModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = DriverTypeId,
            Name = "PeDriver",
            ClrType = typeof(PeDriver).FullName!,
            Breadcrumb = "{Name}",
            Attributes = [new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
        },
    };
}
