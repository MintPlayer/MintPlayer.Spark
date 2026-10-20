using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Payments.Mollie;

/// <summary>
/// Mollie Connect (D5, spike S6; <see href="https://docs.mollie.com/docs/connect-platforms-getting-started">Connect</see>):
/// OAuth 2 authorization code. The seller signs in at Mollie and grants the platform's app the scopes in
/// <see cref="MolliePaymentsOptions.Scopes"/>; the code is exchanged for an access token (one hour) and a refresh token.
/// The seller's verification is Mollie's onboarding status (<c>GET /v2/onboarding/me</c>: <c>needs-data</c>,
/// <c>in-review</c>, <c>completed</c>), which Mollie sends no webhook for, so the payments package polls it.
/// </summary>
internal sealed class MollieConnect(MollieApi api, TimeProvider time) : IConnectOnboardingProvider
{
    public string Provider => MolliePaymentProvider.ProviderName;

    public Task<ConnectOnboardingStart> StartAsync(string sellerOrganizationId, string? existingAccountId, string returnUrl, string refreshUrl, string state, CancellationToken cancellationToken)
    {
        var options = api.Options;
        if (string.IsNullOrEmpty(options.ClientId))
            throw new InvalidOperationException("Mollie Connect needs Spark:Commerce:Payments:Mollie:ClientId.");
        // The redirect URI must equal the one registered with the app exactly, so it carries no query; the state travels apart.
        var url = QueryHelpers.AddQueryString(options.AuthorizeUrl, new Dictionary<string, string?>
        {
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = WithoutQuery(returnUrl),
            ["state"] = state,
            ["scope"] = options.Scopes,
            ["response_type"] = "code",
            ["approval_prompt"] = "auto",
        });
        return Task.FromResult(new ConnectOnboardingStart(url, existingAccountId));
    }

    public async Task<ConnectOnboardingResult> CompleteAsync(string? accountId, string? code, string redirectUri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(code))
            throw new SparkValidationException("commercePayments.noConnect");
        var tokens = ReadTokens(await api.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = WithoutQuery(redirectUri),
        }, cancellationToken));

        var asSeller = MerchantContext.Connected(Provider, "pending", "pending", accessToken: tokens.AccessToken, testMode: api.Options.TestMode);
        var organization = await api.GetAsync("organizations/me", asSeller, cancellationToken);
        var organizationId = MollieJson.String(organization, "id") ?? throw new InvalidOperationException("Mollie answered no organization id.");
        var profiles = await api.GetAsync("profiles?limit=1", asSeller, cancellationToken);
        var profileId = MollieJson.Embedded(profiles, "profiles").Select(p => MollieJson.String(p, "id")).FirstOrDefault(id => id is not null);
        var onboarding = await api.GetAsync("onboarding/me", asSeller, cancellationToken);
        var account = ReadOnboarding(onboarding, organizationId, MollieJson.String(organization, "name"));
        return new ConnectOnboardingResult(organizationId, account, tokens, profileId);
    }

    public async Task<ConnectedAccountSnapshot> GetAccountAsync(MerchantContext merchant, CancellationToken cancellationToken)
        => ReadOnboarding(await api.GetAsync("onboarding/me", merchant, cancellationToken), merchant.AccountId ?? "", null);

    public async Task<ConnectTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        => ReadTokens(await api.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, cancellationToken), refreshToken);

    /// <summary>The token response: <c>access_token</c>, <c>refresh_token</c>, <c>expires_in</c> (seconds).</summary>
    internal ConnectTokens ReadTokens(JsonElement response, string? previousRefreshToken = null)
    {
        var access = MollieJson.String(response, "access_token") ?? throw new InvalidOperationException("Mollie answered no access token.");
        var expiresIn = response.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 3600;
        return new ConnectTokens(access, MollieJson.String(response, "refresh_token") ?? previousRefreshToken, time.GetUtcNow().AddSeconds(expiresIn));
    }

    /// <summary>The onboarding status as a KYC state: <c>completed</c> and able to receive payments is verified.</summary>
    internal static ConnectedAccountSnapshot ReadOnboarding(JsonElement onboarding, string organizationId, string? name)
    {
        var status = MollieJson.String(onboarding, "status");
        var canPay = MollieJson.Bool(onboarding, "canReceivePayments");
        var canSettle = MollieJson.Bool(onboarding, "canReceiveSettlements");
        return new ConnectedAccountSnapshot
        {
            Provider = MolliePaymentProvider.ProviderName,
            AccountId = organizationId,
            Kyc = status == "completed" && canPay ? KycStatus.Verified : KycStatus.Pending,
            ChargesEnabled = canPay,
            PayoutsEnabled = canSettle,
            Requirements = status == "needs-data" ? ["needs-data"] : [],
            Name = name ?? MollieJson.String(onboarding, "name"),
        };
    }

    private static string WithoutQuery(string url) => url.Split('?', 2)[0];
}

/// <summary>
/// Mollie settlements (GB4; <see href="https://docs.mollie.com/reference/list-settlements">list settlements</see>):
/// each paid-out settlement with its payments, refunds and chargebacks, plus its costs as provider fees. Settlements
/// still open or pending are skipped until they are final.
/// </summary>
internal sealed class MollieSettlementSource(MollieApi api) : ISettlementSource
{
    public string Provider => MolliePaymentProvider.ProviderName;

