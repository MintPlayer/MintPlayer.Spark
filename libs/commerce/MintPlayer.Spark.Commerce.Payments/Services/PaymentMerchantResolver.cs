using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Payments.Services;

/// <summary>
/// Merchant contexts from our own records (§4.5): a payment remembers the seller and account it was created on, a seller
/// account its tokens. Mollie access tokens (one hour) are renewed here, with the refresh token, and stored again
/// encrypted.
/// </summary>
internal sealed class PaymentMerchantResolver(
    IDocumentStore store,
    PaymentProviderRegistry registry,
    ConnectTokenProtector tokens,
    IOptions<SparkCommerceOptions> commerce,
    TimeProvider time,
    ILogger<PaymentMerchantResolver> logger) : IPaymentMerchantResolver
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    public async Task<MerchantContext?> ForPaymentAsync(string provider, string providerPaymentId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var payment = await session.LoadAsync<ProviderPayment>(ProviderPayment.IdFor(provider, providerPaymentId), cancellationToken);
        if (payment is null)
        {
            // Stripe events name the PaymentIntent or charge; we store the Checkout Session id and keep these as related ids.
            payment = await session.Query<ProviderPayment>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
                .Where(p => p.Provider == provider && p.RelatedIds.Any(r => r == providerPaymentId))
                .FirstOrDefaultAsync(cancellationToken);
        }
        if (payment is null)
            return null;
        if (payment.SellerOrganizationId is null || payment.AccountId is null)
            return MerchantContext.Platform(provider);
        return await ForSellerCoreAsync(session, provider, payment.SellerOrganizationId, cancellationToken);
    }

    public async Task<MerchantContext?> ForAccountAsync(string provider, string accountId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var account = await session.Query<SellerPaymentAccount>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
            .Where(a => a.Provider == provider && a.AccountId == accountId)
            .FirstOrDefaultAsync(cancellationToken);
        return account is null ? null : await ForSellerCoreAsync(session, provider, account.SellerOrganizationId, cancellationToken);
    }

    public async Task<MerchantContext> ForSellerAsync(string provider, string? sellerOrganizationId, CancellationToken cancellationToken = default)
    {
        if (commerce.Value.Merchant == MerchantMode.Platform || sellerOrganizationId is null)
            return MerchantContext.Platform(provider);
        using var session = store.OpenAsyncSession();
        return await ForSellerCoreAsync(session, provider, sellerOrganizationId, cancellationToken)
            ?? throw new SparkValidationException("commercePayments.sellerNotLinked");
    }

    private async Task<MerchantContext?> ForSellerCoreAsync(IAsyncDocumentSession session, string provider, string sellerOrganizationId, CancellationToken cancellationToken)
    {
        var account = await session.LoadAsync<SellerPaymentAccount>(SellerPaymentAccount.IdFor(provider, sellerOrganizationId), cancellationToken);
        if (account?.AccountId is not { Length: > 0 } accountId)
            return null;

        var current = tokens.Unprotect(account.ProtectedTokens);
        if (current is not null && current.RefreshToken is { } refreshToken && current.ExpiresAt - RefreshMargin <= time.GetUtcNow())
        {
            if (registry.Onboarding(provider) is { } onboarding && await onboarding.RefreshAsync(refreshToken, cancellationToken) is { } renewed)
            {
                current = renewed;
                account.ProtectedTokens = tokens.Protect(renewed);
                account.AccessTokenExpiresAtUtc = renewed.ExpiresAt.UtcDateTime;
                account.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
                await session.SaveChangesAsync(cancellationToken);
                logger.LogDebug("Renewed the {Provider} access token of seller {Seller}.", provider, sellerOrganizationId);
            }
        }

        return MerchantContext.Connected(provider, sellerOrganizationId, accountId, account.ProfileId, current?.AccessToken);
    }
}
