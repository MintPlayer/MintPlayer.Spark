using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Venues;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Tests.Venues;

/// <summary>
/// Layouts against RavenDB (M14, D8): owned drafts with optimistic revisions, publication at once or through an
/// approval (modifications always), the approval handler applying the frozen proposal, copies, immutable snapshots
/// per scope, and provisioning a snapshot into the inventory engine (chains without accessible seats, block quotas).
/// </summary>
public class VenueLayoutLifecycleTests : SparkTestDriver
{
    private readonly FakeUser user = new() { Id = "users/owner" };
    private readonly FakePermissions permissions;
    private readonly FakeApprovals approvals = new();
    private readonly SparkTicketingVenuesOptions options = new();
    private readonly InventoryEngine engine;

    public VenueLayoutLifecycleTests()
    {
        permissions = new FakePermissions(user);
        engine = new InventoryEngine(Store);
    }

    private SparkVenues Venues() => new(Store, engine, approvals, new VenueAccess(user, permissions), new FixedMonitor(options), TimeProvider.System);

    private async Task<string> VenueAsync()
    {
        using var session = Store.OpenAsyncSession();
        var venue = new Venue { Name = "Hall", TimeZone = "Europe/Brussels" };
        await session.StoreAsync(venue);
        await session.SaveChangesAsync();
        return venue.Id!;
    }

    [Fact]
    public async Task The_first_version_publishes_at_once_and_a_modification_waits_for_an_approver()
    {
        var venues = Venues();
        var layout = await venues.CreateLayoutAsync(await VenueAsync(), "Concert");
        var revision = await venues.SaveDraftAsync(layout.LayoutId, layout.Revision, LayoutPlanTests.Plan());

        var first = await venues.PublishAsync(layout.LayoutId, revision);
        first.Outcome.Should().Be(VenueLayoutPublishOutcome.Published);
        first.PublishedVersion.Should().Be(1);
        approvals.Submitted.Should().BeEmpty();

        var changed = LayoutPlanTests.Plan();
        changed.Blocks[1].Capacity = 400;
        revision = await venues.SaveDraftAsync(layout.LayoutId, revision, changed);
        var second = await venues.PublishAsync(layout.LayoutId, revision);
        second.Outcome.Should().Be(VenueLayoutPublishOutcome.Pending);
        approvals.Submitted.Single().Data![LayoutPublishHandler.BaseVersionKey].Should().Be("1");

        // Editing the draft meanwhile does not change what is approved.
        var later = LayoutPlanTests.Plan();
        later.Blocks[1].Capacity = 999;
        await venues.SaveDraftAsync(layout.LayoutId, revision, later);

        await DecideAsync(layout.LayoutId, second.RequestId!, approve: true);
        var published = await venues.GetPlanAsync(layout.LayoutId, "published");
        published.Version.Should().Be(2);
        published.Plan.Blocks[1].Capacity.Should().Be(400);
        published.Status.Should().Be(VenueLayoutStatus.Published);
        (await venues.GetPlanAsync(layout.LayoutId, "1")).Plan.Blocks[1].Capacity.Should().Be(300);
    }

    [Fact]
    public async Task Creations_wait_too_when_configured_and_a_rejection_keeps_its_reason()
    {
        options.ApproveCreations = true;
        var venues = Venues();
        var layout = await venues.CreateLayoutAsync(await VenueAsync(), "Concert");
        var revision = await venues.SaveDraftAsync(layout.LayoutId, layout.Revision, LayoutPlanTests.Plan());
        var pending = await venues.PublishAsync(layout.LayoutId, revision);
        pending.Outcome.Should().Be(VenueLayoutPublishOutcome.Pending);

        // A second publication while one waits is refused.
        Func<Task> again = () => venues.PublishAsync(layout.LayoutId, revision);
        await again.Should().ThrowAsync<MintPlayer.Spark.Abstractions.SparkValidationException>();

        await DecideAsync(layout.LayoutId, pending.RequestId!, approve: false, reason: "Exits are missing");
        var draft = await venues.GetPlanAsync(layout.LayoutId, "draft");
        draft.Status.Should().Be(VenueLayoutStatus.Draft);
        draft.LastRejection.Should().Be("Exits are missing");
        draft.PublishedVersion.Should().Be(0);
    }

