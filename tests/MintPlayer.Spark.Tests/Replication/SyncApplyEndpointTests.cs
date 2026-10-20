using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Endpoints;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// Owner, <c>POST /spark/sync/apply</c> (warehouse §4.2.5): the mTLS gate, one action per request, the status of every
/// outcome (never 207, F12) and op-id idempotency (F15). The compare-and-set itself is
/// <see cref="Services.SyncActionHandlerIntegrationTests"/>.
/// </summary>
public class SyncApplyEndpointTests : SparkTestDriver
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly IModuleCertificateValidator _certValidator = Substitute.For<IModuleCertificateValidator>();
    private readonly ISyncActionHandler _handler = Substitute.For<ISyncActionHandler>();
    private readonly IReplicaSourceTypes _sourceTypes = Substitute.For<IReplicaSourceTypes>();

    public SyncApplyEndpointTests()
    {
        _sourceTypes.Resolve("ReplSourceCars").Returns(typeof(ReplSourceCar));
        Cert(ModuleCertificateValidation.Ok);
    }

    private SyncApplyService Service() => new(Store, _sourceTypes, _handler, NullLogger<SyncApplyService>.Instance);

    private SyncApply NewEndpoint() => new(_certValidator, new HttpContextAccessor(), Service(), NullLogger<SyncApply>.Instance);

    private void Cert(ModuleCertificateValidation result) =>
        _certValidator.ValidateAsync(Arg.Any<HttpContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(result));

    private void Handler(SyncEditOutcome outcome) =>
        _handler.ApplyEditAsync(Arg.Any<SyncEdit>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(outcome));

    private static SyncApplyRequest Request(string opId = "op-1") => new()
    {
        RequestingModule = "HR",
        OpId = opId,
        ActionType = SyncActionType.Update,
        SourceCollection = "ReplSourceCars",
        DocumentId = "ReplSourceCars/1-A",
        Changes = [new SyncPropertyChange { Property = "LicensePlate", Base = JsonSerializer.SerializeToElement("old"), Value = JsonSerializer.SerializeToElement("new") }],
        InitiatorId = "SparkUsers/alice",
    };

    [Fact]
    public async Task Missing_certificate_returns_401()
    {
        Cert(ModuleCertificateValidation.MissingCertificate);
        (await StatusAsync(Request())).Should().Be(HttpStatusCode.Unauthorized);
        await _handler.DidNotReceiveWithAnyArgs().ApplyEditAsync(default!, default);
    }

    [Theory]
    [InlineData((int)ModuleCertificateValidation.UnknownModule)]
    [InlineData((int)ModuleCertificateValidation.ThumbprintMismatch)]
    public async Task A_module_that_is_not_who_it_says_is_403(int validation)
    {
        Cert((ModuleCertificateValidation)validation);
        (await StatusAsync(Request())).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_applied_edit_is_200_and_carries_bases_values_and_initiator()
    {
        Handler(new SyncEditOutcome(SyncEditOutcomeKind.Applied));

        (await StatusAsync(Request())).Should().Be(HttpStatusCode.OK);

        await _handler.Received(1).ApplyEditAsync(Arg.Is<SyncEdit>(e =>
            e.EntityType == typeof(ReplSourceCar) && e.DocumentId == "ReplSourceCars/1-A" && e.InitiatorId == "SparkUsers/alice"
            && e.Values["LicensePlate"].GetString() == "new" && e.BaseValues["LicensePlate"].GetString() == "old"), Arg.Any<CancellationToken>());
    }

    /// <summary>F12 red→green: a refusal used to be a 207 the consumer recorded as Completed.</summary>
    [Theory]
    [InlineData(SyncEditOutcomeKind.Conflict, SyncEditRefusal.Other, HttpStatusCode.Conflict)]
    [InlineData(SyncEditOutcomeKind.Refused, SyncEditRefusal.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(SyncEditOutcomeKind.Refused, SyncEditRefusal.Invalid, HttpStatusCode.UnprocessableEntity)]
    [InlineData(SyncEditOutcomeKind.Refused, SyncEditRefusal.Other, HttpStatusCode.BadRequest)]
    public async Task Every_outcome_has_its_own_status_never_207(SyncEditOutcomeKind kind, SyncEditRefusal refusal, HttpStatusCode expected)
    {
        Handler(new SyncEditOutcome(kind, "why") { Refusal = refusal, Attributes = ["LicensePlate"] });

        (await StatusAsync(Request())).Should().Be(expected);
    }

    /// <summary>F15: a resend after a lost answer returns the first outcome instead of a false conflict.</summary>
    [Fact]
    public async Task A_replayed_op_id_answers_the_recorded_outcome_without_applying_again()
    {
        Handler(new SyncEditOutcome(SyncEditOutcomeKind.Refused, "invalid plate") { Refusal = SyncEditRefusal.Invalid });
        (await StatusAsync(Request())).Should().Be(HttpStatusCode.UnprocessableEntity);

        Handler(new SyncEditOutcome(SyncEditOutcomeKind.Applied));
        var replay = await Service().ApplyAsync("HR", Request(), CancellationToken.None);

        replay.StatusCode.Should().Be(422);
        replay.Result.Reason.Should().Be("invalid plate");
        await _handler.Received(1).ApplyEditAsync(Arg.Any<SyncEdit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_applied_edit_records_its_op_id_with_the_edit()
    {
        _handler.ApplyEditAsync(Arg.Any<SyncEdit>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            // The handler commits Alongside with the edit; here a session of the test's own stands in.
            using var session = Store.OpenAsyncSession();
            await call.Arg<SyncEdit>().Alongside!(session);
            await session.SaveChangesAsync();
            return new SyncEditOutcome(SyncEditOutcomeKind.Applied);
        });

        await Service().ApplyAsync("HR", Request("op-applied"), CancellationToken.None);

        using var check = Store.OpenAsyncSession();
        var inbox = await check.LoadAsync<SyncInboxEntry>(SyncInboxEntry.For("HR", "ReplSourceCars/1-A"));
        inbox!.Recent.Should().ContainSingle(o => o.OpId == "op-applied" && o.Outcome == SyncApplyOutcome.Applied);
    }

    [Fact]
    public async Task An_unknown_collection_is_404()
    {
        var request = Request();
        request.SourceCollection = "Nope";
        (await Service().ApplyAsync("HR", request, CancellationToken.None)).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task An_update_without_changes_is_400()
    {
        var request = Request();
        request.Changes = [];
        (await Service().ApplyAsync("HR", request, CancellationToken.None)).StatusCode.Should().Be(400);
    }

    private async Task<HttpStatusCode> StatusAsync(SyncApplyRequest body)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        new HttpContextAccessor().HttpContext = ctx;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, Web));
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        ctx.Request.ContentLength = bytes.Length;
        ctx.Response.Body = new MemoryStream();
        var result = await NewEndpoint().HandleAsync(ctx);
        await result.ExecuteAsync(ctx);
        return (HttpStatusCode)ctx.Response.StatusCode;
    }
}
