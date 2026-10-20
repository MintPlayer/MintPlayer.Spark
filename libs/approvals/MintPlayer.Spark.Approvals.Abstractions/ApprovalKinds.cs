using System.Security.Claims;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Approvals;

/// <summary>
/// What a request is about (commerce PRD §4.13): a document, optionally one row or facet of it.
/// </summary>
/// <param name="Type">The subject's type, as the kind names it (<c>OidcApplication</c>, <c>Organization</c>); shown, filtered on, never resolved to a CLR type by name alone.</param>
/// <param name="Id">The document id (<c>OidcApplications/1-A</c>).</param>
/// <param name="Key">A row or facet of the document (<c>fleet.read</c> for one scope of an application); null for the whole document.</param>
public sealed record ApprovalSubject(string Type, string Id, string? Key = null);

/// <summary>
/// One kind of decision request: a marker type whose <see cref="Name"/> is stored on every request of the kind, so it
/// is permanent. Prefix it with the library alias (<c>identity-provider.scope</c>). Deciders and the handler are
/// typed on the kind: <see cref="IApprovalDeciders{TKind}"/>, <see cref="IApprovalHandler{TKind}"/>.
/// </summary>
/// <example>
/// <code>
/// public sealed class GoLiveApproval : IApprovalKind { public static string Name => "identity-provider.go-live"; }
/// </code>
/// </example>
public interface IApprovalKind
{
    /// <summary>The stored kind name. Never change it: pending requests carry it.</summary>
    static abstract string Name { get; }
}

/// <summary>The statuses an <c>ApprovalRequest</c> moves through. Pending is the only one that changes again.</summary>
public static class ApprovalStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    /// <summary>The requester took it back, or the code that raised it closed it (the subject went).</summary>
    public const string Withdrawn = "Withdrawn";

    /// <summary>Nobody decided before its deadline.</summary>
    public const string Expired = "Expired";

    /// <summary>
    /// It can no longer be applied as asked: a document proposal whose base changed (a 409 on apply), or a request
    /// whose handler found the subject gone. Nothing was changed.
    /// </summary>
    public const string Stale = "Stale";

    /// <summary>Approved, then taken back by a decider (<see cref="ISparkApprovals.RevokeAsync"/>).</summary>
    public const string Revoked = "Revoked";
}

/// <summary>
/// Who may decide a request of <typeparamref name="TKind"/>. Asked live, at decision time, never from what was stored
/// at submission, so a decider who lost the role cannot decide any more.
/// </summary>
/// <remarks>
/// Discovered (<c>[SparkDiscoverable]</c>) in an application; a library registers its own (scoped). One per kind: a
/// second registration is refused at startup.
/// </remarks>
[SparkDiscoverable]
public interface IApprovalDeciders<TKind> where TKind : IApprovalKind
{
    /// <summary>
    /// The live gate: whether <see cref="ApprovalDeciderContext.UserId"/> may decide this request now. The requester
    /// never may, whatever this answers (no self-approval); they can withdraw instead.
    /// </summary>
    ValueTask<bool> MayDecideAsync(ApprovalDeciderContext context);

    /// <summary>
    /// The users to tell about a new request, and who see it in their inbox. Optional: a population nothing can list
    /// (the holders of a right) answers empty and is reached through <see cref="DecidesAllAsync"/>.
    /// </summary>
    ValueTask<IReadOnlyCollection<string>> GetDeciderIdsAsync(ApprovalSubject subject, IAsyncDocumentSession session, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyCollection<string>>([]);

    /// <summary>
    /// Requests the caller may see without being listed: <see cref="ApprovalScope.All"/> for a kind-wide population
    /// (the identity provider's administrators), a list of subject types for a per-type right (document proposals),
    /// <see cref="ApprovalScope.None"/> by default. It only widens what the inbox shows; <see cref="MayDecideAsync"/>
    /// still decides each request.
    /// </summary>
    ValueTask<ApprovalScope> DecidesAllAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => ValueTask.FromResult(ApprovalScope.None);
}

/// <summary>What <see cref="IApprovalDeciders{TKind}.MayDecideAsync"/> is asked about.</summary>
public sealed class ApprovalDeciderContext
{
    public required ApprovalRequestInfo Request { get; init; }