    [Fact]
    public async Task An_approver_publishes_without_a_request()
    {
        options.ApproveCreations = true;
        permissions.Approvers.Add(user.Id!);
        var venues = Venues();
        var layout = await venues.CreateLayoutAsync(await VenueAsync(), "Concert");
        var revision = await venues.SaveDraftAsync(layout.LayoutId, layout.Revision, LayoutPlanTests.Plan());
        (await venues.PublishAsync(layout.LayoutId, revision)).Outcome.Should().Be(VenueLayoutPublishOutcome.Published);
    }

    [Fact]
    public async Task Only_the_owner_edits_a_stale_revision_conflicts_and_others_copy_the_published_version()
    {
        var venues = Venues();
        var layout = await venues.CreateLayoutAsync(await VenueAsync(), "Concert");
        var revision = await venues.SaveDraftAsync(layout.LayoutId, layout.Revision, LayoutPlanTests.Plan());

        Func<Task> stale = () => venues.SaveDraftAsync(layout.LayoutId, layout.Revision, LayoutPlanTests.Plan());
        await stale.Should().ThrowAsync<VenueLayoutConflictException>();

        var bad = LayoutPlanTests.Plan();
        bad.Blocks[0].Polygon = [0, 0];
        Func<Task> invalid = () => venues.SaveDraftAsync(layout.LayoutId, revision, bad);
        await invalid.Should().ThrowAsync<VenueLayoutInvalidException>();

        user.Id = "users/other";
        Func<Task> foreign = () => venues.SaveDraftAsync(layout.LayoutId, revision, LayoutPlanTests.Plan());
        await foreign.Should().ThrowAsync<SparkAccessDeniedException>();
        Func<Task> draft = () => venues.GetPlanAsync(layout.LayoutId, "draft");
        await draft.Should().ThrowAsync<SparkAccessDeniedException>();
        Func<Task> unpublishedCopy = () => venues.CopyLayoutAsync(layout.LayoutId, "Mine");
        await unpublishedCopy.Should().ThrowAsync<SparkAccessDeniedException>();

        user.Id = "users/owner";
        await venues.PublishAsync(layout.LayoutId, revision);
        user.Id = "users/other";
        var copy = await venues.CopyLayoutAsync(layout.LayoutId, "Mine");
        var copied = await venues.GetPlanAsync(copy.LayoutId, "draft");
        copied.CanEdit.Should().BeTrue();
        copied.Plan.Blocks.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_snapshot_is_made_once_per_scope_and_never_follows_the_layout()
    {
        var venues = Venues();
        var layout = await venues.CreateLayoutAsync(await VenueAsync(), "Concert");
        var revision = await venues.SaveDraftAsync(layout.LayoutId, layout.Revision, LayoutPlanTests.Plan());
        await venues.PublishAsync(layout.LayoutId, revision);

        var id = await venues.SnapshotAsync(layout.LayoutId, "EventDates/1-A");
        (await venues.SnapshotAsync(layout.LayoutId, "EventDates/1-A")).Should().Be(id);

        permissions.Approvers.Add(user.Id!);
        var changed = LayoutPlanTests.Plan();
        changed.Blocks[1].Capacity = 10;
        revision = await venues.SaveDraftAsync(layout.LayoutId, revision, changed);
        (await venues.PublishAsync(layout.LayoutId, revision)).PublishedVersion.Should().Be(2);

        var snapshot = (await venues.GetSnapshotAsync(id))!;
        snapshot.LayoutVersion.Should().Be(1);
        snapshot.Plan.Blocks[1].Capacity.Should().Be(300);
        snapshot.VenueName.Should().Be("Hall");
        snapshot.TimeZone.Should().Be("Europe/Brussels");
        (await venues.GetSnapshotAsync(await venues.SnapshotAsync(layout.LayoutId, "EventDates/2-A")))!.LayoutVersion.Should().Be(2);
    }

    [Fact]
    public async Task A_provisioned_snapshot_sells_chained_seats_together_but_never_the_wheelchair_spaces()
    {
        var venues = Venues();
        var layout = await venues.CreateLayoutAsync(await VenueAsync(), "Concert");
        var revision = await venues.SaveDraftAsync(layout.LayoutId, layout.Revision, LayoutPlanTests.Plan());
        await venues.PublishAsync(layout.LayoutId, revision);
        var id = await venues.SnapshotAsync(layout.LayoutId, "EventDates/1-A");
        var map = await venues.ProvisionAsync(id);
        await venues.ProvisionAsync(id); // idempotent

        (await engine.GetAvailabilityAsync(map.QuotaId("P"))).Available.Should().Be(300);
        var held = await engine.TryClaimAsync(new ClaimRequest("claims/v1", "baskets/v1", DateTimeOffset.UtcNow.AddMinutes(10), [map.Places("P", 4)],
            BestAvailable: [map.BestAvailable("main", 2)]));
        held.Succeeded.Should().BeTrue();
        held.Units.Should().Equal(map.UnitId("A-1-3"), map.UnitId("A-1-4"));

        // Three together no longer fit in row 1 (two seats are wheelchair and companion): row 2.
        var next = await engine.TryClaimAsync(new ClaimRequest("claims/v2", "baskets/v2", DateTimeOffset.UtcNow.AddMinutes(10), [], BestAvailable: [map.BestAvailable("main", 3)]));
        next.Units.Should().Equal(map.UnitId("A-2-1"), map.UnitId("A-2-2"), map.UnitId("A-2-3"));

        var states = await engine.GetUnitStatesAsync([map.UnitId("A-1-1"), map.UnitId("A-1-3")]);
        states[map.UnitId("A-1-1")].Should().Be(UnitState.Free);
        states[map.UnitId("A-1-3")].Should().Be(UnitState.Held);
    }

    private async Task DecideAsync(string layoutId, string requestId, bool approve, string? reason = null)
    {
        using var session = Store.OpenAsyncSession();
        var context = new ApprovalDecisionContext
        {
            Request = new ApprovalRequestInfo
            {
                Id = requestId,
                Kind = LayoutPublishApproval.Name,
                Subject = new ApprovalSubject(VenueAccess.LayoutType, layoutId),
                Status = ApprovalStatuses.Pending,
                Title = "t",
                Data = approvals.Submitted.Last().Data!,
            },
            Status = approve ? ApprovalStatuses.Approved : ApprovalStatuses.Rejected,
            Reason = reason,
            Session = session,
        };
        var handler = new LayoutPublishHandler(TimeProvider.System);
        if (approve) await handler.ApproveAsync(context); else await handler.RejectAsync(context);
        context.IsStale.Should().BeFalse();
        await session.SaveChangesAsync();
    }

    private sealed class FakeUser : ISparkCurrentUser
    {
        public string? Id { get; set; }
        public bool IsAuthenticated => Id is not null;
    }

    private sealed class FakePermissions(FakeUser user) : IPermissionService
    {
        public HashSet<string> Approvers { get; } = [];
        public Task EnsureAuthorizedAsync(string action, string target, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsAllowedAsync(string action, string target, CancellationToken cancellationToken = default)
            => Task.FromResult(user.Id is { } id && Approvers.Contains(id));
    }

    private sealed class FakeApprovals : ISparkApprovals
    {
        public List<ApprovalSubmission> Submitted { get; } = [];

        public Task<ApprovalSubmitResult> SubmitAsync<TKind>(ApprovalSubmission submission, CancellationToken cancellationToken = default) where TKind : IApprovalKind
        {
            Submitted.Add(submission);
            return Task.FromResult(new ApprovalSubmitResult($"ApprovalRequests/{Submitted.Count}", true));
        }

        public Task<ApprovalSubmitResult> SubmitAsync<TKind>(IAsyncDocumentSession session, ApprovalSubmission submission, CancellationToken cancellationToken = default) where TKind : IApprovalKind
            => SubmitAsync<TKind>(submission, cancellationToken);

        public Task<ApprovalResult> DecideAsync(string requestId, ApprovalDecision decision, string? reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ApprovalResult> WithdrawAsync(string requestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ApprovalResult> RevokeAsync(string requestId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ApprovalRequestInfo>> GetPendingAsync<TKind>(ApprovalSubject? subject = null, CancellationToken cancellationToken = default) where TKind : IApprovalKind
            => Task.FromResult<IReadOnlyList<ApprovalRequestInfo>>([]);
        public Task<int> ClosePendingAsync<TKind>(IAsyncDocumentSession session, ApprovalSubject subject, CancellationToken cancellationToken = default) where TKind : IApprovalKind
            => Task.FromResult(0);
    }

    private sealed class FixedMonitor(SparkTicketingVenuesOptions value) : IOptionsMonitor<SparkTicketingVenuesOptions>
    {
        public SparkTicketingVenuesOptions CurrentValue => value;
        public SparkTicketingVenuesOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<SparkTicketingVenuesOptions, string?> listener) => null;
    }
}
