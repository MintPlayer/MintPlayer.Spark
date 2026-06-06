using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Replication.Endpoints;
using MintPlayer.Spark.Replication.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// In-process coverage for the /spark/etl/deploy body-validation and mTLS-gate branches.
/// The happy path runs the concrete EtlTaskManager (real RavenDB ETL) and stays in E2E;
/// the rejection paths return before EtlTaskManager is touched, so a null is safe here.
/// </summary>
public class EtlDeployEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly IModuleCertificateValidator _certValidator = Substitute.For<IModuleCertificateValidator>();

    private EtlDeploy NewEndpoint() =>
        new(NullLogger<EtlTaskManager>.Instance, null!, _certValidator);

    private void Cert(ModuleCertificateValidation result) =>
        _certValidator.ValidateAsync(Arg.Any<HttpContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));

    [Fact]
    public async Task Invalid_json_body_returns_400()
    {
        var ctx = NewContext("{ not json");
        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx)).Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Empty_scripts_list_returns_400()
    {
        var ctx = NewContext(new { requestingModule = "HR", scripts = Array.Empty<object>() });
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

    private static object DeployBody(string module) => new
    {
        requestingModule = module,
        targetDatabase = "victim",
        targetUrls = new[] { "http://owner.example/raven" },
        scripts = new[] { new { sourceCollection = "Users", script = "loadToUsers({ Email: this.Email })" } },
    };

    private static DefaultHttpContext NewContext(object body) => NewContext(JsonSerializer.Serialize(body, Web));

    private static DefaultHttpContext NewContext(string json)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
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
