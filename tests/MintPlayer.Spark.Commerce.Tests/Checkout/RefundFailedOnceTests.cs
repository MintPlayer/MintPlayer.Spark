using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Payments.Models;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Commerce.Tests.Checkout;

/// <summary>
/// Item 4: <c>RefundFailed</c> is raised once per refund. Before, a ledger refund's failure notice reached both checkout's
/// payment recipient (M7, <c>OrderLifecycle.OnRefundAsync</c>) and the refund recipient (M9), and every redelivery of the
/// notice raised it again through M7.
/// </summary>
public class RefundFailedOnceTests : SparkTestDriver
{
    [Fact]
    public async Task A_failed_ledger_refund_raises_RefundFailed_once_however_often_the_notice_comes()
    {
        var (host, provider, _) = RefundHost.Build(Store);
        await host.ItemAsync("shirts/f", "shop", 20m);
        RefundHost.Goods(host, "shirts/f");
        var order = await RefundHost.PaidOrderAsync(host, "visitor-f", ("shirts/f", 1));
        var refunds = host.Get<RefundService>();

        // Unshipped goods: the withdrawal refunds at once; the provider answers "pending".
        await refunds.WithdrawAsync(await RefundHost.OrderAsync(Store, order.Id!), null, null, null, null, ReturnSource.Online, default, null, confirm: true, default);
        var reference = provider.Executed.Keys.Single();
        var refund = new ProviderPaymentRefund
        {
            Reference = reference, Amount = provider.Executed[reference].Amount, Status = ProviderRefundStatus.Failed, FailureReason = RefundFailureReason.Other,
        };
        var notice = new RefundChangeNotice(new ProviderPayment { Id = order.Payment!.PaymentId!, Reference = order.Id! }, refund, ProviderRefundStatus.Pending);

        var recipient = host.Get<RefundPaymentRecipient>();
        for (var i = 0; i < 2; i++) // delivered twice, to both recipients
        {
            await host.Lifecycle.OnRefundAsync(notice, default);
            await recipient.OnRefundChangedAsync(notice, default);
        }

        host.Events.Events.Count(e => e.Kind == OrderEventKind.RefundFailed).Should().Be(1);
        (await RefundHost.OrderAsync(Store, order.Id!)).Log.Count(l => l.Event == "refund-failed").Should().Be(1);
    }
}
