using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Cron;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Checkout.Jobs;

/// <summary>
/// The PaymentPending sweep: an order past its payment deadline (plus grace) is reconciled with the provider (a payment
/// that completed but whose webhook was lost confirms now) or expired, with the provider payment cancelled actively
/// (D3). It also covers OnPayment-only orders, which hold no claim the inventory sweeper could report, and a lapsed
/// claim whose <see cref="Inventory.Jobs.IInventoryClaimLapsed"/> call was lost.
/// </summary>
internal sealed class CheckoutSweepJob(
    IDocumentStore store,
    IServiceProvider services,
    IOptionsMonitor<SparkCommerceCheckoutOptions> options,
    TimeProvider clock,
    ILogger<CheckoutSweepJob> logger) : ISparkCronJob
{
    public static string CronSchedule => "*/30 * * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - options.CurrentValue.PaymentGrace;
        List<string> due;
        using (var session = store.OpenAsyncSession())
        {
            due = await session.Query<CheckoutOrder>()
                .Where(o => o.State == OrderState.PaymentPending && o.PaymentPendingUntil != null && o.PaymentPendingUntil < cutoff)
                .Select(o => o.Id!)
                .Take(200)
                .ToListAsync(cancellationToken);
        }
        foreach (var orderId in due)
        {
            try
            {
                using var scope = services.CreateScope();
                var lifecycle = scope.ServiceProvider.GetRequiredService<OrderLifecycle>();
                var payments = scope.ServiceProvider.GetRequiredService<ISparkPayments>();
                await ReconcileAsync(orderId, lifecycle, payments, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sweeping order {OrderId} failed.", orderId);
            }
        }
    }

    internal static async Task ReconcileAsync(string orderId, OrderLifecycle lifecycle, ISparkPayments payments, CancellationToken cancellationToken)
    {
        string? paymentId;
        using (var probe = lifecycle.OpenSession())
            paymentId = (await probe.LoadAsync<CheckoutOrder>(orderId, cancellationToken))?.Payment?.PaymentId;

        if (paymentId is not null && await payments.GetPaymentAsync(paymentId, refresh: true, cancellationToken) is { } payment
            && payment.Status is ProviderPaymentStatus.Paid or ProviderPaymentStatus.Authorized)
        {
            await lifecycle.OnPaymentAsync(payment, cancellationToken);
            return;
        }
        await lifecycle.UpdateAsync(orderId, (_, order, raised)
            => lifecycle.EndUnpaidAsync(order, OrderState.Expired, "payment window closed", raised, cancelProvider: true, cancellationToken), cancellationToken);
    }
}

/// <summary>
/// GC1: one reminder for a basket left untouched for <c>AbandonedBaskets:Delay</c>, only in channels that opted in, only
/// to a buyer with marketing consent (GA9: account preference, or the e-mail's soft opt-in), and <b>never</b> for a basket
/// that holds seats (a hold expiring is not a reminder trigger). Marketing mail: unsubscribe link and List-Unsubscribe.
/// </summary>
internal sealed class AbandonedBasketJob(
    IDocumentStore store,
    IServiceProvider services,
    IOptionsMonitor<SparkCommerceCheckoutOptions> options,
    TimeProvider clock,
    ILogger<AbandonedBasketJob> logger) : ISparkCronJob
{
    public static string CronSchedule => "0 */10 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var optedIn = settings.Channels.Where(c => c.Value.AbandonedBasketReminder).Select(c => c.Key).ToList();
        if (optedIn.Count == 0 || services.GetService<ISparkConsent>() is not { } consent)
            return; // nothing opted in, or no consent primitive: no marketing mail at all (GA9)

        var now = clock.GetUtcNow();
        var idleSince = now - settings.AbandonedBaskets.Delay;
        var notBefore = now - settings.AbandonedBaskets.MaxAge;
        List<CheckoutBasket> candidates;
        using (var session = store.OpenAsyncSession())
        {
            candidates = await session.Query<CheckoutBasket>()
                .Where(b => b.ReminderSentAt == null && b.PendingOrderId == null && b.UpdatedAt < idleSince && b.UpdatedAt > notBefore)
                .Take(settings.AbandonedBaskets.BatchSize)
                .ToListAsync(cancellationToken);
        }

        using var scope = services.CreateScope();
        var mails = scope.ServiceProvider.GetRequiredService<CheckoutMails>();
        var baskets = scope.ServiceProvider.GetRequiredService<BasketStore>();
        foreach (var basket in candidates)
        {
            try
            {
                if (!Eligible(basket, optedIn))
                    continue;
                var (email, name, culture, subject) = await RecipientAsync(basket, cancellationToken);
                if (email is null || !await consent.AllowsMarketingMailAsync(basket.UserId, email, cancellationToken))
                    continue;
                if (await mails.SendReminderAsync(basket, email, name, culture, subject, cancellationToken))
                    await baskets.TouchAsync(basket.Id!, b => b.ReminderSentAt = now, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Abandoned-basket reminder for {BasketId} failed.", basket.Id);
            }
        }
    }

    /// <summary>The rule, separated for the unit test: a line in an opted-in channel, and no line holding seats.</summary>
    internal static bool Eligible(CheckoutBasket basket, IReadOnlyCollection<string> optedInChannels)
        => basket.Lines.Count > 0
            && basket.Lines.Any(l => optedInChannels.Contains(l.Channel, StringComparer.OrdinalIgnoreCase))
            && !basket.Lines.Any(l => l.Units.Count > 0 || l.Picks.Count > 0 || l.HeldUnits.Count > 0);

    private async Task<(string? Email, string? Name, string? Culture, SparkConsentSubject Subject)> RecipientAsync(CheckoutBasket basket, CancellationToken cancellationToken)
    {
        if (basket.UserId is { } userId)
        {
            using var session = store.OpenAsyncSession();
            var user = await session.LoadAsync<SparkUser>(userId, cancellationToken);
            if (user is { Email: { Length: > 0 } email, EmailConfirmed: true })
                return (email, user.UserName, user.PreferredCulture, SparkConsentSubject.Account(userId));
            return (null, null, null, default);
        }
        return basket.Email is { Length: > 0 } guestEmail
            ? (guestEmail, null, null, SparkConsentSubject.Email(guestEmail))
            : (null, null, null, default);
    }
}
