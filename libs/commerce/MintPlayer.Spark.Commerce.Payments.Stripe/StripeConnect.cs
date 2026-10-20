using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MintPlayer.Spark.Commerce.Payments.Stripe;

/// <summary>
/// Stripe Connect onboarding (D5, spike S6; <see href="https://docs.stripe.com/connect/hosted-onboarding">hosted
/// onboarding</see>): the platform creates a connected account (<c>POST /v1/accounts</c>), then an Account Link
/// (<c>POST /v1/account_links</c>, <c>type=account_onboarding</c>) where the seller completes Stripe's verification. An
/// expired link sends the seller to <c>refresh_url</c>, which starts a new one. The verification state arrives as
/// <c>account.updated</c> on the Connect webhook endpoint (and is re-read when the seller returns).
/// </summary>
internal sealed class StripeConnect(StripeApi api) : IConnectOnboardingProvider
{
    public string Provider => StripePaymentProvider.ProviderName;

    public async Task<ConnectOnboardingStart> StartAsync(string sellerOrganizationId, string? existingAccountId, string returnUrl, string refreshUrl, string state, CancellationToken cancellationToken)
    {
        var platform = MerchantContext.Platform(Provider);
        var accountId = existingAccountId;
        if (accountId is null)
        {
            var form = new StripeForm()
                .Add("type", api.Options.ConnectAccountType)
                .Add("country", api.Options.ConnectCountry)
                .Add("metadata[spark_seller]", sellerOrganizationId);
            var account = await api.PostAsync("accounts", platform, form,
                PaymentIdempotencyKeys.For(PaymentIdempotencyKeys.Operations.Onboard, sellerOrganizationId), cancellationToken);
            accountId = StripeJson.String(account, "id") ?? throw new InvalidOperationException("Stripe answered an account without an id.");
        }

        // An Account Link is single-use and short-lived: never reuse one, so no idempotency key that could replay it.
        var link = await api.PostAsync("account_links", platform, new StripeForm()
            .Add("account", accountId)
            .Add("refresh_url", refreshUrl)
            .Add("return_url", returnUrl)
            .Add("type", "account_onboarding"), null, cancellationToken);
        var url = StripeJson.String(link, "url") ?? throw new InvalidOperationException("Stripe answered an account link without a URL.");
        return new ConnectOnboardingStart(url, accountId);
    }

    public async Task<ConnectOnboardingResult> CompleteAsync(string? accountId, string? code, string redirectUri, CancellationToken cancellationToken)
    {
        if (accountId is null)
            throw new InvalidOperationException("Stripe onboarding completed without an account.");
        var account = StripePaymentProvider.ReadAccount(await api.GetAsync($"accounts/{Uri.EscapeDataString(accountId)}", MerchantContext.Platform(Provider), cancellationToken));
        return new ConnectOnboardingResult(accountId, account, null, null);
    }

    public async Task<ConnectedAccountSnapshot> GetAccountAsync(MerchantContext merchant, CancellationToken cancellationToken)
        => StripePaymentProvider.ReadAccount(await api.GetAsync($"accounts/{Uri.EscapeDataString(merchant.AccountId!)}", MerchantContext.Platform(Provider), cancellationToken));

    public Task<ConnectTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken) => Task.FromResult<ConnectTokens?>(null);
}

/// <summary>
/// Stripe payouts with their balance transactions (GB4;
/// <see href="https://docs.stripe.com/api/balance_transactions/list">balance transactions</see>, filtered by
/// <c>payout</c>). Only paid payouts are imported. A connected seller's are read with its <c>Stripe-Account</c>.
/// </summary>
internal sealed class StripeSettlementSource(StripeApi api) : ISettlementSource
{
    public string Provider => StripePaymentProvider.ProviderName;

    public async IAsyncEnumerable<ProviderSettlement> GetSettlementsAsync(MerchantContext merchant, DateTimeOffset since, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payouts = new List<JsonElement>();
        await foreach (var payout in ListAsync($"payouts?status=paid&created[gt]={since.ToUnixTimeSeconds()}", merchant, cancellationToken))
            payouts.Add(payout);

        foreach (var payout in payouts.OrderBy(p => StripeJson.Long(p, "created")))
        {
            var id = StripeJson.String(payout, "id")!;
            var lines = new List<ProviderSettlementLine>();
            await foreach (var transaction in ListAsync($"balance_transactions?payout={Uri.EscapeDataString(id)}", merchant, cancellationToken))
                lines.Add(Line(transaction));
            var amount = StripeJson.ReadMoney(payout, "amount")!;
            yield return new ProviderSettlement(id, StripeJson.String(payout, "statement_descriptor") ?? StripeJson.String(payout, "trace_id"),
                StripeJson.Time(payout, "created")!.Value, StripeJson.Time(payout, "arrival_date"), amount, lines);
        }
    }

    /// <summary>A list endpoint, every page (<c>has_more</c>, <c>starting_after</c>).</summary>
    private async IAsyncEnumerable<JsonElement> ListAsync(string path, MerchantContext merchant, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? after = null;
        while (true)
        {
            var page = await api.GetAsync(path + "&limit=100" + (after is null ? "" : "&starting_after=" + Uri.EscapeDataString(after)), merchant, cancellationToken);
            string? last = null;
            foreach (var item in StripeJson.Array(page, "data"))
            {
                last = StripeJson.String(item, "id");
                yield return item;
            }
            if (!StripeJson.Bool(page, "has_more") || last is null)
                yield break;
            after = last;
        }
    }

    /// <summary>One balance transaction as a settlement line (gross = <c>amount</c>, <c>fee</c>, <c>net</c>).</summary>
    internal static ProviderSettlementLine Line(JsonElement transaction)
    {
        var type = StripeJson.String(transaction, "type");
        var category = StripeJson.String(transaction, "reporting_category");
        var kind = type switch
        {
            "charge" or "payment" => SettlementLineKind.Payment,
            "refund" or "payment_refund" => SettlementLineKind.Refund,
            "adjustment" when category == "dispute" => SettlementLineKind.Chargeback,
            "adjustment" when category == "dispute_reversal" => SettlementLineKind.ChargebackReversal,
            "application_fee" => SettlementLineKind.ApplicationFee,
            "application_fee_refund" => SettlementLineKind.ApplicationFeeRefund,
            "stripe_fee" or "tax" => SettlementLineKind.ProviderFee,
            "payout" => SettlementLineKind.Payout,
            "transfer" or "transfer_refund" => SettlementLineKind.Transfer,
            _ => SettlementLineKind.Other,
        };
        var fee = StripeJson.ReadMoney(transaction, "fee");
        return new ProviderSettlementLine(
            StripeJson.String(transaction, "id")!,
            kind,
            StripeJson.Id(transaction, "source"),
            StripeJson.ReadMoney(transaction, "amount")!,
            fee is { IsZero: false } ? fee : null,
            StripeJson.ReadMoney(transaction, "net")!,
            StripeJson.Time(transaction, "created") ?? DateTimeOffset.UnixEpoch,
            StripeJson.String(transaction, "description"));
    }
}
