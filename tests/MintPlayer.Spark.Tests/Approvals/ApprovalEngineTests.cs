using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Approvals.Models;
using MintPlayer.Spark.Approvals.Proposals;
using MintPlayer.Spark.Approvals.Services;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Approvals;

// Fixture names start with "Ap": ActionsResolver matches actions classes by simple name across the whole assembly.
public class ApThing
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public bool Approved { get; set; }
    public int Rejections { get; set; }
    public int Expiries { get; set; }
}

public class ApInvoice
{
    public string? Id { get; set; }
    public string Reference { get; set; } = "";
}

public class ApInvoiceActions(IEntityMapper mapper) : DefaultPersistentObjectActions<ApInvoice>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: every row is visible; the subject is document proposals.";
}

public class ApContext : SparkContext
{
    public IRavenQueryable<ApInvoice> Invoices => Session.Query<ApInvoice>();
}

public sealed class ApTestApproval : IApprovalKind
{
    public static string Name => "tests.thing";
}

/// <summary>The signed-in user, switched by the test.</summary>
public sealed class ApUser : ISparkCurrentUser
{
    public string? Id { get; set; }
    public bool IsAuthenticated => Id is not null;
}

/// <summary>What the deciders answer, set by the test.</summary>
public sealed class ApDeciderRules
{
    public HashSet<string> Deciders { get; } = ["users/decider"];
    public bool StaleOnApprove { get; set; }
}

public sealed class ApDeciders(ApDeciderRules rules) : IApprovalDeciders<ApTestApproval>
{
    public ValueTask<bool> MayDecideAsync(ApprovalDeciderContext context) => ValueTask.FromResult(rules.Deciders.Contains(context.UserId));

    public ValueTask<IReadOnlyCollection<string>> GetDeciderIdsAsync(ApprovalSubject subject, IAsyncDocumentSession session, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyCollection<string>>(rules.Deciders.ToList());
}

public sealed class ApHandler(ApDeciderRules rules) : IApprovalHandler<ApTestApproval>
{
    public async Task ApproveAsync(ApprovalDecisionContext context)
    {
        var thing = await context.Session.LoadAsync<ApThing>(context.Request.Subject.Id, context.CancellationToken);
        thing.Approved = true;
        if (rules.StaleOnApprove)
            context.MarkStale();
    }

    public async Task RejectAsync(ApprovalDecisionContext context)
        => (await context.Session.LoadAsync<ApThing>(context.Request.Subject.Id, context.CancellationToken)).Rejections++;

    public async Task ExpireAsync(ApprovalDecisionContext context)
        => (await context.Session.LoadAsync<ApThing>(context.Request.Subject.Id, context.CancellationToken)).Expiries++;
}

/// <summary>
/// Commerce D9 (PRD §4.13): the decision-request engine. A decision runs the kind's handler in the request's own
/// session and commits it with the new status, guarded by the version decided on; the requester never decides; a
/// rejection needs a reason; overdue requests expire through the sweep; document proposals divert a governed save
/// (202) and apply it as the approver.
/// </summary>
public class ApprovalEngineTests : SparkTestDriver
{
    private static readonly Guid InvoiceTypeId = Guid.Parse("a9900000-0000-4000-8000-a99000000001");
    private const string ThingId = "apthings/1";
    private const string InvoiceId = "apinvoices/1";
    private const string Requester = "users/requester";
    private const string Decider = "users/decider";
    private const string Approver = "users/approver";

    private readonly ApUser user = new() { Id = Requester };
    private readonly ApDeciderRules rules = new();
    private readonly List<IAsyncDisposable> factories = [];
    private readonly List<SparkClient> clients = [];
    private SparkEndpointFactory<ApContext> factory = null!;

    private static EntityTypeFile InvoiceModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = InvoiceTypeId,
            Name = "ApInvoice",
            ClrType = typeof(ApInvoice).FullName!,
            Breadcrumb = "{Reference}",
            Attributes = [new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Reference", DataType = "string", Order = 1 }],
        },
    };

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await SeedAsync(async session =>
        {
            await session.StoreAsync(new ApThing { Name = "Thing" }, ThingId);
            await session.StoreAsync(new ApInvoice { Reference = "INV-1" }, InvoiceId);
        });

