using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Commerce.Payments.CustomActions;
using MintPlayer.Spark.Commerce.Payments.Models;
using MintPlayer.Spark.Commerce.Payments.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Payments.Actions;

// Every payments type is written by the library only (only QueryRead and the custom actions are granted) and read
// under FinanceAccess's row rule: the seller's Owners, Admins and finance members, and the platform organization's.
// Another caller gets the missing-row 404 (#453).

public partial class ProviderPaymentActions : DefaultPersistentObjectActions<ProviderPayment>
{
    [Inject] private readonly FinanceAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>ProviderPayments</c>, under the row rule.</summary>
    public IQueryable<ProviderPayment> ProviderPayments() => session.Query<ProviderPayment>();


    public override async Task<Expression<Func<ProviderPayment, bool>>?> GetRowFilterAsync(string action)
        => await access.RowFilterAsync<ProviderPayment>(p => p.SellerOrganizationId);
}

public partial class SellerPaymentAccountActions : DefaultPersistentObjectActions<SellerPaymentAccount>
{
    [Inject] private readonly FinanceAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>SellerPaymentAccounts</c>, under the row rule.</summary>
    public IQueryable<SellerPaymentAccount> SellerPaymentAccounts() => session.Query<SellerPaymentAccount>();


    public override async Task<Expression<Func<SellerPaymentAccount, bool>>?> GetRowFilterAsync(string action)
        => await access.RowFilterAsync<SellerPaymentAccount>(a => a.SellerOrganizationId);
}

public partial class PlatformFeeEntryActions : DefaultPersistentObjectActions<PlatformFeeEntry>
{
    [Inject] private readonly FinanceAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>PlatformFeeLedger</c>, under the row rule.</summary>
    public IQueryable<PlatformFeeEntry> PlatformFeeLedger() => session.Query<PlatformFeeEntry>();


    public override async Task<Expression<Func<PlatformFeeEntry, bool>>?> GetRowFilterAsync(string action)
        => await access.RowFilterAsync<PlatformFeeEntry>(e => e.SellerOrganizationId);
}

public partial class SettlementLineActions : DefaultPersistentObjectActions<SettlementLine>
{
    [Inject] private readonly FinanceAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>SettlementLines</c>, under the row rule.</summary>
    public IQueryable<SettlementLine> SettlementLines() => session.Query<SettlementLine>();


    public override async Task<Expression<Func<SettlementLine, bool>>?> GetRowFilterAsync(string action)
        => await access.RowFilterAsync<SettlementLine>(l => l.SellerOrganizationId);
}

/// <summary>Disputes: Submit evidence is offered while a response is due; Accept while it is open.</summary>
public partial class PaymentDisputeActions : DefaultPersistentObjectActions<PaymentDispute>
{
    [Inject] private readonly FinanceAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>PaymentDisputes</c>, under the row rule.</summary>
    public IQueryable<PaymentDispute> PaymentDisputes() => session.Query<PaymentDispute>();

    [Inject] private readonly PaymentProviderRegistry registry;

    public override async Task<Expression<Func<PaymentDispute, bool>>?> GetRowFilterAsync(string action)
        => await access.RowFilterAsync<PaymentDispute>(d => d.SellerOrganizationId);

    public override Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context.Entity is not PaymentDispute dispute)
            return Task.CompletedTask;
        var byApi = registry.All.FirstOrDefault(p => p.Name == dispute.Provider)?.Capabilities.DisputeEvidenceByApi ?? false;
        if (dispute.IsClosed || dispute.Status != DisputeStatus.NeedsResponse || !byApi)
            target.DisableActions(PaymentActionNames.SubmitEvidence);
        if (dispute.IsClosed)
            target.DisableActions(PaymentActionNames.AcceptDispute);
        return Task.CompletedTask;
    }
}

/// <summary>The reconciliation queue (GB4): open items first; Resolve only on an open item.</summary>
public partial class ReconciliationItemActions : DefaultPersistentObjectActions<ReconciliationItem>
{
    [Inject] private readonly FinanceAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>ReconciliationQueue</c>, under the row rule.</summary>
    public IQueryable<ReconciliationItem> ReconciliationQueue() => session.Query<ReconciliationItem>();


    public override async Task<Expression<Func<ReconciliationItem, bool>>?> GetRowFilterAsync(string action)
        => await access.RowFilterAsync<ReconciliationItem>(i => i.SellerOrganizationId);

    public override Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context.Entity is ReconciliationItem { Status: ReconciliationStatus.Resolved })
            target.DisableActions(PaymentActionNames.ResolveReconciliation);
        return Task.CompletedTask;
    }
}
