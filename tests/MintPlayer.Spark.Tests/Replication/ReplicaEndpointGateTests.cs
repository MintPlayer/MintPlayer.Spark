using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Endpoints;
using MintPlayer.Spark.Replication.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// The mTLS gate of <c>/spark/replicas/register</c> and <c>/spark/replicas/apply</c> (R2-C1/C2): no certificate is 401,
/// a module that is not who it says is 403, and nothing behind the gate runs for either. Reworked from
/// <c>EtlDeployEndpointTests</c>.
/// </summary>
public class ReplicaEndpointGateTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly IModuleCertificateValidator _certValidator = Substitute.For<IModuleCertificateValidator>();
    private readonly IReplicaSubscriptions _subscriptions = Substitute.For<IReplicaSubscriptions>();
    private readonly IReplicaApplier _applier = Substitute.For<IReplicaApplier>();

    private void Cert(ModuleCertificateValidation result) =>
        _certValidator.ValidateAsync(Arg.Any<HttpContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(result));

    [Theory]
    [InlineData((int)ModuleCertificateValidation.MissingCertificate, HttpStatusCode.Unauthorized)]
    [InlineData((int)ModuleCertificateValidation.UnknownModule, HttpStatusCode.Forbidden)]
    [InlineData((int)ModuleCertificateValidation.ThumbprintMismatch, HttpStatusCode.Forbidden)]
    public async Task Register_refuses_a_caller_without_its_pinned_certificate(int validation, HttpStatusCode expected)
    {
        Cert((ModuleCertificateValidation)validation);
        var endpoint = new ReplicaRegister(_certValidator, new HttpContextAccessor(), _subscriptions, NullLogger<ReplicaRegister>.Instance);

        (await StatusAsync(ctx => endpoint.HandleAsync(ctx), new ReplicaRegistrationRequest { RequestingModule = "HR", Items = [ReplicationFixtures.Item()] }))
            .Should().Be(expected);
        await _subscriptions.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default!, default);
    }

    [Fact]
    public async Task Register_answers_the_owners_status()
    {
        Cert(ModuleCertificateValidation.Ok);
        _subscriptions.RegisterAsync("HR", Arg.Any<IReadOnlyList<ReplicaRegistrationItem>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ReplicaRegisterOutcome(403, new ReplicaRegistrationResult { Error = "Forbidden" })));
        var endpoint = new ReplicaRegister(_certValidator, new HttpContextAccessor(), _subscriptions, NullLogger<ReplicaRegister>.Instance);

        (await StatusAsync(ctx => endpoint.HandleAsync(ctx), new ReplicaRegistrationRequest { RequestingModule = "HR", Items = [ReplicationFixtures.Item()] }))
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData((int)ModuleCertificateValidation.MissingCertificate, HttpStatusCode.Unauthorized)]
    [InlineData((int)ModuleCertificateValidation.ThumbprintMismatch, HttpStatusCode.Forbidden)]
    public async Task Apply_refuses_a_caller_without_its_pinned_certificate(int validation, HttpStatusCode expected)
    {
        Cert((ModuleCertificateValidation)validation);
        var endpoint = new ReplicaApply(_certValidator, new HttpContextAccessor(), _applier, NullLogger<ReplicaApply>.Instance);

        (await StatusAsync(ctx => endpoint.HandleAsync(ctx), new ReplicaApplyRequest { SourceModule = "Fleet", TargetCollection = "ReplicaCars", Hash = "h" }))
            .Should().Be(expected);
        await _applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default);
    }

    private static async Task<HttpStatusCode> StatusAsync(Func<HttpContext, Task<IResult>> handle, object body)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        new HttpContextAccessor().HttpContext = ctx;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, Web));
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        ctx.Request.ContentLength = bytes.Length;
        ctx.Response.Body = new MemoryStream();
        await (await handle(ctx)).ExecuteAsync(ctx);
        return (HttpStatusCode)ctx.Response.StatusCode;
    }
}
