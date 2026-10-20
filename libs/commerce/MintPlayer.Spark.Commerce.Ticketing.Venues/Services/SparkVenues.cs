using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Services;

/// <summary>The draft changed since the caller loaded it (another tab, or a CSV import): reload and redo.</summary>
public sealed class VenueLayoutConflictException(int currentRevision) : Exception("The layout's draft changed since it was loaded.")
{
    public int CurrentRevision { get; } = currentRevision;
}

/// <summary>A plan or a CSV file has problems; nothing was saved.</summary>
public sealed class VenueLayoutInvalidException(IReadOnlyList<LayoutProblem> problems) : Exception("The layout has problems.")
{
    public IReadOnlyList<LayoutProblem> Problems { get; } = problems;
}

/// <summary>
/// Venues and layouts (PRD §4.11, D8, D32): the service behind <c>/spark/ticketing/venues/*</c> and the seam
/// Ticketing.Events uses to freeze a layout for a date. Refusals throw <see cref="SparkAccessDeniedException"/> (the
/// endpoints answer the uniform 404), stale saves <see cref="VenueLayoutConflictException"/>, bad plans
/// <see cref="VenueLayoutInvalidException"/>.
/// </summary>
public interface ISparkVenues
{
    /// <summary>A new private layout of <paramref name="venueId"/>, owned by the caller, with an empty draft.</summary>
    Task<VenueLayoutRef> CreateLayoutAsync(string venueId, string name, CancellationToken cancellationToken = default);

    /// <summary>A private copy of a layout's latest published version (or of one's own draft), owned by the caller (D8: "copied to edit").</summary>
    Task<VenueLayoutRef> CopyLayoutAsync(string layoutId, string name, CancellationToken cancellationToken = default);

    /// <summary>A plan: <c>draft</c> and <c>proposal</c> for the owner and approvers, <c>published</c> or a version number for every signed-in user.</summary>
    Task<VenueLayoutPlanResponse> GetPlanAsync(string layoutId, string version, CancellationToken cancellationToken = default);

