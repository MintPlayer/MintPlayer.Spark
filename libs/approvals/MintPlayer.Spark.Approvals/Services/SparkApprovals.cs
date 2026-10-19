using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Approvals.Models;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Approvals.Services;

/// <summary>
/// The decision-request engine (commerce PRD §4.13). A decision loads the request in a session of its own, asks the
/// kind's live gate, runs the kind's handler in that session and commits the handler's writes with the request's new
/// status, guarded by the version it decided on: of two concurrent deciders exactly one commits (the other answers
/// <see cref="ApprovalOutcome.NotPending"/>). Document proposals apply through <see cref="ISparkReplacedSaves"/> instead.
/// </summary>
internal sealed partial class SparkApprovals : ISparkApprovals
{
    /// <summary>The longest reason kept.</summary>
    internal const int MaxReasonLength = 2000;

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IServiceProvider services;
    [Inject] private readonly ApprovalKindCatalog catalog;
    [Inject] private readonly ApprovalVisibility visibility;
    [Inject] private readonly ApprovalNotices notices;
    [Inject] private readonly ISparkReplacedSaves replacedSaves;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IOptions<SparkApprovalsOptions> options;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<SparkApprovals> logger;

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<ApprovalSubmitResult> SubmitAsync<TKind>(ApprovalSubmission submission, CancellationToken cancellationToken = default)
        where TKind : IApprovalKind
    {
        using var session = store.OpenAsyncSession();
        var (result, request, replaced) = await StageAsync(TKind.Name, session, submission, cancellationToken);
        if (!result.Created)
            return result;
        request!.NotifiedAtUtc = UtcNow;
        await session.SaveChangesAsync(cancellationToken);

        if (replaced is not null)
            await notices.DecidedAsync(replaced, request.RequestedBy, cancellationToken);
        await notices.SubmittedAsync(request, cancellationToken);
        return result;
    }

    public async Task<ApprovalSubmitResult> SubmitAsync<TKind>(IAsyncDocumentSession session, ApprovalSubmission submission, CancellationToken cancellationToken = default)
        where TKind : IApprovalKind
    {
        // NotifiedAtUtc stays null: the sweep tells the deciders once the caller's session has committed.
        var (result, _, _) = await StageAsync(TKind.Name, session, submission, cancellationToken);
        return result;
    }

    /// <summary>Stores a new request (or finds the pending one) in <paramref name="session"/>; nothing is saved.</summary>
    internal async Task<(ApprovalSubmitResult Result, ApprovalRequest? Request, ApprovalRequest? Replaced)> StageAsync(
        string kindName, IAsyncDocumentSession session, ApprovalSubmission submission, CancellationToken cancellationToken, ApprovalProposal? proposal = null)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentException.ThrowIfNullOrWhiteSpace(submission.Title);
        var kind = catalog.Find(kindName)
            ?? throw new InvalidOperationException($"The approval kind '{kindName}' has no IApprovalDeciders<> registered.");
        var subject = submission.Subject;
        var requestedBy = submission.RequestedBy ?? visibility.CallerId
            ?? throw new InvalidOperationException("An approval request needs a requester: the caller is anonymous and the submission names none.");
        if (submission.Data is { Count: > 32 })
            throw new ArgumentException("An approval request keeps at most 32 facts.", nameof(submission));

        if (submission.RequestedBy is null && subject.Id is { Length: > 0 })
            await GuardAsync(subject, kindName, cancellationToken);

        ApprovalRequest? replaced = null;
        if (proposal is null && subject.Id is { Length: > 0 })
        {
            var pending = await PendingAsync(session, kindName, subject, exactKey: true, cancellationToken);
            if (pending.FirstOrDefault() is { } existing)
            {
                if (!submission.ReplacePending)
                    return (new ApprovalSubmitResult(existing.Id!, Created: false), null, null);
                Close(existing, ApprovalStatuses.Withdrawn, requestedBy, reason: null);
                replaced = existing;
            }
        }