    /// <summary>The caller's user id.</summary>
    public required string UserId { get; init; }

    /// <summary>The caller.</summary>
    public required ClaimsPrincipal User { get; init; }

    /// <summary>A session to read the subject in (do not write).</summary>
    public required IAsyncDocumentSession Session { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Requests a caller sees as a decider without being listed (see <see cref="IApprovalDeciders{TKind}.DecidesAllAsync"/>).</summary>
/// <param name="Everything">Every request of the kind.</param>
/// <param name="SubjectTypes">The requests of the kind about these subject types.</param>
public sealed record ApprovalScope(bool Everything, IReadOnlyCollection<string> SubjectTypes)
{
    public static ApprovalScope None { get; } = new(false, []);

    public static ApprovalScope All { get; } = new(true, []);

    public static ApprovalScope Types(IReadOnlyCollection<string> subjectTypes) => new(false, subjectTypes);
}

/// <summary>
/// What a decision does to the subject. Each method runs inside the decision's session
/// (<see cref="ApprovalDecisionContext.Session"/>): store what it changes, never <c>SaveChanges</c>. The request's new
/// status commits in the same transaction, guarded by the version that was decided on, so two deciders cannot both
/// run it.
/// </summary>
/// <remarks>Discovered like <see cref="IApprovalDeciders{TKind}"/>; one per kind.</remarks>
[SparkDiscoverable]
public interface IApprovalHandler<TKind> where TKind : IApprovalKind
{
    Task ApproveAsync(ApprovalDecisionContext context);

    /// <summary><see cref="ApprovalDecisionContext.Reason"/> is set: a rejection always has one.</summary>
    Task RejectAsync(ApprovalDecisionContext context);

    /// <summary>The requester took the request back. Nothing by default.</summary>
    Task WithdrawAsync(ApprovalDecisionContext context) => Task.CompletedTask;

    /// <summary>Nobody decided in time. Nothing by default.</summary>
    Task ExpireAsync(ApprovalDecisionContext context) => Task.CompletedTask;

    /// <summary>A decider took back an approval (<see cref="ISparkApprovals.RevokeAsync"/>); <see cref="ApprovalDecisionContext.Reason"/> is set. Refused by default.</summary>
    Task RevokeAsync(ApprovalDecisionContext context) => throw new NotSupportedException($"{TKind.Name} approvals cannot be revoked.");
}

/// <summary>A decision being made (see <see cref="IApprovalHandler{TKind}"/>).</summary>
public sealed class ApprovalDecisionContext
{
    public required ApprovalRequestInfo Request { get; init; }

    /// <summary>The status the request moves to (<see cref="ApprovalStatuses"/>).</summary>
    public required string Status { get; init; }

    /// <summary>Who decided; null for an expiry or a system withdrawal.</summary>
    public string? DecidedBy { get; init; }

    /// <summary>The decider's reason: always set for a rejection or a revocation.</summary>
    public string? Reason { get; init; }

    /// <summary>The decision's session: store, do not save.</summary>
    public required IAsyncDocumentSession Session { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Whether <see cref="MarkStale"/> was called (read by the engine).</summary>
    public bool IsStale { get; private set; }

    /// <summary>
    /// The subject can no longer take this decision (it was deleted, or already changed some other way): the request
    /// becomes <see cref="ApprovalStatuses.Stale"/> instead, and nothing the handler stored is committed.
    /// </summary>
    public void MarkStale() => IsStale = true;
}

/// <summary>A request as the engine hands it to deciders, handlers and notifiers.</summary>
public sealed record ApprovalRequestInfo
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required ApprovalSubject Subject { get; init; }

    public required string Status { get; init; }

    public required string Title { get; init; }

    public string? Summary { get; init; }

    public string? Url { get; init; }

    public string? RequestedBy { get; init; }

    public DateTime RequestedAtUtc { get; init; }

    public string? OrganizationId { get; init; }

    public string? DecidedBy { get; init; }

    public DateTime? DecidedAtUtc { get; init; }

    public string? Reason { get; init; }

    /// <summary>Small facts the submitter kept for the handler.</summary>
    public IReadOnlyDictionary<string, string?> Data { get; init; } = new Dictionary<string, string?>();
}