    /// <summary>Replaces the draft (the owner only), when <paramref name="revision"/> is still the current one.</summary>
    Task<int> SaveDraftAsync(string layoutId, int revision, LayoutPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Merges a CSV of seats into the draft (D32).</summary>
    Task<int> ImportCsvAsync(string layoutId, int revision, string csv, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes the draft as the next version: at once for an approver, and for a first version unless
    /// <c>ApproveCreations</c>; otherwise through an approval request (D8: modifications always).
    /// </summary>
    Task<VenueLayoutPublishResponse> PublishAsync(string layoutId, int revision, CancellationToken cancellationToken = default);

    /// <summary>The visual diff of the pending proposal (or the draft) against the published version.</summary>
    Task<LayoutDiffResult> DiffAsync(string layoutId, CancellationToken cancellationToken = default);

    /// <summary>Stores an uploaded background image for the caller's layout; answers its id for <see cref="LayoutBackground.ImageId"/>.</summary>
    Task<string> UploadBackgroundAsync(string layoutId, Stream content, string contentType, long length, CancellationToken cancellationToken = default);

    /// <summary>
    /// Freezes a published version of a layout for <paramref name="scope"/> (a date id): the snapshot a sale uses, never
    /// changed again (§4.11). Idempotent per scope: a second call answers the same snapshot. System call (no caller
    /// check): Ticketing.Events decides who may publish a date.
    /// </summary>
    Task<string> SnapshotAsync(string layoutId, string scope, int? version = null, CancellationToken cancellationToken = default);

    Task<VenueLayoutSnapshot?> GetSnapshotAsync(string snapshotId, CancellationToken cancellationToken = default);

    /// <summary>Defines the snapshot's chains and block quotas in the inventory engine (idempotent) and answers the id map.</summary>
    Task<SnapshotInventory> ProvisionAsync(string snapshotId, CancellationToken cancellationToken = default);

    /// <summary>Publishes the snapshot's seat states live (<c>ticketing-venues:{snapshotId}</c>) until <paramref name="liveUntilUtc"/> (the end of the sale).</summary>
    Task SetLiveAsync(string snapshotId, DateTime liveUntilUtc, CancellationToken cancellationToken = default);
}

internal sealed class SparkVenues(
    IDocumentStore store,
    ISparkInventory inventory,
    ISparkApprovals approvals,
    VenueAccess access,
    IOptionsMonitor<SparkTicketingVenuesOptions> options,
    TimeProvider clock,
    Live.SeatStatePusher? pusher = null) : ISparkVenues
{
    private static readonly string[] ImageTypes = ["image/png", "image/jpeg", "image/webp"];

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private string Me => access.CallerId ?? throw new SparkAccessDeniedException(VenueAccess.LayoutType);

    public async Task<VenueLayoutRef> CreateLayoutAsync(string venueId, string name, CancellationToken cancellationToken = default)
    {
        var me = Me;
        CheckName(name);
        using var session = store.OpenAsyncSession();
        var venue = await session.LoadAsync<Venue>(venueId, cancellationToken) ?? throw new SparkAccessDeniedException(VenueAccess.VenueType);
        var layout = new VenueLayout { VenueId = venue.Id!, Name = name.Trim(), OwnerId = me, State = LayoutState.Draft, DraftRevision = 1, UpdatedAtUtc = Now };
        await session.StoreAsync(layout, cancellationToken);
        await session.StoreAsync(new VenueLayoutPlan { LayoutId = layout.Id!, Revision = 1, Plan = new LayoutPlan(), SavedAtUtc = Now }, VenueLayoutPlan.DraftId(layout.Id!), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return new VenueLayoutRef(layout.Id!, 1);
    }

    public async Task<VenueLayoutRef> CopyLayoutAsync(string layoutId, string name, CancellationToken cancellationToken = default)
    {
        var me = Me;
        CheckName(name);
        using var session = store.OpenAsyncSession();
        var source = await session.LoadAsync<VenueLayout>(layoutId, cancellationToken) ?? throw new SparkAccessDeniedException(VenueAccess.LayoutType);
        LayoutPlan plan;
        if (source.PublishedVersion > 0 && access.MayReadPublished(source))
            plan = (await session.LoadAsync<VenueLayoutPlan>(VenueLayoutPlan.VersionId(source.Id!, source.PublishedVersion), cancellationToken))!.Plan;
        else if (access.Owns(source))
            plan = (await session.LoadAsync<VenueLayoutPlan>(VenueLayoutPlan.DraftId(source.Id!), cancellationToken))!.Plan;
        else
            throw new SparkAccessDeniedException(VenueAccess.LayoutType);

        var copy = new VenueLayout
        {
            VenueId = source.VenueId, Name = name.Trim(), OwnerId = me, CopiedFromId = source.Id, State = LayoutState.Draft, DraftRevision = 1, UpdatedAtUtc = Now,
        };
        Summarize(copy, plan);
        await session.StoreAsync(copy, cancellationToken);
        await session.StoreAsync(new VenueLayoutPlan { LayoutId = copy.Id!, Revision = 1, Plan = plan, SavedAtUtc = Now }, VenueLayoutPlan.DraftId(copy.Id!), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return new VenueLayoutRef(copy.Id!, 1);
    }

    public async Task<VenueLayoutPlanResponse> GetPlanAsync(string layoutId, string version, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var layout = await session.LoadAsync<VenueLayout>(layoutId, cancellationToken) ?? throw new SparkAccessDeniedException(VenueAccess.LayoutType);
        VenueLayoutPlan? plan;
        var number = 0;
        switch (version)
        {
            case "draft":
                if (!await access.MayReadPrivateAsync(layout, cancellationToken)) throw new SparkAccessDeniedException(VenueAccess.LayoutType);
                plan = await session.LoadAsync<VenueLayoutPlan>(VenueLayoutPlan.DraftId(layout.Id!), cancellationToken);
                break;
            case "proposal":
                if (!await access.MayReadPrivateAsync(layout, cancellationToken)) throw new SparkAccessDeniedException(VenueAccess.LayoutType);
                plan = await session.LoadAsync<VenueLayoutPlan>(VenueLayoutPlan.ProposalId(layout.Id!), cancellationToken);
                break;
            default:
                number = version == "published" ? layout.PublishedVersion : int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
                if (number < 1 || number > layout.PublishedVersion || !(access.MayReadPublished(layout) || await access.MayReadPrivateAsync(layout, cancellationToken)))
                    throw new SparkAccessDeniedException(VenueAccess.LayoutType);
                plan = await session.LoadAsync<VenueLayoutPlan>(VenueLayoutPlan.VersionId(layout.Id!, number), cancellationToken);
                break;
        }
        if (plan is null)
            throw new SparkAccessDeniedException(VenueAccess.LayoutType);
        return new VenueLayoutPlanResponse(layout.Id!, layout.VenueId, layout.Name, Status(layout.State), layout.DraftRevision, layout.PublishedVersion, number,
            access.Owns(layout), !string.IsNullOrEmpty(plan.Plan.Background?.ImageId), layout.LastRejection, plan.Plan);
    }

    public async Task<int> SaveDraftAsync(string layoutId, int revision, LayoutPlan plan, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var (layout, draft) = await LoadOwnDraftAsync(session, layoutId, revision, cancellationToken);
        var problems = LayoutPlanValidator.Validate(plan, options.CurrentValue.Limits).ToList();
        if (plan.Background is { ImageId: { Length: > 0 } imageId } && (await session.LoadAsync<VenueLayoutBackground>(imageId, cancellationToken)) is null)
            problems.Add(new("background", "ticketingVenues.problem.background"));
        if (problems.Count > 0)
            throw new VenueLayoutInvalidException(problems);

        draft.Plan = plan;
        draft.Revision = ++layout.DraftRevision;
        draft.SavedAtUtc = layout.UpdatedAtUtc = Now;
        if (layout.PublishedVersion == 0)
            Summarize(layout, plan);
        await session.SaveChangesAsync(cancellationToken);
        return layout.DraftRevision;
    }

    public async Task<int> ImportCsvAsync(string layoutId, int revision, string csv, CancellationToken cancellationToken = default)
    {
        LayoutPlan current;
        using (var session = store.OpenAsyncSession())
            current = (await LoadOwnDraftAsync(session, layoutId, revision, cancellationToken)).Draft.Plan;
        var (merged, problems) = LayoutCsv.Import(current, csv);
        if (merged is null)
            throw new VenueLayoutInvalidException(problems);
        return await SaveDraftAsync(layoutId, revision, merged, cancellationToken);
    }

    public async Task<VenueLayoutPublishResponse> PublishAsync(string layoutId, int revision, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var (layout, draft) = await LoadOwnDraftAsync(session, layoutId, revision, cancellationToken);
        if (layout.State == LayoutState.PendingApproval)
            throw new SparkValidationException("ticketingVenues.publish.pending");
        var problems = LayoutPlanValidator.Validate(draft.Plan, options.CurrentValue.Limits);
        if (problems.Count > 0)
            throw new VenueLayoutInvalidException(problems);
        if (draft.Plan.Blocks.Count == 0)
            throw new VenueLayoutInvalidException([new("blocks", "ticketingVenues.problem.empty")]);

        var needsApproval = (layout.PublishedVersion > 0 || options.CurrentValue.ApproveCreations) && !await access.IsApproverAsync(cancellationToken);
        if (!needsApproval)
        {
            await PublishVersionAsync(session, layout, draft.Plan, draft.Revision, Now, cancellationToken);
            layout.LastRejection = null;
            await session.SaveChangesAsync(cancellationToken);
            return new VenueLayoutPublishResponse(VenueLayoutPublishOutcome.Published, layout.PublishedVersion, null);
        }

        await session.StoreAsync(new VenueLayoutPlan { LayoutId = layout.Id!, Revision = draft.Revision, Plan = draft.Plan, SavedAtUtc = Now },
            VenueLayoutPlan.ProposalId(layout.Id!), cancellationToken);
        var submitted = await approvals.SubmitAsync<LayoutPublishApproval>(session, new ApprovalSubmission
        {
            Subject = new ApprovalSubject(VenueAccess.LayoutType, layout.Id!),
            Title = layout.PublishedVersion == 0 ? $"New layout \"{layout.Name}\"" : $"Layout \"{layout.Name}\" version {layout.PublishedVersion + 1}",
            Summary = $"{CountSeats(draft.Plan)} seats, {draft.Plan.Blocks.Count} blocks.",
            Url = "/ticketing/venues/layouts/review?layoutId=" + Uri.EscapeDataString(layout.Id!),
            Data = new Dictionary<string, string?> { [LayoutPublishHandler.BaseVersionKey] = layout.PublishedVersion.ToString(CultureInfo.InvariantCulture) },
            ReplacePending = true,
        }, cancellationToken);
        layout.State = LayoutState.PendingApproval;
        layout.PendingRequestId = submitted.RequestId;
        layout.LastRejection = null;
        layout.UpdatedAtUtc = Now;
        await session.SaveChangesAsync(cancellationToken);
        return new VenueLayoutPublishResponse(VenueLayoutPublishOutcome.Pending, layout.PublishedVersion, submitted.RequestId);
    }

    public async Task<LayoutDiffResult> DiffAsync(string layoutId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var layout = await session.LoadAsync<VenueLayout>(layoutId, cancellationToken);
        if (layout is null || !await access.MayReadPrivateAsync(layout, cancellationToken))
            throw new SparkAccessDeniedException(VenueAccess.LayoutType);
        var proposal = await session.LoadAsync<VenueLayoutPlan>(VenueLayoutPlan.ProposalId(layout.Id!), cancellationToken)
                       ?? await session.LoadAsync<VenueLayoutPlan>(VenueLayoutPlan.DraftId(layout.Id!), cancellationToken);
        var published = layout.PublishedVersion > 0
            ? await session.LoadAsync<VenueLayoutPlan>(VenueLayoutPlan.VersionId(layout.Id!, layout.PublishedVersion), cancellationToken)
            : null;
        return LayoutDiff.Compare(published?.Plan, proposal?.Plan ?? new LayoutPlan());
    }

    public async Task<string> UploadBackgroundAsync(string layoutId, Stream content, string contentType, long length, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var layout = await session.LoadAsync<VenueLayout>(layoutId, cancellationToken);
        if (layout is null || !access.Owns(layout))
            throw new SparkAccessDeniedException(VenueAccess.LayoutType);
        if (length <= 0 || length > options.CurrentValue.MaxBackgroundBytes)
            throw new SparkValidationException("ticketingVenues.background.tooLarge");

        // Read it all (bounded above) and trust the bytes, not the declared type: SVG and anything else is refused.
        var buffer = new MemoryStream((int)length);
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > options.CurrentValue.MaxBackgroundBytes)
            throw new SparkValidationException("ticketingVenues.background.tooLarge");
        var sniffed = SniffImage(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (sniffed is null || !ImageTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw new SparkValidationException("ticketingVenues.background.type");
        buffer.Position = 0;

        var image = new VenueLayoutBackground { LayoutId = layout.Id!, ContentType = sniffed, Length = buffer.Length, UploadedAtUtc = Now };
        await session.StoreAsync(image, "VenueLayoutBackgrounds/" + Guid.NewGuid().ToString("N"), cancellationToken);
        session.Advanced.Attachments.Store(image.Id!, VenueLayoutBackground.AttachmentName, buffer, sniffed);
        await session.SaveChangesAsync(cancellationToken);
        return image.Id!;
    }

    public async Task<string> SnapshotAsync(string layoutId, string scope, int? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        var id = SnapshotId(scope);
        using var session = store.OpenAsyncSession();
        if (await session.LoadAsync<VenueLayoutSnapshot>(id, cancellationToken) is { } existing)
            return existing.LayoutId == layoutId ? existing.Id! : throw new InvalidOperationException($"Scope '{scope}' already has a snapshot of another layout.");

        var layout = await session.LoadAsync<VenueLayout>(layoutId, cancellationToken) ?? throw new InvalidOperationException($"Layout '{layoutId}' does not exist.");
        var number = version ?? layout.PublishedVersion;
        if (number < 1 || number > layout.PublishedVersion)
            throw new InvalidOperationException($"Layout '{layoutId}' has no published version {number}.");
        var plan = await session.LoadAsync<VenueLayoutPlan>(VenueLayoutPlan.VersionId(layoutId, number), cancellationToken)
                   ?? throw new InvalidOperationException($"Layout '{layoutId}' version {number} is missing.");
        var venue = await session.LoadAsync<Venue>(layout.VenueId, cancellationToken);

        var snapshot = new VenueLayoutSnapshot
        {
            Scope = scope,
            LayoutId = layoutId,
            LayoutVersion = number,
            VenueId = layout.VenueId,
            VenueName = venue?.Name ?? "",
            AddressLines = venue?.Address?.Lines.ToList() ?? [],
            TimeZone = venue?.TimeZone,
            Plan = plan.Plan,
            CreatedAtUtc = Now,
        };
        // Create-only: an empty change vector refuses an existing document, so two racing calls make one snapshot.
        await session.StoreAsync(snapshot, changeVector: string.Empty, id, cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            using var reread = store.OpenAsyncSession();
            var winner = await reread.LoadAsync<VenueLayoutSnapshot>(id, cancellationToken);
            return winner?.LayoutId == layoutId ? winner.Id! : throw new InvalidOperationException($"Scope '{scope}' already has a snapshot of another layout.");
        }
        return id;
    }

    public async Task<VenueLayoutSnapshot?> GetSnapshotAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<VenueLayoutSnapshot>(snapshotId, cancellationToken);
    }

    public async Task<SnapshotInventory> ProvisionAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var snapshot = await session.LoadAsync<VenueLayoutSnapshot>(snapshotId, cancellationToken) ?? throw new InvalidOperationException($"Snapshot '{snapshotId}' does not exist.");
        var map = new SnapshotInventory(snapshot.Id!, snapshot.Plan, options.CurrentValue.ExcludedFromChains);
        var live = await session.LoadAsync<VenueSnapshotLive>(VenueSnapshotLive.For(snapshotId), cancellationToken);
        if (live?.Provisioned == true)
            return map;
        await map.ProvisionAsync(inventory, options.CurrentValue.ShardsPerBlockQuota, cancellationToken);
        if (live is null)
        {
            live = new VenueSnapshotLive { SnapshotId = snapshotId };
            await session.StoreAsync(live, VenueSnapshotLive.For(snapshotId), cancellationToken);
        }
        live.Provisioned = true;
        await session.SaveChangesAsync(cancellationToken);
        return map;
    }

    public async Task SetLiveAsync(string snapshotId, DateTime liveUntilUtc, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        if (await session.LoadAsync<VenueLayoutSnapshot>(snapshotId, cancellationToken) is null)
            throw new InvalidOperationException($"Snapshot '{snapshotId}' does not exist.");
        var live = await session.LoadAsync<VenueSnapshotLive>(VenueSnapshotLive.For(snapshotId), cancellationToken);
        if (live is null)
        {
            live = new VenueSnapshotLive { SnapshotId = snapshotId };
            await session.StoreAsync(live, VenueSnapshotLive.For(snapshotId), cancellationToken);
        }
        live.LiveUntilUtc = liveUntilUtc;
        await session.SaveChangesAsync(cancellationToken);
        // The first frame now, so a retained topic has one before the next commit pushes again.
        pusher?.Touch([snapshotId]);
    }

    // ---- shared ---------------------------------------------------------------------------------------------

    /// <summary>The snapshot id of a scope: a hash, so any scope string (a date id with slashes) makes a short, safe id.</summary>
    public static string SnapshotId(string scope)
        => "VenueLayoutSnapshots/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)).AsSpan(0, 12));

    /// <summary>Stores <paramref name="plan"/> as the layout's next version and marks it published (in the caller's session).</summary>
    internal static async Task PublishVersionAsync(IAsyncDocumentSession session, VenueLayout layout, LayoutPlan plan, int revision, DateTime now, CancellationToken cancellationToken)
    {
        layout.PublishedVersion++;
        var version = new VenueLayoutPlan { LayoutId = layout.Id!, Version = layout.PublishedVersion, Revision = revision, Plan = plan, SavedAtUtc = now };
        // A published version is never written again: create-only.
        await session.StoreAsync(version, changeVector: string.Empty, VenueLayoutPlan.VersionId(layout.Id!, layout.PublishedVersion), cancellationToken);
        layout.State = LayoutState.Published;
        layout.PendingRequestId = null;
        layout.UpdatedAtUtc = now;
        Summarize(layout, plan);
    }

    internal static void Summarize(VenueLayout layout, LayoutPlan plan)
    {
        layout.SeatCount = CountSeats(plan);
        layout.UnnumberedCapacity = plan.Blocks.Where(b => b.Kind != BlockKind.SeatedNumbered).Sum(b => b.Capacity);
        layout.BlockCount = plan.Blocks.Count;
    }

    private static int CountSeats(LayoutPlan plan)
        => plan.Blocks.Where(b => b.Kind == BlockKind.SeatedNumbered).Sum(b => b.Rows.Sum(r => r.Seats.Count));

    private static VenueLayoutStatus Status(LayoutState state) => state switch
    {
        LayoutState.PendingApproval => VenueLayoutStatus.PendingApproval,
        LayoutState.Published => VenueLayoutStatus.Published,
        _ => VenueLayoutStatus.Draft,
    };

    private async Task<(VenueLayout Layout, VenueLayoutPlan Draft)> LoadOwnDraftAsync(IAsyncDocumentSession session, string layoutId, int revision, CancellationToken cancellationToken)
    {
        var layout = await session.LoadAsync<VenueLayout>(layoutId, cancellationToken);
        if (layout is null || !access.Owns(layout))
            throw new SparkAccessDeniedException(VenueAccess.LayoutType);
        if (layout.DraftRevision != revision)
            throw new VenueLayoutConflictException(layout.DraftRevision);
        var draft = await session.LoadAsync<VenueLayoutPlan>(VenueLayoutPlan.DraftId(layout.Id!), cancellationToken)
                    ?? throw new SparkAccessDeniedException(VenueAccess.LayoutType);
        // The revision check above and the layout's change vector make concurrent saves of one draft fail, not merge.
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        return (layout, draft);
    }

    private static void CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
            throw new SparkValidationException("ticketingVenues.problem.name", "Name");
    }

    /// <summary>The image type the bytes are (PNG, JPEG, WebP), or null.</summary>
    internal static string? SniffImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }
}
