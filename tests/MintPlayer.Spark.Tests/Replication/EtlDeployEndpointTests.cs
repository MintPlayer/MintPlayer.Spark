using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
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
    private readonly IPermissionService _permissionService = Substitute.For<IPermissionService>();

    private EtlDeploy NewEndpoint() =>
        new(NullLogger<EtlTaskManager>.Instance, null!, _certValidator, _permissionService);

    /// <summary>Grants the caller the right to replicate every collection it asks for.</summary>
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

    /// <summary>
    /// The read-authorization gap, now closed. A certificate answers "who are you"; it never
    /// answered "what may you read", and the mTLS gate was the only gate — so any module holding a
    /// valid pinned certificate could ask the owner to push <b>any</b> collection into a database it
    /// controls, continuously, through a caller-supplied JavaScript transform.
    /// <para>
    /// The example is the one that matters: <c>SparkUsers</c>. The requesting module's
    /// <c>[Replicated]</c> attributes constrain nothing here — they live on the consumer and the
    /// owner never sees them, so "what gets replicated" was entirely the requester's say-so.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_module_may_not_replicate_a_collection_it_has_no_right_to()
    {
        Cert(ModuleCertificateValidation.Ok);
        _permissionService.IsAllowedAsync("Replicate", "SparkUsers", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var ctx = NewContext(new
        {
            requestingModule = "HR",
            targetDatabase = "hr-db",
            targetUrls = new[] { "http://hr.example/raven" },
            scripts = new[] { new { sourceCollection = "SparkUsers", script = "loadToSparkUsers(this)" } },
        });

        (await StatusAsync(await NewEndpoint().HandleAsync(ctx), ctx))
            .Should().Be(HttpStatusCode.Forbidden,
                "an authenticated module is not thereby entitled to every collection the owner holds");
    }

    /// <summary>
    /// The control. Without it the test above passes for any reason at all — including the endpoint
    /// refusing every deployment — and would not show that the check is what refused.
    /// </summary>
    [Fact]
    public async Task A_module_may_replicate_a_collection_it_is_granted()
    {
        Cert(ModuleCertificateValidation.Ok);
        AllowReplication();

        var ctx = NewContext(DeployBody("HR"));

        // EtlTaskManager is null here, so reaching it throws rather than returning 403: passing the
        // authorization check is exactly what this asserts.
        var act = async () => await NewEndpoint().HandleAsync(ctx);

        await act.Should().ThrowAsync<NullReferenceException>(
            "a granted collection must pass the read check and proceed to deployment");
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
