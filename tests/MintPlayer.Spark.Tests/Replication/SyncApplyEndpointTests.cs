using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Replication.Endpoints;
using MintPlayer.Spark.Replication.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// In-process coverage for the /spark/sync/apply endpoint. The E2E suite exercises it
/// against a real Fleet subprocess, but that runs out-of-process so it contributes no
/// line coverage — these tests drive HandleAsync directly with mocked collaborators.
/// </summary>
public class SyncApplyEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly IModuleCertificateValidator _certValidator = Substitute.For<IModuleCertificateValidator>();
    private readonly ISyncActionHandler _handler = Substitute.For<ISyncActionHandler>();

    private SyncApply NewEndpoint(bool withHandler = true) =>
        new(NullLoggerFactory.Instance, _certValidator, withHandler ? _handler : null);

    private void Cert(ModuleCertificateValidation result) =>
        _certValidator.ValidateAsync(Arg.Any<HttpContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));

    [Fact]
    public async Task Invalid_json_body_returns_400()
    {
        var ctx = NewContext("{ this is not json");
        var result = await NewEndpoint().HandleAsync(ctx);
        (await StatusAsync(result, ctx)).Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Empty_actions_list_returns_400()
    {
        var ctx = NewContext(new { requestingModule = "HR", actions = Array.Empty<object>() });
        var result = await NewEndpoint().HandleAsync(ctx);
        (await StatusAsync(result, ctx)).Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Missing_certificate_returns_401()
    {
        Cert(ModuleCertificateValidation.MissingCertificate);
        var ctx = NewContext(DeleteBody("HR"));
        var result = await NewEndpoint().HandleAsync(ctx);
        (await StatusAsync(result, ctx)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_module_returns_403()
    {
        Cert(ModuleCertificateValidation.UnknownModule);
        var ctx = NewContext(DeleteBody("Attacker-Not-Registered"));
        var result = await NewEndpoint().HandleAsync(ctx);
        (await StatusAsync(result, ctx)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Thumbprint_mismatch_returns_403()
    {
        Cert(ModuleCertificateValidation.ThumbprintMismatch);
        var ctx = NewContext(DeleteBody("HR"));
        var result = await NewEndpoint().HandleAsync(ctx);
        (await StatusAsync(result, ctx)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Authorized_call_with_no_registered_handler_returns_500()
    {
        Cert(ModuleCertificateValidation.Ok);
        var ctx = NewContext(DeleteBody("HR"));
        var result = await NewEndpoint(withHandler: false).HandleAsync(ctx);
        (await StatusAsync(result, ctx)).Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Delete_action_in_string_enum_form_binds_and_invokes_handler_returning_200()
    {
        // Regression for the JsonStringEnumConverter<SyncActionType> change: the worker
        // now serializes actionType as "Delete" (string), and it must bind + dispatch.
        Cert(ModuleCertificateValidation.Ok);
        var ctx = NewContext(new
        {
            requestingModule = "HR",
            actions = new[] { new { actionType = "Delete", collection = "Cars", documentId = "cars/1" } },
        });

        var result = await NewEndpoint().HandleAsync(ctx);

        await _handler.Received(1).HandleDeleteAsync("Cars", "cars/1");
        (await StatusAsync(result, ctx)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Insert_without_data_is_reported_as_partial_failure_207()
    {
        Cert(ModuleCertificateValidation.Ok);
        var ctx = NewContext(new
        {
            requestingModule = "HR",
            actions = new[] { new { actionType = "Insert", collection = "Cars", documentId = (string?)null } },
        });

        var result = await NewEndpoint().HandleAsync(ctx);

        await _handler.DidNotReceive().HandleSaveAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object?>>(), Arg.Any<string[]?>());
        (await StatusAsync(result, ctx)).Should().Be((HttpStatusCode)207);
    }

    [Fact]
    public async Task Insert_with_data_saves_and_returns_200()
    {
        Cert(ModuleCertificateValidation.Ok);
        _handler.HandleSaveAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object?>>(), Arg.Any<string[]?>())
            .Returns(Task.FromResult<string?>("cars/generated"));

        var ctx = NewContext(new
        {
            requestingModule = "HR",
            actions = new[]
            {
                new { actionType = "Insert", collection = "Cars", documentId = (string?)null, data = new { Plate = "ABC-123" } },
            },
        });

        var result = await NewEndpoint().HandleAsync(ctx);

        // Key casing follows the wire JSON (serialized camelCase here), so assert on
        // payload presence rather than an exact key name — the point is that the data
        // dictionary flowed through to the handler.
        await _handler.Received(1).HandleSaveAsync("Cars", null,
            Arg.Is<Dictionary<string, object?>>(d => d.Count > 0), Arg.Any<string[]?>());
        (await StatusAsync(result, ctx)).Should().Be(HttpStatusCode.OK);
    }

    private static object DeleteBody(string module) => new
    {
        requestingModule = module,
        actions = new[] { new { actionType = "Delete", collection = "Cars", documentId = "cars/1" } },
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