        factory = new SparkEndpointFactory<ApContext>(
            Store,
            [InvoiceModel()],
            configureServices: services =>
            {
                services.RemoveAll<ISparkCurrentUser>();
                services.AddSingleton<ISparkCurrentUser>(user);
                services.AddSingleton(rules);
                services.AddScoped<ApInvoiceActions>();
                services.AddScoped<IApprovalDeciders<ApTestApproval>, ApDeciders>();
                services.AddScoped<IApprovalHandler<ApTestApproval>, ApHandler>();
                // Only the approver may decide proposals of ApInvoice; everything else is allowed.
                services.UseSparkTestAccessControl(SparkTestAccessControl.Matching(resource =>
                    !resource.StartsWith(ApprovalRights.ApproveProposal + "/", StringComparison.OrdinalIgnoreCase) || user.Id == Approver));
            },
            configureSpark: spark => spark.AddApprovals(o =>
            {
                o.SweepSchedule = "0 0 1 1 *"; // the tests run the sweep themselves
                o.Types["ApInvoice"] = new SparkApprovalTypeOptions { Modify = true, Create = true };
            }));
        factories.Add(factory);
    }

    public override async Task DisposeAsync()
    {
        foreach (var client in clients)
            client.Dispose();
        foreach (var f in factories)
            await f.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<T> AsAsync<T>(string? userId, Func<IServiceProvider, Task<T>> act)
    {
        user.Id = userId;
        using var scope = factory.CreateScope();
        return await act(scope.ServiceProvider);
    }

    private Task<ApprovalSubmitResult> SubmitAsync(bool replacePending = false, TimeSpan? decideWithin = null)
        => AsAsync(Requester, sp => sp.GetRequiredService<ISparkApprovals>().SubmitAsync<ApTestApproval>(new ApprovalSubmission
        {
            Subject = new ApprovalSubject("ApThing", ThingId),
            Title = "Approve the thing",
            ReplacePending = replacePending,
            DecideWithin = decideWithin,
        }));

    private Task<ApprovalResult> DecideAsync(string? userId, string requestId, ApprovalDecision decision, string? reason = null)
        => AsAsync(userId, sp => sp.GetRequiredService<ISparkApprovals>().DecideAsync(requestId, decision, reason));

    private async Task<ApprovalRequest> LoadRequestAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<ApprovalRequest>(id))!;
    }

    private async Task<ApThing> LoadThingAsync()
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<ApThing>(ThingId))!;
    }

    [Fact]
    public async Task A_submitted_request_is_pending_lists_its_deciders_and_is_answered_again_while_pending()
    {
        var first = await SubmitAsync();
        var again = await SubmitAsync();

        first.Created.Should().BeTrue();
        again.Should().Be(first with { Created = false }, "one pending request per kind and subject");
        var request = await LoadRequestAsync(first.RequestId);
        request.Status.Should().Be(ApprovalStatuses.Pending);
        request.Kind.Should().Be(ApTestApproval.Name);
        request.RequestedBy.Should().Be(Requester);
        request.DeciderIds.Should().Equal(Decider);
        request.NotifiedAtUtc.HasValue.Should().BeTrue("a request raised in its own session tells its deciders at once");
        request.History.Should().ContainSingle().Which.Status.Should().Be(ApprovalStatuses.Pending);
    }

    [Fact]
    public async Task Replacing_a_pending_request_withdraws_it()
    {
        var first = await SubmitAsync();
        var second = await SubmitAsync(replacePending: true);

        second.RequestId.Should().NotBe(first.RequestId);
        (await LoadRequestAsync(first.RequestId)).Status.Should().Be(ApprovalStatuses.Withdrawn);
        (await LoadRequestAsync(second.RequestId)).Status.Should().Be(ApprovalStatuses.Pending);
    }

    [Fact]
    public async Task Approving_runs_the_handler_in_the_same_commit_as_the_status()
    {
        var submitted = await SubmitAsync();

        var result = await DecideAsync(Decider, submitted.RequestId, ApprovalDecision.Approve);

        result.Outcome.Should().Be(ApprovalOutcome.Done);
        result.Status.Should().Be(ApprovalStatuses.Approved);
        (await LoadThingAsync()).Approved.Should().BeTrue();
        var request = await LoadRequestAsync(submitted.RequestId);
        request.Status.Should().Be(ApprovalStatuses.Approved);
        request.DecidedBy.Should().Be(Decider);
        request.History.Select(e => e.Status).Should().Equal(ApprovalStatuses.Pending, ApprovalStatuses.Approved);
    }

    [Fact]
    public async Task The_requester_never_decides_their_own_request_even_when_the_gate_would_let_them()
    {
        rules.Deciders.Add(Requester);
        var submitted = await SubmitAsync();

        var result = await DecideAsync(Requester, submitted.RequestId, ApprovalDecision.Approve);

        result.Outcome.Should().Be(ApprovalOutcome.NotFound, "the uniform refusal: no self-approval");
        (await LoadThingAsync()).Approved.Should().BeFalse();
        (await LoadRequestAsync(submitted.RequestId)).Status.Should().Be(ApprovalStatuses.Pending);
    }

    [Fact]
    public async Task Someone_the_live_gate_refuses_gets_the_uniform_refusal()
    {
        var submitted = await SubmitAsync();

        (await DecideAsync("users/stranger", submitted.RequestId, ApprovalDecision.Approve)).Outcome.Should().Be(ApprovalOutcome.NotFound);
        (await DecideAsync(Decider, "ApprovalRequests/does-not-exist", ApprovalDecision.Approve)).Outcome.Should().Be(ApprovalOutcome.NotFound);
        (await LoadThingAsync()).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task A_decider_who_lost_the_role_cannot_decide_any_more()
    {
        var submitted = await SubmitAsync();
        rules.Deciders.Clear();

        (await DecideAsync(Decider, submitted.RequestId, ApprovalDecision.Approve)).Outcome.Should().Be(ApprovalOutcome.NotFound,
            "the gate is asked at decision time, not read from the deciders listed at submission");
    }

    [Fact]
    public async Task A_rejection_needs_a_reason_and_stores_it()
    {
        var submitted = await SubmitAsync();

        (await DecideAsync(Decider, submitted.RequestId, ApprovalDecision.Reject, "  ")).Outcome.Should().Be(ApprovalOutcome.ReasonRequired);
        var result = await DecideAsync(Decider, submitted.RequestId, ApprovalDecision.Reject, "Not now");

        result.Outcome.Should().Be(ApprovalOutcome.Done);
        var request = await LoadRequestAsync(submitted.RequestId);
        request.Status.Should().Be(ApprovalStatuses.Rejected);
        request.Reason.Should().Be("Not now");
        (await LoadThingAsync()).Rejections.Should().Be(1);
    }

    [Fact]
    public async Task A_second_decision_is_not_pending_and_runs_nothing()
    {
        var submitted = await SubmitAsync();
        await DecideAsync(Decider, submitted.RequestId, ApprovalDecision.Approve);

        var second = await DecideAsync(Decider, submitted.RequestId, ApprovalDecision.Reject, "Changed my mind");

        second.Outcome.Should().Be(ApprovalOutcome.NotPending);
        second.Status.Should().Be(ApprovalStatuses.Approved);
        (await LoadThingAsync()).Rejections.Should().Be(0);
    }

    [Fact]
    public async Task Only_the_requester_withdraws()
    {
        var submitted = await SubmitAsync();

        (await AsAsync(Decider, sp => sp.GetRequiredService<ISparkApprovals>().WithdrawAsync(submitted.RequestId))).Outcome
            .Should().Be(ApprovalOutcome.NotFound);
        (await AsAsync(Requester, sp => sp.GetRequiredService<ISparkApprovals>().WithdrawAsync(submitted.RequestId))).Outcome
            .Should().Be(ApprovalOutcome.Done);
        (await LoadRequestAsync(submitted.RequestId)).Status.Should().Be(ApprovalStatuses.Withdrawn);
    }

    [Fact]
    public async Task A_handler_that_finds_the_subject_changed_makes_the_request_stale_and_commits_nothing_of_its_own()
    {
        rules.StaleOnApprove = true;
        var submitted = await SubmitAsync();

        var result = await DecideAsync(Decider, submitted.RequestId, ApprovalDecision.Approve);

        result.Outcome.Should().Be(ApprovalOutcome.Stale);
        (await LoadRequestAsync(submitted.RequestId)).Status.Should().Be(ApprovalStatuses.Stale);
        (await LoadThingAsync()).Approved.Should().BeFalse("what the handler stored before marking stale is not committed");
    }

    [Fact]
    public async Task The_sweep_expires_an_overdue_request_through_its_handler()
    {
        var submitted = await SubmitAsync(decideWithin: TimeSpan.FromSeconds(-1));

        (await DecideAsync(Decider, submitted.RequestId, ApprovalDecision.Approve)).Outcome
            .Should().Be(ApprovalOutcome.NotPending, "past its deadline it is expired already, whether or not the sweep ran");
        await AsAsync<object?>(null, async sp => { await sp.GetRequiredService<SparkApprovals>().SweepAsync(CancellationToken.None); return null; });

        (await LoadRequestAsync(submitted.RequestId)).Status.Should().Be(ApprovalStatuses.Expired);
        (await LoadThingAsync()).Expiries.Should().Be(1);
    }

    [Fact]
    public async Task A_request_raised_inside_another_session_commits_with_it_and_is_told_by_the_sweep()
    {
        string requestId;
        user.Id = Requester;
        using (var scope = factory.CreateScope())
        {
            using var session = Store.OpenAsyncSession();
            var result = await scope.ServiceProvider.GetRequiredService<ISparkApprovals>().SubmitAsync<ApTestApproval>(session, new ApprovalSubmission
            {
                Subject = new ApprovalSubject("ApThing", ThingId),
                Title = "Inside a save",
            });
            requestId = result.RequestId;
            using (var other = Store.OpenAsyncSession())
                (await other.LoadAsync<ApprovalRequest>(requestId)).Should().BeNull("nothing is written before the caller's session commits");
            await session.SaveChangesAsync();
        }

        (await LoadRequestAsync(requestId)).NotifiedAtUtc.HasValue.Should().BeFalse();
        await AsAsync<object?>(null, async sp => { await sp.GetRequiredService<SparkApprovals>().SweepAsync(CancellationToken.None); return null; });
        (await LoadRequestAsync(requestId)).NotifiedAtUtc.HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task A_request_is_visible_to_its_requester_its_deciders_and_VisibleTo_only()
    {
        user.Id = Requester;
        ApprovalSubmitResult submitted;
        using (var scope = factory.CreateScope())
            submitted = await scope.ServiceProvider.GetRequiredService<ISparkApprovals>().SubmitAsync<ApTestApproval>(new ApprovalSubmission
            {
                Subject = new ApprovalSubject("ApThing", ThingId),
                Title = "Visible",
                VisibleTo = ["users/teammate"],
            });
        var request = await LoadRequestAsync(submitted.RequestId);

        foreach (var (who, sees) in new[] { (Requester, true), (Decider, true), ("users/teammate", true), ("users/stranger", false), ((string?)null, false) })
        {
            var visible = await AsAsync(who, sp => sp.GetRequiredService<ApprovalVisibility>().MaySeeAsync(request));
            visible.Should().Be(sees, $"{who ?? "an anonymous caller"} {(sees ? "sees" : "does not see")} it");
        }
    }

    [Fact]
    public async Task A_governed_edit_is_diverted_into_a_proposal_and_applied_by_the_approver_on_the_proposers_behalf()
    {
        user.Id = Requester;
        var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        clients.Add(client);
        var po = await client.GetPersistentObjectAsync(InvoiceTypeId, InvoiceId);
        po!["Reference"].Value = "INV-1b";
        po["Reference"].IsValueChanged = true;

        var ex = (await new Func<Task>(() => client.UpdatePersistentObjectAsync(po)).Should().ThrowExactlyAsync<SparkClientException>()).Which;

        ex.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var receipt = JsonDocument.Parse(ex.ResponseBody!);
        var requestId = receipt.RootElement.GetProperty("submitted").GetProperty("referenceId").GetString()!;
        var request = await LoadRequestAsync(requestId);
        request.Kind.Should().Be(DocumentProposalApproval.Name);
        request.Proposal!.Operation.Should().Be(ApprovalProposalOperations.Modify);
        using (var session = Store.OpenAsyncSession())
            (await session.LoadAsync<ApInvoice>(InvoiceId)).Reference.Should().Be("INV-1", "nothing is written until approved");

        (await DecideAsync(Decider, requestId, ApprovalDecision.Approve)).Outcome
            .Should().Be(ApprovalOutcome.NotFound, "only holders of ApproveProposal/ApInvoice decide its proposals");
        var applied = await DecideAsync(Approver, requestId, ApprovalDecision.Approve);

        applied.Outcome.Should().Be(ApprovalOutcome.Done);
        using (var session = Store.OpenAsyncSession())
            (await session.LoadAsync<ApInvoice>(InvoiceId)).Reference.Should().Be("INV-1b");
        (await LoadRequestAsync(requestId)).Status.Should().Be(ApprovalStatuses.Approved);
    }

    [Fact]
    public async Task A_proposal_whose_base_changed_goes_stale_and_writes_nothing()
    {
        user.Id = Requester;
        var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        clients.Add(client);
        var po = await client.GetPersistentObjectAsync(InvoiceTypeId, InvoiceId);
        po!["Reference"].Value = "INV-1b";
        po["Reference"].IsValueChanged = true;
        var ex = (await new Func<Task>(() => client.UpdatePersistentObjectAsync(po)).Should().ThrowExactlyAsync<SparkClientException>()).Which;
        using var receipt = JsonDocument.Parse(ex.ResponseBody!);
        var requestId = receipt.RootElement.GetProperty("submitted").GetProperty("referenceId").GetString()!;

        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<ApInvoice>(InvoiceId)).Reference = "someone else";
            await session.SaveChangesAsync();
        }

        var result = await DecideAsync(Approver, requestId, ApprovalDecision.Approve);

        result.Outcome.Should().Be(ApprovalOutcome.Stale);
        result.ConflictReason.Should().Be("changed");
        (await LoadRequestAsync(requestId)).Status.Should().Be(ApprovalStatuses.Stale);
        using (var check = Store.OpenAsyncSession())
            (await check.LoadAsync<ApInvoice>(InvoiceId)).Reference.Should().Be("someone else");
    }

    [Fact]
    public async Task An_approvers_own_save_of_a_governed_type_is_not_a_proposal()
    {
        user.Id = Approver;
        using var scope = factory.CreateScope();
        var databaseAccess = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
        var po = await databaseAccess.GetPersistentObjectAsync(InvoiceTypeId, InvoiceId);
        po!["Reference"].Value = "INV-direct";
        po["Reference"].IsValueChanged = true;

        var saved = await databaseAccess.SavePersistentObjectAsync(po);

        saved.Replacement.Should().BeNull();
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<ApInvoice>(InvoiceId)).Reference.Should().Be("INV-direct");
    }

    [Fact]
    public void A_kind_without_a_handler_or_with_two_deciders_is_a_startup_problem()
    {
        var services = new ServiceCollection();
        services.AddScoped<IApprovalDeciders<ApTestApproval>, ApDeciders>();
        services.AddScoped<IApprovalDeciders<ApTestApproval>, ApDeciders2>();

        var problems = new ApprovalKindCatalog(services).Problems();

        problems.Should().Contain(p => p.Contains("has 2 implementations"));
        problems.Should().Contain(p => p.Contains("without IApprovalHandler"));
    }

    private sealed class ApDeciders2 : IApprovalDeciders<ApTestApproval>
    {
        public ValueTask<bool> MayDecideAsync(ApprovalDeciderContext context) => ValueTask.FromResult(false);
    }
}
