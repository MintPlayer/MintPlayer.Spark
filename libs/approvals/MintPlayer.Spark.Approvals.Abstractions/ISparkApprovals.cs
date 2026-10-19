using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using Raven.Client.Documents.Session;

[assembly: SparkReservedActions(typeof(MintPlayer.Spark.Approvals.ApprovalRights))]

namespace MintPlayer.Spark.Approvals;

/// <summary>
/// Decision requests (commerce PRD §4.13, D9): someone asks, the deciders of the kind decide, the kind's handler runs
/// the decision. Every call acts as the current user.
/// </summary>
public interface ISparkApprovals
{
    /// <summary>
    /// Raises a request and commits it, then tells the deciders. A pending request of the same kind for the same
    /// subject is answered instead (<see cref="ApprovalSubmitResult.Created"/> false), unless
    /// <see cref="ApprovalSubmission.ReplacePending"/>.
    /// </summary>
    Task<ApprovalSubmitResult> SubmitAsync<TKind>(ApprovalSubmission submission, CancellationToken cancellationToken = default)
        where TKind : IApprovalKind;

    /// <summary>
    /// Raises a request inside <paramref name="session"/>: it commits with the caller's own writes, or not at all (an
    /// interceptor raising one while its save commits). The deciders are told shortly after the commit, by the
    /// library's sweep.
    /// </summary>
    Task<ApprovalSubmitResult> SubmitAsync<TKind>(IAsyncDocumentSession session, ApprovalSubmission submission, CancellationToken cancellationToken = default)
        where TKind : IApprovalKind;

    /// <summary>
    /// Approves or rejects. A rejection needs a <paramref name="reason"/>. The caller must pass the kind's live gate and
    /// may not be the requester; anything else answers <see cref="ApprovalOutcome.NotFound"/>, the uniform refusal.
    /// </summary>
    Task<ApprovalResult> DecideAsync(string requestId, ApprovalDecision decision, string? reason, CancellationToken cancellationToken = default);

    /// <summary>The requester takes a pending request back.</summary>
    Task<ApprovalResult> WithdrawAsync(string requestId, CancellationToken cancellationToken = default);

    /// <summary>A decider takes back an approval, with a <paramref name="reason"/>; the kind's handler must support it.</summary>
    Task<ApprovalResult> RevokeAsync(string requestId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// The pending requests of <typeparamref name="TKind"/>, all of them or those about <paramref name="subject"/>
    /// (matching its key too when it has one). Server code: no visibility filter.
    /// </summary>
    Task<IReadOnlyList<ApprovalRequestInfo>> GetPendingAsync<TKind>(ApprovalSubject? subject = null, CancellationToken cancellationToken = default)
        where TKind : IApprovalKind;

    /// <summary>
    /// Closes the pending requests of <typeparamref name="TKind"/> about <paramref name="subject"/> (any key when the
    /// subject has none) as <see cref="ApprovalStatuses.Withdrawn"/>, inside <paramref name="session"/>, without
    /// running the handler: the caller is changing the subject itself (deleting it, removing the row). Returns how
    /// many were closed.
    /// </summary>
    Task<int> ClosePendingAsync<TKind>(IAsyncDocumentSession session, ApprovalSubject subject, CancellationToken cancellationToken = default)
        where TKind : IApprovalKind;
}

/// <summary>A request to raise (see <see cref="ISparkApprovals.SubmitAsync{TKind}(ApprovalSubmission, CancellationToken)"/>).</summary>
public sealed record ApprovalSubmission
{
    public required ApprovalSubject Subject { get; init; }

    /// <summary>One line for lists and notifications (<c>"Fleet asks for fleet.read"</c>).</summary>
    public required string Title { get; init; }

    /// <summary>More text for the detail page and the mail.</summary>
    public string? Summary { get; init; }

    /// <summary>Where the subject is: an app-relative path (<c>/po/OidcApplication/…</c>) for the links in notices.</summary>
    public string? Url { get; init; }

    /// <summary>Who asks. Default: the current user; server code acting for a user names them.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>Further users who may see the request (never decide it): the subject's team.</summary>
    public IReadOnlyCollection<string>? VisibleTo { get; init; }

    /// <summary>The organization it belongs to, for notification preferences.</summary>
    public string? OrganizationId { get; init; }

    /// <summary>How long the deciders have; default <c>Spark:Approvals:DecideWithin</c>.</summary>
    public TimeSpan? DecideWithin { get; init; }

