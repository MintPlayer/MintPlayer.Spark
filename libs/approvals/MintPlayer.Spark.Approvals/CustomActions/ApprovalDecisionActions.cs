using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Approvals.CustomActions;

/// <summary>The decision actions on <c>ApprovalRequest</c>, named with the library's prefix (custom-action names are first-wins across assemblies).</summary>
public static class ApprovalActionNames
{
    public const string Approve = "ApprovalApprove";
    public const string Reject = "ApprovalReject";
    public const string Withdraw = "ApprovalWithdraw";
    public const string Revoke = "ApprovalRevoke";
}

/// <summary>
/// Approves the selected requests, or the one on the page. Every request is decided through
/// <see cref="ISparkApprovals"/>, which asks the kind's live gate: a request the caller may not decide is skipped as
/// if it were not there (no existence oracle), as the rights alone (granted to every signed-in user) promise nothing.
/// </summary>
internal sealed partial class ApprovalApproveAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkApprovals approvals;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var results = new List<ApprovalResult>();
        foreach (var id in ApprovalActionTargets.Ids(args))
            results.Add(await approvals.DecideAsync(id, ApprovalDecision.Approve, reason: null, cancellationToken));
        ApprovalActionTargets.Report(manager, results, "approvals.approvedNotice");
    }
}

/// <summary>Rejects the selected requests with one reason, asked for in a form (<c>ApprovalReason</c>).</summary>
internal sealed partial class ApprovalRejectAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkApprovals approvals;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var ids = ApprovalActionTargets.Ids(args);
        if (ids.Count == 0)
            return;
        if (await ApprovalActionTargets.AskReasonAsync(manager, "approvals.rejectTitle", cancellationToken) is not { } reason)
            return;

        var results = new List<ApprovalResult>();
        foreach (var id in ids)
            results.Add(await approvals.DecideAsync(id, ApprovalDecision.Reject, reason, cancellationToken));
        ApprovalActionTargets.Report(manager, results, "approvals.rejectedNotice");
    }
}

/// <summary>The requester takes their pending requests back.</summary>
internal sealed partial class ApprovalWithdrawAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkApprovals approvals;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var results = new List<ApprovalResult>();
        foreach (var id in ApprovalActionTargets.Ids(args))
            results.Add(await approvals.WithdrawAsync(id, cancellationToken));
        ApprovalActionTargets.Report(manager, results, "approvals.withdrawnNotice");
    }
}

/// <summary>A decider takes back an approval, with a reason (only kinds whose handler supports it).</summary>
internal sealed partial class ApprovalRevokeAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkApprovals approvals;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var ids = ApprovalActionTargets.Ids(args);
        if (ids.Count == 0)
            return;
        if (await ApprovalActionTargets.AskReasonAsync(manager, "approvals.revokeTitle", cancellationToken) is not { } reason)
            return;

        var results = new List<ApprovalResult>();
        foreach (var id in ids)
        {
            try { results.Add(await approvals.RevokeAsync(id, reason, cancellationToken)); }
            catch (NotSupportedException) { results.Add(new ApprovalResult(ApprovalOutcome.NotFound)); }
        }
        ApprovalActionTargets.Report(manager, results, "approvals.revokedNotice");
    }
}

internal static class ApprovalActionTargets
{
    /// <summary>The page's request, else the selected rows (server-loaded and row-checked by the framework).</summary>
    public static IReadOnlyList<string> Ids(CustomActionArgs args)
        => args.Parent?.Id is { Length: > 0 } parent
            ? [parent]
            : args.SelectedItems.Select(item => item.Id).OfType<string>().Where(id => id.Length > 0).Distinct().ToList();

    /// <summary>
    /// Asks for the reason in the <c>ApprovalReason</c> form; null when the caller cancelled. The answered form is not
    /// validated by the framework, so an empty reason is asked again by the engine's <see cref="ApprovalOutcome.ReasonRequired"/>.
    /// </summary>
    public static async Task<string?> AskReasonAsync(IManager manager, string titleKey, CancellationToken cancellationToken)
    {
        var form = await manager.GetPersistentObjectAsync("ApprovalReason", cancellationToken: cancellationToken);
        var confirm = manager.GetTranslatedMessage("approvals.confirm");
        manager.Retry.Action(
            title: manager.GetTranslatedMessage(titleKey),
            options: [confirm],
            defaultOption: confirm,
            persistentObject: form,
            cancellable: true);

        var answer = manager.Retry.Result!;
        if (answer.Option == RetryResult.CancelOption)
            return null;
        var reason = answer.PersistentObject is { } filled && filled.TryGetAttribute("Reason", out var attribute)
            ? attribute.Value?.ToString()
            : null;
        if (string.IsNullOrWhiteSpace(reason))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("approvals.reasonRequired"), NotificationKind.Warning);
            return null;
        }
        return reason;
    }

    /// <summary>One notice for the whole selection, and the inbox refreshed.</summary>
    public static void Report(IManager manager, IReadOnlyList<ApprovalResult> results, string doneKey)
    {
        var done = results.Count(r => r.Outcome == ApprovalOutcome.Done);
        if (done > 0)
            manager.Client.Notify(manager.GetTranslatedMessage(doneKey, done), NotificationKind.Success);

        foreach (var problem in results.Where(r => r.Outcome != ApprovalOutcome.Done).GroupBy(r => r.Outcome))
        {
            var key = problem.Key switch
            {
                ApprovalOutcome.NotPending => "approvals.notPending",
                ApprovalOutcome.Stale => "approvals.stale",
                ApprovalOutcome.Unwritable => "approvals.unwritable",
                ApprovalOutcome.Invalid => "approvals.invalid",
                ApprovalOutcome.Cancelled => "approvals.cancelled",
                ApprovalOutcome.ReasonRequired => "approvals.reasonRequired",
                _ => "approvals.gone",
            };
            manager.Client.Notify(manager.GetTranslatedMessage(key, problem.Count()), NotificationKind.Warning);
        }

        foreach (var alias in new[] { "approvals", "pending-approvals", "my-approval-requests" })
            manager.Client.RefreshQuery(alias);
    }
}
