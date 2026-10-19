using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Commerce.Payments.Models;
using MintPlayer.Spark.Organizations;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Payments.Services;

/// <summary>
/// Connect onboarding (D5, D21, spike S6): starts the provider's flow for a seller, completes the redirect back, and
/// keeps the seller's verification state current (webhooks where the provider sends them, Stripe <c>account.updated</c>;
/// a poll where it does not, Mollie's onboarding status).
/// </summary>
internal sealed class SellerOnboarding(
    IDocumentStore store,
    PaymentProviderRegistry registry,
    ConnectTokenProtector tokens,
    PaymentUrls urls,
    PaymentEventProcessor processor,
    IPaymentMerchantResolver merchants,
    ISparkOrganizations organizations,
    IOptionsMonitor<SparkCommercePaymentsOptions> options,
    IOptions<SparkCommerceOptions> commerce,
    TimeProvider time,
    ILogger<SellerOnboarding> logger)
{
    /// <summary>The callback route the provider returns to.</summary>
    public const string CallbackPath = "/spark/commerce/payments/connect/callback";

    private const string DeniedResource = "ConnectOnboarding/SellerPaymentAccount";

    /// <summary>
    /// Starts onboarding for <paramref name="sellerOrganizationId"/>; answers the provider URL. Only an Owner or Admin of
    /// the seller may link its account.
    /// </summary>
    public async Task<string> StartAsync(string? provider, string sellerOrganizationId, string actingUserId, CancellationToken cancellationToken)
    {
        if (commerce.Value.Merchant != MerchantMode.ConnectedSellers)
            throw new SparkValidationException("commercePayments.notConnectedMode");
        if (await organizations.GetRoleAsync(sellerOrganizationId, actingUserId, cancellationToken) is not (OrganizationRole.Admin or OrganizationRole.Owner))
            throw new SparkAccessDeniedException(DeniedResource);

        var name = registry.Get(provider).Name;
        var onboarding = registry.Onboarding(name) ?? throw new SparkValidationException("commercePayments.noConnect");

        using var session = store.OpenAsyncSession();
        var id = SellerPaymentAccount.IdFor(name, sellerOrganizationId);
        var account = await session.LoadAsync<SellerPaymentAccount>(id, cancellationToken);
        if (account is null)
        {
            account = new SellerPaymentAccount { Id = id, Provider = name, SellerOrganizationId = sellerOrganizationId };
            await session.StoreAsync(account, id, cancellationToken);
        }

        // The state binds the callback to this seller and this start: 32 random bytes, stored only as a hash.
        var state = $"{name}.{Base64Url(RandomNumberGenerator.GetBytes(32))}";
        account.PendingStateHash = Hash(state);
        account.PendingStateExpiresAtUtc = (time.GetUtcNow() + options.CurrentValue.Connect.StateLifetime).UtcDateTime;
        account.LinkedBy = actingUserId;
        account.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;

        var callback = urls.Absolute(CallbackPath);
        var start = await onboarding.StartAsync(sellerOrganizationId, account.AccountId, $"{callback}?state={Uri.EscapeDataString(state)}", $"{callback}?state={Uri.EscapeDataString(state)}&refresh=1", state, cancellationToken);
        if (start.AccountId is { } created && account.AccountId is null)
        {
            account.AccountId = created;
            account.Kyc = KycStatus.Pending;
        }
        await session.SaveChangesAsync(cancellationToken);
        return start.RedirectUrl;
    }

    /// <summary>
    /// Completes the redirect: checks the state (single use, unexpired, and the caller still an Admin of the seller),
    /// exchanges the code where the provider uses OAuth, stores the tokens encrypted, and records the account state.
    /// Answers the app-relative page to land on.
    /// </summary>
    public async Task<string> CompleteAsync(string state, string? code, bool refresh, string actingUserId, CancellationToken cancellationToken)
    {
        var dot = state.IndexOf('.');
        if (dot <= 0)
            throw new SparkAccessDeniedException(DeniedResource);
        var name = state[..dot];
        var onboarding = registry.Onboarding(name) ?? throw new SparkAccessDeniedException(DeniedResource);

        using var session = store.OpenAsyncSession();
        var hash = Hash(state);
        var account = await session.Query<SellerPaymentAccount>()
            .Where(a => a.Provider == name && a.PendingStateHash == hash)
            .FirstOrDefaultAsync(cancellationToken);
        if (account is null || account.PendingStateExpiresAtUtc < time.GetUtcNow().UtcDateTime)
            throw new SparkAccessDeniedException(DeniedResource);
        if (await organizations.GetRoleAsync(account.SellerOrganizationId, actingUserId, cancellationToken) is not (OrganizationRole.Admin or OrganizationRole.Owner))
            throw new SparkAccessDeniedException(DeniedResource);

        if (refresh)
        {
            // Stripe's Account Link expired before the seller finished: start again with a fresh link.
            await session.SaveChangesAsync(cancellationToken);
            return await StartAsync(name, account.SellerOrganizationId, actingUserId, cancellationToken);
        }

        account.PendingStateHash = null;
        account.PendingStateExpiresAtUtc = null;
        var result = await onboarding.CompleteAsync(account.AccountId, code, urls.Absolute(CallbackPath) + "?state=" + Uri.EscapeDataString(state), cancellationToken);
        account.AccountId = result.AccountId;
        account.ProfileId = result.ProfileId ?? account.ProfileId;
        if (result.Tokens is { } connectTokens)
        {
            account.ProtectedTokens = tokens.Protect(connectTokens);
            account.AccessTokenExpiresAtUtc = connectTokens.ExpiresAt.UtcDateTime;
        }
        account.LinkedAtUtc ??= time.GetUtcNow().UtcDateTime;
        var previous = await processor.ApplyAccountAsync(account, result.Account);
        await session.SaveChangesAsync(cancellationToken);
        if (previous != account.Kyc)
            await processor.AnnounceAccountAsync(account, previous, cancellationToken);
        return options.CurrentValue.Connect.ReturnPath;
    }

    /// <summary>Re-reads every linked account that is not verified yet (the KYC refresh job).</summary>
    public async Task<int> RefreshPendingAsync(CancellationToken cancellationToken)
    {
        List<SellerPaymentAccount> pending;
        using (var session = store.OpenAsyncSession())
            pending = await session.Query<SellerPaymentAccount>()
                .Where(a => a.AccountId != null && a.Kyc != KycStatus.Verified && a.Kyc != KycStatus.Rejected)
                .Take(500)
                .ToListAsync(cancellationToken);

        var refreshed = 0;
        foreach (var stale in pending)
        {
            if (registry.Onboarding(stale.Provider) is not { } onboarding)
                continue;
            try
            {
                var merchant = await merchants.ForSellerAsync(stale.Provider, stale.SellerOrganizationId, cancellationToken);
                var snapshot = await onboarding.GetAccountAsync(merchant, cancellationToken);
                await processor.ProcessAsync([new ConnectedAccountChangedEvent(stale.Provider, $"poll:{stale.AccountId}:{snapshot.Kyc}", time.GetUtcNow(), snapshot)], cancellationToken);
                refreshed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not refresh the {Provider} account of seller {Seller}.", stale.Provider, stale.SellerOrganizationId);
            }
        }
        return refreshed;
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The KYC gate hook (Commerce's <see cref="ISellerKycSource"/>, D21): a seller's verification at a provider, as the
/// provider last reported it to us.
/// </summary>
internal sealed class PaymentsKycSource(IDocumentStore store, PaymentProviderRegistry registry) : ISellerKycSource
{
    public string Provider => registry.DefaultName;

    public async Task<KycStatus> GetKycStatusAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var account = await session.LoadAsync<SellerPaymentAccount>(SellerPaymentAccount.IdFor(Provider, organizationId), cancellationToken);
        return account switch
        {
            null or { AccountId: null } => KycStatus.NotStarted,
            { Kyc: KycStatus.Verified, ChargesEnabled: false } => KycStatus.Pending,
            _ => account.Kyc,
        };
    }
}