    public async IAsyncEnumerable<ProviderSettlement> GetSettlementsAsync(MerchantContext merchant, DateTimeOffset since, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The platform's own settlements need an organization token (the Settlements API takes no API key).
        var bearer = merchant.IsConnected ? null : api.Options.OrganizationToken;
        if (!merchant.IsConnected && string.IsNullOrEmpty(bearer))
            yield break;

        // Mollie lists newest first: collect the new ones, then answer oldest first.
        var fresh = new List<JsonElement>();
        string? next = "settlements?limit=250";
        while (next is not null)
        {
            var page = await api.GetAsync(next, merchant, cancellationToken, bearer);
            var reachedOld = false;
            foreach (var settlement in MollieJson.Embedded(page, "settlements"))
            {
                var created = MollieJson.Date(settlement, "createdAt");
                if (created is null || created <= since)
                {
                    reachedOld = true;
                    continue;
                }
                if (MollieJson.String(settlement, "status") == "paidout")
                    fresh.Add(settlement);
            }
            next = reachedOld ? null : MollieJson.Next(page);
        }

        foreach (var settlement in fresh.OrderBy(s => MollieJson.Date(s, "createdAt")))
        {
            var id = MollieJson.String(settlement, "id")!;
            var lines = new List<ProviderSettlementLine>();
            await foreach (var payment in ListAsync($"settlements/{id}/payments", "payments", merchant, bearer, cancellationToken))
                lines.Add(Line(id, SettlementLineKind.Payment, payment, sign: 1));
            await foreach (var refund in ListAsync($"settlements/{id}/refunds", "refunds", merchant, bearer, cancellationToken))
                lines.Add(Line(id, SettlementLineKind.Refund, refund, sign: -1));
            await foreach (var chargeback in ListAsync($"settlements/{id}/chargebacks", "chargebacks", merchant, bearer, cancellationToken))
                lines.Add(Line(id, MollieJson.Date(chargeback, "reversedAt") is null ? SettlementLineKind.Chargeback : SettlementLineKind.ChargebackReversal, chargeback, sign: -1));
            lines.AddRange(Costs(id, settlement));

            var amount = MollieJson.ReadMoney(settlement, "amount") ?? throw new InvalidOperationException($"Settlement {id} has no amount.");
            lines.Add(new ProviderSettlementLine($"{id}:payout", SettlementLineKind.Payout, id, amount, null, amount,
                MollieJson.Date(settlement, "settledAt") ?? MollieJson.Date(settlement, "createdAt")!.Value, "Payout " + MollieJson.String(settlement, "reference")));

            yield return new ProviderSettlement(id, MollieJson.String(settlement, "reference"), MollieJson.Date(settlement, "createdAt")!.Value,
                MollieJson.Date(settlement, "settledAt"), amount, lines);
        }
    }

    private async IAsyncEnumerable<JsonElement> ListAsync(string path, string name, MerchantContext merchant, string? bearer, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? next = path + "?limit=250";
        while (next is not null)
        {
            var page = await api.GetAsync(next, merchant, cancellationToken, bearer);
            foreach (var item in MollieJson.Embedded(page, name))
                yield return item;
            next = MollieJson.Next(page);
        }
    }

    /// <summary>A payment, refund or chargeback of a settlement: gross is its amount (signed), net its <c>settlementAmount</c>.</summary>
    internal static ProviderSettlementLine Line(string settlementId, SettlementLineKind kind, JsonElement item, int sign)
    {
        var id = MollieJson.String(item, "id")!;
        var amount = MollieJson.ReadMoney(item, "amount")!;
        var gross = sign < 0 && amount.ToMinorUnits() > 0 ? Money.FromMinorUnits(-amount.ToMinorUnits(), amount.Currency) : amount;
        var net = MollieJson.ReadMoney(item, "settlementAmount") ?? gross;
        Money? fee = net.Currency == gross.Currency && net.ToMinorUnits() != gross.ToMinorUnits()
            ? Money.FromMinorUnits(gross.ToMinorUnits() - net.ToMinorUnits(), gross.Currency)
            : null;
        // A refund or chargeback names its payment: matching a refund uses its own id, a chargeback its own id.
        return new ProviderSettlementLine($"{settlementId}:{id}", kind, id, gross, fee, net,
            MollieJson.Date(item, "createdAt") ?? DateTimeOffset.MinValue, MollieJson.String(item, "description"));
    }

    /// <summary>The settlement's costs (<c>periods.{year}.{month}.costs[]</c>), one provider-fee line each.</summary>
    internal static IEnumerable<ProviderSettlementLine> Costs(string settlementId, JsonElement settlement)
    {
        var periods = MollieJson.Path(settlement, "periods");
        if (periods.ValueKind != JsonValueKind.Object)
            yield break;
        var index = 0;
        foreach (var year in periods.EnumerateObject())
        {
            foreach (var month in year.Value.EnumerateObject())
            {
                if (!month.Value.TryGetProperty("costs", out var costs) || costs.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var cost in costs.EnumerateArray())
                {
                    var gross = MollieJson.ReadMoney(cost, "amountGross");
                    if (gross is null)
                        continue;
                    var negative = Money.FromMinorUnits(-Math.Abs(gross.ToMinorUnits()), gross.Currency);
                    yield return new ProviderSettlementLine($"{settlementId}:cost:{index++}", SettlementLineKind.ProviderFee, null, negative, null, negative,
                        MollieJson.Date(settlement, "createdAt") ?? DateTimeOffset.MinValue, MollieJson.String(cost, "description"));
                }
            }
        }
    }
}