        var now = UtcNow;
        var decideBy = now + (submission.DecideWithin ?? options.Value.DecideWithin);
        var deciders = await kind.GetDeciderIdsAsync(services, subject, session, cancellationToken);
        var request = new ApprovalRequest
        {
            Id = ApprovalRequest.NewId(),
            Kind = kindName,
            SubjectType = subject.Type,
            SubjectId = subject.Id ?? string.Empty,
            SubjectKey = subject.Key,
            KindAndType = ApprovalVisibility.KindAndType(kindName, subject.Type),
            Status = ApprovalStatuses.Pending,
            Title = Truncate(submission.Title, 300)!,
            Summary = Truncate(submission.Summary, 4000),
            Url = submission.Url is { } url && SparkLinkUrl.IsAllowed(url) ? url : null,
            OrganizationId = submission.OrganizationId,
            RequestedBy = requestedBy,
            RequestedAtUtc = now,
            DeciderIds = deciders.Where(id => id is { Length: > 0 } && id != requestedBy).Distinct(StringComparer.Ordinal).ToList(),
            VisibleTo = (submission.VisibleTo ?? []).Where(id => id is { Length: > 0 }).Distinct(StringComparer.Ordinal).ToList(),
            DecideByUtc = decideBy,
            Data = submission.Data?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? [],
            Proposal = proposal,
            History = [new ApprovalRequestEvent { Status = ApprovalStatuses.Pending, By = requestedBy, AtUtc = now }],
            ExpiresAtUtc = decideBy + options.Value.Retention,
        };
        await session.StoreAsync(request, request.Id, cancellationToken);
        return (new ApprovalSubmitResult(request.Id!, Created: true), request, replaced);
    }

    public async Task<ApprovalResult> DecideAsync(string requestId, ApprovalDecision decision, string? reason, CancellationToken cancellationToken = default)
    {
        reason = CleanReason(reason);
        if (decision == ApprovalDecision.Reject && reason is null)
            return new ApprovalResult(ApprovalOutcome.ReasonRequired);

        using var session = store.OpenAsyncSession();
        var request = await session.LoadAsync<ApprovalRequest>(requestId, cancellationToken);
        if (request is null || !await visibility.MayDecideAsync(request, session, cancellationToken))
            return new ApprovalResult(ApprovalOutcome.NotFound);
        if (!IsOpen(request))
            return new ApprovalResult(ApprovalOutcome.NotPending) { Status = EffectiveStatus(request) };

        await GuardAsync(request, cancellationToken);
        var status = decision == ApprovalDecision.Approve ? ApprovalStatuses.Approved : ApprovalStatuses.Rejected;
        if (request.Proposal is not null && status == ApprovalStatuses.Approved)
            return await ApplyProposalAsync(request, session, cancellationToken);
        return await RunAsync(request, session, status, visibility.CallerId, reason, cancellationToken);
    }

    public async Task<ApprovalResult> WithdrawAsync(string requestId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var request = await session.LoadAsync<ApprovalRequest>(requestId, cancellationToken);
        if (request is null || !visibility.MayWithdraw(request))
            return new ApprovalResult(ApprovalOutcome.NotFound);
        if (!IsOpen(request))
            return new ApprovalResult(ApprovalOutcome.NotPending) { Status = EffectiveStatus(request) };
        return await RunAsync(request, session, ApprovalStatuses.Withdrawn, visibility.CallerId, reason: null, cancellationToken);
    }

    public async Task<ApprovalResult> RevokeAsync(string requestId, string reason, CancellationToken cancellationToken = default)
    {
        var cleaned = CleanReason(reason);
        if (cleaned is null)
            return new ApprovalResult(ApprovalOutcome.ReasonRequired);

        using var session = store.OpenAsyncSession();
        var request = await session.LoadAsync<ApprovalRequest>(requestId, cancellationToken);
        if (request is null || request.Proposal is not null || !await visibility.MayDecideAsync(request, session, cancellationToken))
            return new ApprovalResult(ApprovalOutcome.NotFound);
        if (request.Status != ApprovalStatuses.Approved)
            return new ApprovalResult(ApprovalOutcome.NotPending) { Status = EffectiveStatus(request) };
        return await RunAsync(request, session, ApprovalStatuses.Revoked, visibility.CallerId, cleaned, cancellationToken);
    }

    public async Task<IReadOnlyList<ApprovalRequestInfo>> GetPendingAsync<TKind>(ApprovalSubject? subject = null, CancellationToken cancellationToken = default)
        where TKind : IApprovalKind
    {
        using var session = store.OpenAsyncSession();
        var pending = await PendingAsync(session, TKind.Name, subject, exactKey: subject?.Key is not null, cancellationToken);
        return pending.Select(r => r.ToInfo()).ToList();
    }

    public async Task<int> ClosePendingAsync<TKind>(IAsyncDocumentSession session, ApprovalSubject subject, CancellationToken cancellationToken = default)
        where TKind : IApprovalKind
    {
        var pending = await PendingAsync(session, TKind.Name, subject, exactKey: subject.Key is not null, cancellationToken);
        foreach (var request in pending)
            Close(request, ApprovalStatuses.Withdrawn, by: null, reason: null);
        return pending.Count;
    }

    /// <summary>The sweep: expires overdue requests (running their handler), tells the deciders of requests raised inside another session.</summary>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = UtcNow;
        using (var session = store.OpenAsyncSession())
        {
            var overdue = await session.Query<ApprovalRequest>().Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                .Where(r => r.Status == ApprovalStatuses.Pending && r.DecideByUtc < now)
                .Take(128)
                .ToListAsync(cancellationToken);
            foreach (var id in overdue.Select(r => r.Id!))
            {
                try
                {
                    using var decision = store.OpenAsyncSession();
                    if (await decision.LoadAsync<ApprovalRequest>(id, cancellationToken) is { Status: ApprovalStatuses.Pending } request)
                        await RunAsync(request, decision, ApprovalStatuses.Expired, by: null, reason: null, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One kind's failing expiry must not hold up every other request: it is tried again next sweep.
                    logger.LogWarning(ex, "Approval request {RequestId} could not be expired.", id);
                }
            }
        }

        using (var session = store.OpenAsyncSession())
        {
            var untold = await session.Query<ApprovalRequest>().Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                .Where(r => r.NotifiedAtUtc == null && r.Status == ApprovalStatuses.Pending)
                .Take(128)
                .ToListAsync(cancellationToken);
            foreach (var request in untold)
            {
                request.NotifiedAtUtc = now;
                await session.StoreAsync(request, session.Advanced.GetChangeVectorFor(request), request.Id, cancellationToken);
            }
            if (untold.Count == 0)
                return;
            try { await session.SaveChangesAsync(cancellationToken); }
            catch (ConcurrencyException) { return; } // another instance told them
            foreach (var request in untold)
                await notices.SubmittedAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Runs the kind's handler for <paramref name="status"/> in <paramref name="session"/> and commits it with the
    /// request's new status, guarded by the version loaded.
    /// </summary>
    private async Task<ApprovalResult> RunAsync(ApprovalRequest request, IAsyncDocumentSession session, string status, string? by, string? reason, CancellationToken cancellationToken)
    {
        var changeVector = session.Advanced.GetChangeVectorFor(request);
        var context = new ApprovalDecisionContext
        {
            Request = request.ToInfo(),
            Status = status,
            DecidedBy = by,
            Reason = reason,
            Session = session,
            CancellationToken = cancellationToken,
        };

        // A proposal's withdrawal, rejection or expiry touches nothing but the request.
        if (request.Proposal is null && catalog.Find(request.Kind) is { } kind)
            await kind.RunAsync(services, context);
        else if (request.Proposal is null)
            logger.LogWarning("Approval request {RequestId} is of the unknown kind '{Kind}': its status changes, nothing else runs.", request.Id, request.Kind);

        if (context.IsStale)
            return await MarkStaleAsync(request.Id!, changeVector, by, conflictReason: null, cancellationToken);

        Close(request, status, by, reason);
        await session.StoreAsync(request, changeVector, request.Id, cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            return await NotPendingAsync(request.Id!, cancellationToken);
        }

        await notices.DecidedAsync(request, by, cancellationToken);
        return new ApprovalResult(ApprovalOutcome.Done) { Status = status };
    }

    /// <summary>
    /// Approves a document proposal: the diverted save, applied as the caller (the approver) with
    /// <c>Etag = base</c> and the proposer as <see cref="SaveContext.OnBehalfOf"/>, in one commit with the request's
    /// new status.
    /// </summary>
    private async Task<ApprovalResult> ApplyProposalAsync(ApprovalRequest request, IAsyncDocumentSession session, CancellationToken cancellationToken)
    {
        var changeVector = session.Advanced.GetChangeVectorFor(request);
        var by = visibility.CallerId;
        var proposal = request.Proposal!;
        ReplacedSaveOutcome outcome;
        try
        {
            outcome = await replacedSaves.ApplyAsync(new ReplacedSave
            {
                ObjectTypeId = proposal.ObjectTypeId,
                PersistentObjectJson = proposal.PersistentObjectJson,
                BaseEtag = proposal.BaseEtag,
                OnBehalfOf = request.RequestedBy,
                Alongside = async applySession =>
                {
                    var writes = (IAsyncDocumentSession)applySession;
                    var current = await writes.LoadAsync<ApprovalRequest>(request.Id, cancellationToken);
                    // Decided meanwhile: nothing of the apply may commit.
                    if (current is not { Status: ApprovalStatuses.Pending })
                        throw new SparkCancelException();
                    Close(current, ApprovalStatuses.Approved, by, reason: null);
                    await writes.StoreAsync(current, changeVector, current.Id, cancellationToken);
                },
            }, cancellationToken);
        }
        catch (ConcurrencyException)
        {
            return await NotPendingAsync(request.Id!, cancellationToken);
        }
        catch (SparkAccessDeniedException)
        {
            // The approver may hold ApproveProposal/T but not the row: the same answer as any refusal.
            return new ApprovalResult(ApprovalOutcome.NotFound);
        }

        switch (outcome.Kind)
        {
            case ReplacedSaveOutcomeKind.Applied:
                using (var follow = store.OpenAsyncSession())
                {
                    var applied = await follow.LoadAsync<ApprovalRequest>(request.Id, cancellationToken);
                    if (applied is not null)
                    {
                        // A creation's subject exists only now.
                        if (string.IsNullOrEmpty(applied.SubjectId) && outcome.Object?.Id is { Length: > 0 } newId)
                        {
                            applied.SubjectId = newId;
                            await follow.SaveChangesAsync(cancellationToken);
                        }
                        await notices.DecidedAsync(applied, by, cancellationToken);
                    }
                }
                return new ApprovalResult(ApprovalOutcome.Done) { Status = ApprovalStatuses.Approved, Object = outcome.Object };
            case ReplacedSaveOutcomeKind.Conflict:
                return await MarkStaleAsync(request.Id!, changeVector, by, outcome.ConflictReason, cancellationToken);
            case ReplacedSaveOutcomeKind.Unwritable:
                return new ApprovalResult(ApprovalOutcome.Unwritable) { Status = ApprovalStatuses.Pending, Attributes = outcome.Attributes };
            case ReplacedSaveOutcomeKind.Invalid:
                return new ApprovalResult(ApprovalOutcome.Invalid) { Status = ApprovalStatuses.Pending, Errors = outcome.Errors };
            default:
                // Cancelled: by an interceptor, or by the Alongside because someone decided first.
                using (var check = store.OpenAsyncSession())
                {
                    var current = await check.LoadAsync<ApprovalRequest>(request.Id, cancellationToken);
                    return current is { Status: ApprovalStatuses.Pending }
                        ? new ApprovalResult(ApprovalOutcome.Cancelled) { Status = ApprovalStatuses.Pending }
                        : new ApprovalResult(ApprovalOutcome.NotPending) { Status = current?.Status };
                }
        }
    }

    private async Task<ApprovalResult> MarkStaleAsync(string requestId, string? changeVector, string? by, string? conflictReason, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var request = await session.LoadAsync<ApprovalRequest>(requestId, cancellationToken);
        if (request is not { Status: ApprovalStatuses.Pending })
            return new ApprovalResult(ApprovalOutcome.NotPending) { Status = request?.Status };
        Close(request, ApprovalStatuses.Stale, by, conflictReason);
        await session.StoreAsync(request, changeVector ?? session.Advanced.GetChangeVectorFor(request), request.Id, cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            return await NotPendingAsync(requestId, cancellationToken);
        }
        await notices.DecidedAsync(request, by, cancellationToken);
        return new ApprovalResult(ApprovalOutcome.Stale) { Status = ApprovalStatuses.Stale, ConflictReason = conflictReason };
    }

    private async Task<ApprovalResult> NotPendingAsync(string requestId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var current = await session.LoadAsync<ApprovalRequest>(requestId, cancellationToken);
        return new ApprovalResult(ApprovalOutcome.NotPending) { Status = current?.Status };
    }

    /// <summary>Moves <paramref name="request"/> to its final <paramref name="status"/> and restarts its retention.</summary>
    private void Close(ApprovalRequest request, string status, string? by, string? reason)
    {
        var now = UtcNow;
        request.Status = status;
        request.DecidedBy = by;
        request.DecidedAtUtc = now;
        request.Reason = reason;
        request.History.Add(new ApprovalRequestEvent { Status = status, By = by, AtUtc = now, Reason = reason });
        request.ExpiresAtUtc = now + options.Value.Retention;
    }

    /// <summary>Pending and not past its deadline (the sweep has not run yet).</summary>
    internal bool IsOpen(ApprovalRequest request) => request.Status == ApprovalStatuses.Pending && request.DecideByUtc >= UtcNow;

    /// <summary>The status as the caller should read it: an overdue pending request is expired already.</summary>
    internal string EffectiveStatus(ApprovalRequest request)
        => request.Status == ApprovalStatuses.Pending && request.DecideByUtc < UtcNow ? ApprovalStatuses.Expired : request.Status;

    private static async Task<List<ApprovalRequest>> PendingAsync(IAsyncDocumentSession session, string kind, ApprovalSubject? subject, bool exactKey, CancellationToken cancellationToken)
    {
        IQueryable<ApprovalRequest> query = session.Query<ApprovalRequest>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
            .Where(r => r.Kind == kind && r.Status == ApprovalStatuses.Pending);
        if (subject is not null)
        {
            var id = subject.Id;
            query = query.Where(r => r.SubjectId == id);
            if (exactKey)
            {
                var key = subject.Key;
                query = query.Where(r => r.SubjectKey == key);
            }
        }
        return await query.Take(1024).ToListAsync(cancellationToken);
    }

    internal static string? CleanReason(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        var clean = new string(trimmed.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());
        return Truncate(clean, MaxReasonLength);
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    /// <summary>The write guards (a suspended caller, a locked subject) refuse a request about a document as they refuse a save.</summary>
    private Task GuardAsync(ApprovalRequest request, CancellationToken cancellationToken)
        => request.SubjectId is { Length: > 0 }
            ? GuardAsync(new ApprovalSubject(request.SubjectType, request.SubjectId, request.SubjectKey), request.Kind, cancellationToken, request.Id)
            : Task.CompletedTask;

    private async Task GuardAsync(ApprovalSubject subject, string kind, CancellationToken cancellationToken, string? requestId = null)
    {
        var guards = services.GetServices<ISatelliteWriteGuard>().ToList();
        if (guards.Count == 0 || ResolveSubjectType(subject.Type) is not { } targetType)
            return;
        var context = new SatelliteWriteContext
        {
            DocumentType = typeof(ApprovalRequest),
            DocumentId = requestId ?? $"ApprovalRequests/new/{kind}",
            TargetType = targetType,
            TargetId = subject.Id,
            User = visibility.CallerPrincipal,
        };
        foreach (var guard in guards)
            await guard.EnsureMayWriteAsync(context);
    }

    private static readonly ConcurrentDictionary<string, Type?> subjectTypes = new(StringComparer.Ordinal);

    private Type? ResolveSubjectType(string subjectType)
        => subjectTypes.GetOrAdd(subjectType, name =>
            modelLoader.GetEntityTypeByName(name)?.ClrType is { Length: > 0 } clrType
                ? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(clrType, throwOnError: false)).FirstOrDefault(t => t is not null)
                : null);
}
