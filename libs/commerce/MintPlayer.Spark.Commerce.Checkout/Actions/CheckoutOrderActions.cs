using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Checkout.Actions;

/// <summary>
/// Orders are written by checkout only (only <c>QueryRead</c> is granted) and read under <see cref="OrderAccess"/>'s row
/// rule: the buyer's own, a seller's Owners/Admins/finance/box-office members the seller's, the platform organization
/// every one. Another caller gets the missing-row 404 (#453).
/// </summary>
public partial class CheckoutOrderActions : DefaultPersistentObjectActions<CheckoutOrder>
{
    [Inject] private readonly OrderAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>CheckoutOrders</c>, under the row rule.</summary>
    public IQueryable<CheckoutOrder> CheckoutOrders() => session.Query<CheckoutOrder>();

    public override async Task<Expression<Func<CheckoutOrder, bool>>?> GetRowFilterAsync(string action)
        => await access.RowFilterAsync();
}
