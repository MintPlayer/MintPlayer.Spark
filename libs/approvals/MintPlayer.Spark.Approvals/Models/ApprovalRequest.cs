using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Approvals.Models;

/// <summary>
/// One decision request (commerce PRD §4.13): what is asked, by whom, of whom, and how it ended. Written only by
/// <see cref="ISparkApprovals"/> (never by a form); read by its requester, its deciders and <see cref="VisibleTo"/>
/// (the row rule in <c>ApprovalRequestActions</c>). The request itself is the audit trail: its status, decider,
/// time and reason, plus <see cref="History"/>.
/// </summary>
public class ApprovalRequest : IExpiring
{
    public string? Id { get; set; }

    /// <summary><see cref="IApprovalKind.Name"/> of the kind.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary><see cref="ApprovalSubject.Type"/>.</summary>
    public string SubjectType { get; set; } = string.Empty;

    /// <summary><see cref="ApprovalSubject.Id"/>.</summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <summary><see cref="ApprovalSubject.Key"/>.</summary>
    public string? SubjectKey { get; set; }

    /// <summary><c>{Kind}|{SubjectType}</c>: what a per-type decider scope matches (the row rule cannot concatenate).</summary>
    public string KindAndType { get; set; } = string.Empty;

    /// <summary><see cref="ApprovalStatuses"/>.</summary>
    public string Status { get; set; } = ApprovalStatuses.Pending;

    public string Title { get; set; } = string.Empty;

    public string? Summary { get; set; }

    /// <summary>The subject's page, app-relative.</summary>
    public string? Url { get; set; }

    /// <summary>The organization it belongs to.</summary>
    public string? OrganizationId { get; set; }

    public string? RequestedBy { get; set; }

    public DateTime RequestedAtUtc { get; set; }

    /// <summary>The deciders listed at submission (<see cref="IApprovalDeciders{TKind}.GetDeciderIdsAsync"/>): told, and shown it. The live gate still decides.</summary>
    public List<string> DeciderIds { get; set; } = [];

    /// <summary>Further users who may see it, never decide it.</summary>
    public List<string> VisibleTo { get; set; } = [];

    /// <summary>Pending past this, it expires (the sweep runs the handler's expiry).</summary>
    public DateTime DecideByUtc { get; set; }

    public string? DecidedBy { get; set; }

    public DateTime? DecidedAtUtc { get; set; }

    /// <summary>The decider's reason (always set for a rejection or revocation).</summary>
    public string? Reason { get; set; }

    /// <summary>When the deciders were told; null until then (the sweep tells a request raised inside another session).</summary>
    public DateTime? NotifiedAtUtc { get; set; }

    /// <summary>Small facts the submitter kept for the handler.</summary>
    public Dictionary<string, string?> Data { get; set; } = [];

    /// <summary>For a document proposal: the diverted save.</summary>
    public ApprovalProposal? Proposal { get; set; }

    /// <summary>Every status change, oldest first.</summary>
    public List<ApprovalRequestEvent> History { get; set; } = [];

    /// <summary>X1: the retention. Pending: the deadline plus <c>Spark:Approvals:Retention</c>; decided: the decision plus it.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    internal static string NewId() => "ApprovalRequests/" + Guid.NewGuid().ToString("N");

    internal ApprovalRequestInfo ToInfo() => new()
    {
        Id = Id!,
        Kind = Kind,
        Subject = new ApprovalSubject(SubjectType, SubjectId, SubjectKey),
        Status = Status,
        Title = Title,
        Summary = Summary,
        Url = Url,
        RequestedBy = RequestedBy,
        RequestedAtUtc = RequestedAtUtc,
        OrganizationId = OrganizationId,
        DecidedBy = DecidedBy,
        DecidedAtUtc = DecidedAtUtc,
        Reason = Reason,
        Data = Data,
    };
}

/// <summary>A diverted save (commerce X7): what the proposer saved, against which version.</summary>
public class ApprovalProposal
{
    /// <summary><c>Create</c> or <c>Modify</c>.</summary>
    public string Operation { get; set; } = string.Empty;

    public Guid ObjectTypeId { get; set; }

    /// <summary>The proposed object as wire JSON text (never the object: S10).</summary>
    public string PersistentObjectJson { get; set; } = string.Empty;

    /// <summary>The change vector it was made against; null for a creation.</summary>
    public string? BaseEtag { get; set; }
}

/// <summary>One status change of a request.</summary>
public class ApprovalRequestEvent
{
    public string Status { get; set; } = string.Empty;

    public string? By { get; set; }

    public DateTime AtUtc { get; set; }

    public string? Reason { get; set; }
}

/// <summary>The operations a document proposal is configured for (<c>Spark:Approvals:Types</c>).</summary>
public static class ApprovalProposalOperations
{
    public const string Create = "Create";
    public const string Modify = "Modify";
}
