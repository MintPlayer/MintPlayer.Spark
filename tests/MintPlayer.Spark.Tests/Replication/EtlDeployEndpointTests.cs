using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Endpoints;
using MintPlayer.Spark.Replication.Services;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// In-process coverage for /spark/etl/deploy: body validation, the mTLS gate, the read grant, and
/// what the owner actually deploys. The ETL task manager is substituted, so the deployment the
/// endpoint hands it is assertable: its transforms (F16) and its destination (F19).
/// </summary>
public class EtlDeployEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public class Car
    {
        public string? Id { get; set; }
        public string LicensePlate { get; set; } = "";
        public string Model { get; set; } = "";
    }

    public class CarV1
    {
        public string Plate { get; set; } = "";
        public string Model { get; set; } = "";
    }

    private readonly IModuleCertificateValidator _certValidator = Substitute.For<IModuleCertificateValidator>();
    private readonly IPermissionService _permissionService = Substitute.For<IPermissionService>();
    private readonly IEtlTaskManager _etlTaskManager = Substitute.For<IEtlTaskManager>();
    private readonly IModuleDirectory _moduleDirectory = Substitute.For<IModuleDirectory>();
    private readonly ReplicationContractCatalog _catalog;
    private readonly List<EtlDeployment> _deployed = [];

    public EtlDeployEndpointTests()
    {
        var store = Substitute.For<IDocumentStore>();
        store.Conventions.Returns(new DocumentConventions());
        store.Database.Returns("fleet-db");

        var options = new SparkReplicationOptions { ModuleName = "Fleet", ModuleUrl = "https://fleet.test" };
        options.Publish<Car, CarV1>("cars", 1, c => new CarV1 { Plate = c.LicensePlate, Model = c.Model });
        _catalog = new ReplicationContractCatalog(Options.Create(options), store);

        _moduleDirectory.FindAsync("HR", Arg.Any<CancellationToken>()).Returns(Task.FromResult<ModuleInformation?>(new ModuleInformation
        {
            AppName = "HR",
            AppUrl = "https://hr.test",
            DatabaseName = "hr-db",
            DatabaseUrls = ["http://hr.registry:8080"],
        }));

        _etlTaskManager.DeployAsync(Arg.Do<EtlDeployment>(_deployed.Add), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EtlDeploymentResult { Success = true, TasksCreated = 1 }));
    }

    private EtlDeploy NewEndpoint() =>
        new(NullLogger<EtlTaskManager>.Instance, _etlTaskManager, _certValidator, _permissionService, new HttpContextAccessor(), _moduleDirectory, _catalog);

    /// <summary>Grants the caller the right to replicate every collection.</summary>
    private void AllowReplication() =>
        _permissionService.IsAllowedAsync("Replicate", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

    private void Cert(ModuleCertificateValidation result) =>
        _certValidator.ValidateAsync(Arg.Any<HttpContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));

    [Fact]
    public async Task Invalid_json_body_returns_400()
    {
        var ctx = NewContext("{ not json");
        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx)).Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A body the endpoint cannot bind answers what it answered while it read the body by hand
    /// (measured before it became a typed endpoint, endpoints generator completion M3); a JSON
    /// <c>null</c> now names contracts, which is what the body carries.
    /// </summary>
    [Fact]
    public async Task Unbindable_bodies_answer_as_before()
    {
        (await SyncApplyEndpointTests.ProbeAsync(ctx => NewEndpoint().HandleAsync(ctx))).Should().Be("""
            none: 400 {"success":false,"tasksCreated":0,"tasksUpdated":0,"tasksRemoved":0,"error":"Invalid request body"}
            empty: 400 {"success":false,"tasksCreated":0,"tasksUpdated":0,"tasksRemoved":0,"error":"Invalid request body"}
            null: 400 {"success":false,"tasksCreated":0,"tasksUpdated":0,"tasksRemoved":0,"error":"Request must contain at least one contract"}
            malformed: 400 {"success":false,"tasksCreated":0,"tasksUpdated":0,"tasksRemoved":0,"error":"Invalid request body"}
            text/plain: 400 {"success":false,"tasksCreated":0,"tasksUpdated":0,"tasksRemoved":0,"error":"Invalid request body"}
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Empty_contracts_list_returns_400()
    {
        var ctx = NewContext(new { requestingModule = "HR", contracts = Array.Empty<object>() });
        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx)).Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Missing_certificate_returns_401()
    {
        Cert(ModuleCertificateValidation.MissingCertificate);
        var ctx = NewContext(DeployBody("HR"));
        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_module_returns_403()
    {
        Cert(ModuleCertificateValidation.UnknownModule);
        var ctx = NewContext(DeployBody("Attacker-Not-Registered"));
        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Thumbprint_mismatch_returns_403()
    {
        Cert(ModuleCertificateValidation.ThumbprintMismatch);
        var ctx = NewContext(DeployBody("HR"));
        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx)).Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The read grant. A certificate answers "who are you", never "what may you read". The
    /// collection checked is the owner's contract source (<c>Cars</c>), which the request cannot name.
    /// </summary>
    [Fact]
    public async Task A_module_may_not_replicate_a_contract_whose_source_it_has_no_right_to()
    {
        Cert(ModuleCertificateValidation.Ok);
        _permissionService.IsAllowedAsync("Replicate", "Cars", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var ctx = NewContext(DeployBody("HR"));

        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx))
            .Should().Be(HttpStatusCode.Forbidden,
                "an authenticated module is not thereby entitled to every collection the owner holds");
        _deployed.Should().BeEmpty();
    }

    /// <summary>The control: a granted contract deploys.</summary>
    [Fact]
    public async Task A_module_may_replicate_a_contract_it_is_granted()
    {
        Cert(ModuleCertificateValidation.Ok);
        AllowReplication();

        var ctx = NewContext(DeployBody("HR"));

        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx)).Should().Be(HttpStatusCode.OK);
        _deployed.Should().ContainSingle();
    }

    [Fact]
    public async Task A_contract_the_owner_does_not_publish_is_refused_with_404()
    {
        Cert(ModuleCertificateValidation.Ok);
        AllowReplication();

        var ctx = NewContext(new
        {
            requestingModule = "HR",
            contracts = new[] { new { contract = "sparkusers", version = 1, targetCollection = "SparkUsers" } },
        });

        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx)).Should().Be(HttpStatusCode.NotFound);
        _deployed.Should().BeEmpty();
    }

    /// <summary>
    /// F16, red before the fix: the owner deployed the consumer's JavaScript verbatim, so a
    /// <c>load()</c> in it read any document on the owner's database past the
    /// <c>Replicate/{Collection}</c> grant. Now a body may carry a script and it is simply not read:
    /// the deployed transform is the one the owner generated from its own contract.
    /// </summary>
    [Fact]
    public async Task F16_a_consumer_supplied_script_never_reaches_the_deployed_transform()
    {
        Cert(ModuleCertificateValidation.Ok);
        AllowReplication();

        var ctx = NewContext(new
        {
            requestingModule = "HR",
            scripts = new[] { new { sourceCollection = "Cars", script = "loadToCars({ Leak: load('SparkUsers/1') })" } },
            contracts = new[] { new { contract = "cars", version = 1, targetCollection = "Cars", script = "loadToCars(load('SparkUsers/1'))" } },
        });

        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx)).Should().Be(HttpStatusCode.OK);

        var transform = _deployed.Single().Transforms.Single();
        transform.SourceCollection.Should().Be("Cars");
        transform.Script.Should().NotContain("load(");
        transform.Script.Should().NotContain("SparkUsers");
        transform.Script.Should().Be(_catalog.Find("cars", 1)!.ScriptFor("Cars"));
    }

    /// <summary>
    /// F19, red before the fix: the request's <c>TargetDatabase</c>/<c>TargetUrls</c> became the ETL
    /// connection string unchecked, so the calling module chose where the owner's server sent the
    /// granted data. Now the destination is the authenticated module's registry entry.
    /// </summary>
    [Fact]
    public async Task F19_the_destination_comes_from_the_registry_never_from_the_request()
    {
        Cert(ModuleCertificateValidation.Ok);
        AllowReplication();

        var ctx = NewContext(new
        {
            requestingModule = "HR",
            targetDatabase = "attacker-db",
            targetUrls = new[] { "http://attacker.example:8080" },
            contracts = new[] { new { contract = "cars", version = 1, targetCollection = "Cars" } },
        });

        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx)).Should().Be(HttpStatusCode.OK);

        var deployment = _deployed.Single();
        deployment.TargetDatabase.Should().Be("hr-db");
        deployment.TargetUrls.Should().Equal("http://hr.registry:8080");
    }

    [Fact]
    public async Task A_module_without_a_registered_database_is_refused()
    {
        Cert(ModuleCertificateValidation.Ok);
        AllowReplication();
        _moduleDirectory.FindAsync("HR", Arg.Any<CancellationToken>()).Returns(Task.FromResult<ModuleInformation?>(null));

        var ctx = NewContext(DeployBody("HR"));

        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx)).Should().Be(HttpStatusCode.Conflict);
        _deployed.Should().BeEmpty();
    }

    /// <summary>The target collection is spliced into <c>loadTo…</c>, so only a plain identifier is accepted.</summary>
    [Theory]
    [InlineData("Cars({}); load('SparkUsers/1'); loadToCars")]
    [InlineData("Cars-v2")]
    public async Task A_target_collection_that_is_not_an_identifier_is_refused(string targetCollection)
    {
        Cert(ModuleCertificateValidation.Ok);
        AllowReplication();

        var ctx = NewContext(new
        {
            requestingModule = "HR",
            contracts = new[] { new { contract = "cars", version = 1, targetCollection } },
        });

        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx)).Should().Be(HttpStatusCode.BadRequest);
        _deployed.Should().BeEmpty();
    }

    /// <summary>
    /// F18: an owner whose licence has no ETL answers 409 with the reason, not a generic 500 the
    /// consumer would retry until it dead-lettered.
    /// </summary>
    [Fact]
    public async Task An_owner_without_an_ETL_licence_answers_409_with_the_reason()
    {
        Cert(ModuleCertificateValidation.Ok);
        AllowReplication();
        _etlTaskManager.DeployAsync(Arg.Any<EtlDeployment>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EtlDeploymentResult { Success = false, Error = EtlDeploymentResult.NotLicensedError }));

        var ctx = NewContext(DeployBody("HR"));

        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx)).Should().Be(HttpStatusCode.Conflict);
        ctx.Response.Body.Position = 0;
        new StreamReader(ctx.Response.Body).ReadToEnd().Should().Contain(EtlDeploymentResult.NotLicensedError);
    }

    private static object DeployBody(string module) => new
    {
        requestingModule = module,
        contracts = new[] { new { contract = "cars", version = 1, targetCollection = "Cars" } },
    };

    private static DefaultHttpContext NewContext(object body) => NewContext(JsonSerializer.Serialize(body, Web));

    private static DefaultHttpContext NewContext(string json)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        // The typed endpoint reaches its context through IHttpContextAccessor (AsyncLocal, so per test flow).
        new HttpContextAccessor().HttpContext = ctx;
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        ctx.Request.ContentLength = bytes.Length;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<HttpStatusCode> StatusAsync(IResult result, HttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        return (HttpStatusCode)ctx.Response.StatusCode;
    }
}
