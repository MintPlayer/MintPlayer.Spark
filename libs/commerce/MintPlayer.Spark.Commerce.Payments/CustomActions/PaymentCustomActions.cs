using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Commerce.Payments.Services;

namespace MintPlayer.Spark.Commerce.Payments.CustomActions;

/// <summary>The custom actions of the payments types, prefixed with the library's name (custom-action names are first-wins across assemblies).</summary>
public static class PaymentActionNames
{
    public const string SubmitEvidence = "PaymentDisputeSubmitEvidence";
    public const string AcceptDispute = "PaymentDisputeAccept";
    public const string ResolveReconciliation = "PaymentReconciliationResolve";
}

/// <summary>Submits the dispute's evidence pack (Stripe), or tells the caller to download it (Mollie).</summary>
internal sealed partial class PaymentDisputeSubmitEvidenceAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkPayments payments;
    [Inject] private readonly FinanceAccess access;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent?.Id is not { Length: > 0 } id || access.CallerId is not { } me)
            return;
        var result = await payments.SubmitEvidenceAsync(id, me, cancellationToken);
        var key = result switch
        {
            DisputeEvidenceSubmission.Submitted => "commercePayments.evidenceSubmitted",
            DisputeEvidenceSubmission.ManualOnly => "commercePayments.evidenceManualOnly",
            _ => "commercePayments.disputeClosed",
        };
        manager.Client.Notify(manager.GetTranslatedMessage(key), result == DisputeEvidenceSubmission.Submitted ? NotificationKind.Success : NotificationKind.Warning);
        manager.Client.RefreshQuery("payment-disputes");
    }
}

/// <summary>Accepts the dispute as lost (a Mollie chargeback the seller does not contest). Confirmation in actions.json.</summary>
internal sealed partial class PaymentDisputeAcceptAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkPayments payments;
    [Inject] private readonly FinanceAccess access;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent?.Id is not { Length: > 0 } id || access.CallerId is not { } me)
            return;
        await payments.AcceptDisputeAsync(id, me, cancellationToken);
        manager.Client.Notify(manager.GetTranslatedMessage("commercePayments.disputeAccepted"), NotificationKind.Success);
        manager.Client.RefreshQuery("payment-disputes");
    }
}

/// <summary>Resolves reconciliation items with one note, asked for in the <c>ReconciliationNote</c> form.</summary>
internal sealed partial class PaymentReconciliationResolveAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly SettlementReconciler reconciler;
    [Inject] private readonly FinanceAccess access;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (access.CallerId is not { } me)
            return;
        IReadOnlyList<string> ids = args.Parent?.Id is { Length: > 0 } parent
            ? [parent]
            : args.SelectedItems.Select(item => item.Id).OfType<string>().Where(id => id.Length > 0).Distinct().ToList();
        if (ids.Count == 0)
            return;

        var form = await manager.GetPersistentObjectAsync("ReconciliationNote", cancellationToken: cancellationToken);
        var confirm = manager.GetTranslatedMessage("commercePayments.confirm");
        manager.Retry.Action(
            title: manager.GetTranslatedMessage("commercePayments.resolveTitle"),
            options: [confirm],
            defaultOption: confirm,
            persistentObject: form,
            cancellable: true);
        var answer = manager.Retry.Result!;
        if (answer.Option == RetryResult.CancelOption)
            return;
        var note = answer.PersistentObject is { } filled && filled.TryGetAttribute("Note", out var attribute) ? attribute.Value?.ToString() : null;
        if (string.IsNullOrWhiteSpace(note))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("commercePayments.noteRequired"), NotificationKind.Warning);
            return;
        }

        var resolved = 0;
        foreach (var id in ids)
        {
            // The framework loaded and row-checked the selection; the reconciler only resolves open items.
            if (await reconciler.ResolveAsync(id, note, me, cancellationToken))
                resolved++;
        }
        manager.Client.Notify(manager.GetTranslatedMessage("commercePayments.resolvedNotice", resolved), NotificationKind.Success);
        manager.Client.RefreshQuery("reconciliation-queue");
    }
}