    /// <summary>Small facts for the handler (at most 32 entries).</summary>
    public IReadOnlyDictionary<string, string?>? Data { get; init; }

    /// <summary>A pending request of the same kind and subject is withdrawn and this one raised instead.</summary>
    public bool ReplacePending { get; init; }
}

/// <param name="RequestId">The request (new, or the pending one answered instead).</param>
/// <param name="Created">Whether a new request was raised.</param>
public sealed record ApprovalSubmitResult(string RequestId, bool Created);

public enum ApprovalDecision
{
    Approve,
    Reject,
}

/// <summary>How a decision, a withdrawal or a revocation ended.</summary>
public enum ApprovalOutcome
{
    /// <summary>Done: <see cref="ApprovalResult.Status"/> is the new status.</summary>
    Done,

    /// <summary>
    /// Absent, not visible to the caller, or the caller may not do this: one answer for all three (the uniform
    /// 404/401), so a request id is no oracle.
    /// </summary>
    NotFound,

    /// <summary>Already decided, withdrawn or expired (someone else was first).</summary>
    NotPending,

    /// <summary>A rejection or revocation without a reason.</summary>
    ReasonRequired,

    /// <summary>It could not be applied as asked: the request is now <see cref="ApprovalStatuses.Stale"/>.</summary>
    Stale,

    /// <summary>A document proposal sets attributes the decider may not write (<see cref="ApprovalResult.Attributes"/>). Still pending.</summary>
    Unwritable,

    /// <summary>A document proposal failed validation (<see cref="ApprovalResult.Errors"/>). Still pending.</summary>
    Invalid,

    /// <summary>An interceptor cancelled the apply. Still pending.</summary>
    Cancelled,
}

/// <summary>The result of a decision, a withdrawal or a revocation.</summary>
public sealed record ApprovalResult(ApprovalOutcome Outcome)
{
    /// <summary>The request's status afterwards.</summary>
    public string? Status { get; init; }

    /// <summary>For a stale document proposal: <c>changed</c>, <c>deleted</c> or <c>exists</c>.</summary>
    public string? ConflictReason { get; init; }

    public IReadOnlyList<string> Attributes { get; init; } = [];

    public IReadOnlyList<ValidationError> Errors { get; init; } = [];

    /// <summary>For an applied document proposal: the saved row.</summary>
    public PersistentObject? Object { get; init; }
}

/// <summary>
/// The rights Approvals asks <c>security.json</c> about. The library grants the decision actions on its own
/// <c>ApprovalRequest</c> type to <c>@authenticated</c> (every request is checked per row and per decider); the
/// right below is the application's to grant, per type, like Moderation's.
/// </summary>
public static class ApprovalRights
{
    /// <summary>
    /// <c>ApproveProposal/T</c>: decide document proposals of <c>T</c> (a type listed under
    /// <c>Spark:Approvals:Types</c>). A holder's own saves of <c>T</c> are not proposals.
    /// </summary>
    public const string ApproveProposal = "ApproveProposal";
}

/// <summary>
/// Delivers approval notices (a request raised for its deciders, a decision for its requester). Discovered like the
/// deciders; every registered notifier is called. With none, Approvals mails the recipients itself through
/// MailManager (deduplicated per request, event and user). Notifications registers one (in-app, mail and push by
/// each user's preference), which replaces that mail.
/// </summary>
[SparkDiscoverable]
public interface IApprovalNotifier
{
    Task NotifyAsync(ApprovalNotice notice, CancellationToken cancellationToken);
}

/// <summary>One notice: what happened to which request, for whom.</summary>
public sealed record ApprovalNotice
{
    /// <summary><see cref="ApprovalNoticeEvents"/>.</summary>
    public required string Event { get; init; }

    public required ApprovalRequestInfo Request { get; init; }

    /// <summary>The users to tell.</summary>
    public required IReadOnlyCollection<string> UserIds { get; init; }

    /// <summary>The headline, in the language of the request that caused it.</summary>
    public required string Title { get; init; }

    public string? Body { get; init; }

    /// <summary>The request's page, app-relative.</summary>
    public required string Url { get; init; }
}

/// <summary>The events an <see cref="ApprovalNotice"/> reports.</summary>
public static class ApprovalNoticeEvents
{
    /// <summary>A request was raised: told to its deciders.</summary>
    public const string Submitted = "submitted";

    /// <summary>Approved, rejected, revoked, expired or stale: told to the requester.</summary>
    public const string Decided = "decided";
}
