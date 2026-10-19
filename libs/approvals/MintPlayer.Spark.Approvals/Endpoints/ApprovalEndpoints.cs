using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Approvals.Models;
using MintPlayer.Spark.Approvals.Services;
using MintPlayer.Spark.Endpoints;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Approvals.Endpoints;

internal class ApprovalsGroup : IEndpointGroup
{
    public static string Prefix => "/spark/approvals";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services)
        => group.RequireAuthorization();
}

/// <param name="Id">The request id.</param>
/// <param name="Decision"><c>approve</c> or <c>reject</c>.</param>
/// <param name="Reason">Required for a rejection.</param>
public sealed record ApprovalDecideBody(string? Id, string? Decision, string? Reason);

public sealed record ApprovalWithdrawBody(string? Id);

public sealed record ApprovalRevokeBody(string? Id, string? Reason);

/// <summary>What a decision answered: the outcome, the status afterwards and, when it did not apply, why.</summary>
public sealed record ApprovalDecisionResponse(
    string Outcome,
    string? Status,
    string? ConflictReason,
    IReadOnlyList<string> Attributes,
    IReadOnlyList<ValidationError> Errors,
    PersistentObject? Object);

/// <summary>
/// The answers every decision endpoint shares. A request the caller may not see or decide is the standard refusal
/// (401 anonymous, 404 otherwise); a request that is no longer pending, or a proposal that went stale, is a 409;
/// a missing reason a 400; a proposal the approver cannot apply in full a 422.
/// </summary>
internal static class ApprovalAnswers
{
    public static IResult Answer(ISparkAddOnEndpoints addOn, HttpContext httpContext, ApprovalResult result)
    {
        if (result.Outcome == ApprovalOutcome.NotFound)
            return addOn.Refusal(httpContext);
        var status = result.Outcome switch
        {
            ApprovalOutcome.Done => StatusCodes.Status200OK,
            ApprovalOutcome.ReasonRequired or ApprovalOutcome.Invalid => StatusCodes.Status400BadRequest,
            ApprovalOutcome.Unwritable => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status409Conflict,
        };
        return addOn.Envelope(new ApprovalDecisionResponse(
            JsonNamingPolicy.CamelCase.ConvertName(result.Outcome.ToString()),
            result.Status,
            result.ConflictReason,
            result.Attributes,
            result.Errors,
            result.Object), status);
    }
}

/// <summary><c>POST /spark/approvals/decide</c>: approve or reject one request (the ng-spark detail panel's buttons).</summary>
[MemberOf<ApprovalsGroup>]
internal sealed partial class DecideApproval : IPostEndpoint<ApprovalDecideBody>
{
    public static string Path => "/decide";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkApprovals approvals;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(ApprovalDecideBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        ApprovalDecision? decision = request?.Decision?.ToLowerInvariant() switch
        {
            "approve" => ApprovalDecision.Approve,
            "reject" => ApprovalDecision.Reject,
            _ => null,
        };
        if (request?.Id is not { Length: > 0 } id || decision is null)
            return addOn.Refusal(http);
        return ApprovalAnswers.Answer(addOn, http, await approvals.DecideAsync(id, decision.Value, request.Reason, cancellationToken));
    }
}

/// <summary><c>POST /spark/approvals/withdraw</c>: the requester takes a pending request back.</summary>
[MemberOf<ApprovalsGroup>]
internal sealed partial class WithdrawApproval : IPostEndpoint<ApprovalWithdrawBody>
{
    public static string Path => "/withdraw";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkApprovals approvals;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(ApprovalWithdrawBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (request?.Id is not { Length: > 0 } id)
            return addOn.Refusal(http);
        return ApprovalAnswers.Answer(addOn, http, await approvals.WithdrawAsync(id, cancellationToken));
    }
}

/// <summary><c>POST /spark/approvals/revoke</c>: a decider takes back an approval, with a reason.</summary>
[MemberOf<ApprovalsGroup>]
internal sealed partial class RevokeApproval : IPostEndpoint<ApprovalRevokeBody>
{
    public static string Path => "/revoke";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkApprovals approvals;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(ApprovalRevokeBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (request?.Id is not { Length: > 0 } id)
            return addOn.Refusal(http);
        try
        {
            return ApprovalAnswers.Answer(addOn, http, await approvals.RevokeAsync(id, request.Reason ?? string.Empty, cancellationToken));
        }
        catch (NotSupportedException)
        {
            return addOn.Refusal(http);
        }
    }
}

/// <summary>
/// A document proposal, for the diff (ng-spark's approval panel): the proposed object as the proposer saved it, and
/// the row as it is now, loaded as the caller (null for a creation, or a row the caller may not read).
/// </summary>
/// <param name="Operation"><c>Create</c> or <c>Modify</c>.</param>
/// <param name="Proposed">The proposed persistent object, as JSON (it was never presented, so it is not one).</param>
public sealed record ApprovalProposalResponse(
    string RequestId,
    string Status,
    string Operation,
    Guid ObjectTypeId,
    string? TargetId,
    bool IsStale,
    JsonElement Proposed,
    PersistentObject? Current);

/// <summary><c>GET /spark/approvals/proposal?id=…</c>: one proposal and its target, for the diff.</summary>
[MemberOf<ApprovalsGroup>]
internal sealed partial class GetApprovalProposal : IGetEndpoint
{
    public static string Path => "/proposal";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ApprovalVisibility visibility;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Query["id"].ToString() is not { Length: > 0 } id)
            return addOn.Refusal(httpContext);

        using var session = store.OpenAsyncSession();
        var request = await session.LoadAsync<ApprovalRequest>(id, httpContext.RequestAborted);
        if (request?.Proposal is not { } proposal || !await visibility.MaySeeAsync(request, httpContext.RequestAborted))
            return addOn.Refusal(httpContext);

        using var proposed = JsonDocument.Parse(proposal.PersistentObjectJson);
        var targetId = request.SubjectId is { Length: > 0 } subject ? subject : null;
        PersistentObject? current = null;
        if (targetId is not null)
        {
            try { current = await databaseAccess.GetPersistentObjectAsync(proposal.ObjectTypeId, targetId); }
            catch (SparkAccessDeniedException) { current = null; }
        }

        // The base moved since the proposal: approving it will answer 409, so say so before.
        var isStale = request.Status == ApprovalStatuses.Pending
            && proposal.BaseEtag is { } baseEtag
            && (current is null || current.Etag != baseEtag);

        return addOn.Envelope(new ApprovalProposalResponse(
            request.Id!,
            request.Status,
            proposal.Operation,
            proposal.ObjectTypeId,
            targetId,
            isStale,
            proposed.RootElement.Clone(),
            current), StatusCodes.Status200OK);
    }
}
