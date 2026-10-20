using System.Security.Claims;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Approvals.Models;
using MintPlayer.Spark.Approvals.Services;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Approvals.Proposals;

/// <summary>
/// The built-in kind of document proposals (commerce PRD §4.13, X7): a save of a type listed under
/// <c>Spark:Approvals:Types</c> that waits for an approver. Decided by holders of <c>ApproveProposal/&lt;Type&gt;</c>.
/// </summary>
public sealed class DocumentProposalApproval : IApprovalKind
{
    public static string Name => "approvals.document";
}

/// <summary>
/// Who decides a proposal: the holders of <c>ApproveProposal/&lt;Type&gt;</c>, asked live. Nothing lists them, so a
/// proposal names no deciders; they find it in their inbox through <see cref="DecidesAllAsync"/>.
/// </summary>
internal sealed partial class DocumentProposalDeciders : IApprovalDeciders<DocumentProposalApproval>
{
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly IOptions<SparkApprovalsOptions> options;

    public async ValueTask<bool> MayDecideAsync(ApprovalDeciderContext context)
        => await permissions.IsAllowedAsync(ApprovalRights.ApproveProposal, context.Request.Subject.Type, context.CancellationToken);

    public async ValueTask<ApprovalScope> DecidesAllAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var types = new List<string>();
        foreach (var type in options.Value.Types.Keys)
            if (await permissions.IsAllowedAsync(ApprovalRights.ApproveProposal, type, cancellationToken))
                types.Add(type);
        return types.Count == 0 ? ApprovalScope.None : ApprovalScope.Types(types);
    }
}

/// <summary>
/// Turns a save of a governed type into a proposal (<see cref="ISaveReplacement"/>), and tells a page of a governed
/// row that an edit of it is pending (<c>metadata.approval</c>, for ng-spark's title badge).
/// </summary>
/// <remarks>
/// <para>
/// <b>Who proposes:</b> anyone who may save the type (the save path checked the right, the row, the shield and the
/// validation before asking). A holder of <c>ApproveProposal/&lt;Type&gt;</c> saves directly: an approver's own edit
/// is its approval. A system-context write (a migration, a job) is never diverted.
/// </para>
/// <para>
/// <b>Pending creations are invisible:</b> the row does not exist until it is approved; the proposal lives in its own
/// collection, so no query of the type can show it.
/// </para>
/// </remarks>
internal sealed partial class DocumentProposals : ISaveReplacement, IAfterLoad
{
    /// <summary>The <see cref="PersistentObject.Metadata"/> key of a row's pending proposal.</summary>
    internal const string MetadataKey = "approval";

    [Inject] private readonly IOptions<SparkApprovalsOptions> options;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly SparkApprovals approvals;
    [Inject] private readonly ApprovalVisibility visibility;
    [Inject] private readonly IManager manager;

    public bool AppliesTo(Type entityType)
        => options.Value.Types.Count > 0
           && entityType.FullName is { } clrType
           && modelLoader.GetEntityTypeByClrType(clrType) is { } definition
           && options.Value.Types.ContainsKey(definition.Name);

    public async ValueTask<SaveReplacementReceipt?> ReplaceAsync(SaveReplacementContext context)
    {
        if (context.IsSystemContext || context.Operation is not (PersistentObjectOperation.New or PersistentObjectOperation.Save))
            return null;
        if (modelLoader.GetEntityType(context.PersistentObject.ObjectTypeId) is not { } definition
            || !options.Value.Types.TryGetValue(definition.Name, out var governed))
            return null;
        if (context.IsNew ? !governed.Create : !governed.Modify)
            return null;
        if (await permissions.IsAllowedAsync(ApprovalRights.ApproveProposal, definition.Name))
            return null;

        var breadcrumb = context.PersistentObject.Breadcrumb is { Length: > 0 } b ? b : context.Id;
        var title = context.IsNew
            ? manager.GetTranslatedMessage("approvals.proposalNew", definition.Name)
            : manager.GetTranslatedMessage("approvals.proposalEdit", definition.Name, breadcrumb ?? string.Empty);

        var (result, _, _) = await approvals.StageAsync(
            DocumentProposalApproval.Name,
            context.GetSession(),
            new ApprovalSubmission
            {
                Subject = new ApprovalSubject(definition.Name, context.Id ?? string.Empty),
                Title = title,
                Url = context.Id is { Length: > 0 } id ? $"/po/{definition.Id}/{Uri.EscapeDataString(id)}" : null,
            },
            CancellationToken.None,
            new ApprovalProposal
            {
                Operation = context.IsNew ? ApprovalProposalOperations.Create : ApprovalProposalOperations.Modify,
                ObjectTypeId = context.PersistentObject.ObjectTypeId,
                PersistentObjectJson = context.PersistentObjectJson,
                BaseEtag = context.BaseEtag,
            });
        return new SaveReplacementReceipt("approval", result.RequestId);
    }

    /// <summary>
    /// A row with an edit pending: <c>metadata.approval = { requestId, own, mayDecide }</c>, shown only to the proposer
    /// and to those who may decide it (anyone else cannot see the request, so is not told it exists).
    /// </summary>
    public async ValueTask OnAfterLoadAsync(LoadContext context)
    {
        var po = context.PersistentObject;
        if (po.Id is not { Length: > 0 } id || visibility.CallerId is not { } me)
            return;
        var session = context.GetSession();
        var kind = DocumentProposalApproval.Name;
        var pending = await session.Query<ApprovalRequest>()
            .Where(r => r.Kind == kind && r.Status == ApprovalStatuses.Pending && r.SubjectId == id)
            .Take(16)
            .ToListAsync();
        foreach (var request in pending.Where(approvals.IsOpen).OrderByDescending(r => r.RequestedAtUtc))
        {
            var own = request.RequestedBy == me;
            var mayDecide = !own && await visibility.MayDecideAsync(request, session);
            if (!own && !mayDecide)
                continue;
            po.Metadata ??= [];
            po.Metadata[MetadataKey] = new Dictionary<string, object?>
            {
                ["requestId"] = request.Id,
                ["status"] = request.Status,
                ["own"] = own,
                ["mayDecide"] = mayDecide,
            };
            return;
        }
    }
}
