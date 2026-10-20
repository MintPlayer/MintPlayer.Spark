namespace MintPlayer.Spark.Commerce.Payments;

/// <summary>
/// Whose provider account a call acts on, with what it needs to reach it (D5). In <see cref="MerchantMode.Platform"/>
/// mode it is the platform's own account (the adapter's configured key). In <see cref="MerchantMode.ConnectedSellers"/>
/// mode it is a seller's linked account: Stripe acts on it with the platform key and a <c>Stripe-Account</c> header;
/// Mollie with the seller's OAuth access token.
/// </summary>
/// <remarks>
/// <see cref="AccessToken"/> is a credential. It is never logged or serialized: <see cref="ToString"/> leaves it out,
/// and the type is not a record, so no generated member prints it.
/// </remarks>
public sealed class MerchantContext
{
    private MerchantContext(string provider, MerchantMode mode, string? sellerOrganizationId, string? accountId, string? profileId, string? accessToken, bool testMode)
    {
        Provider = provider;
        Mode = mode;
        SellerOrganizationId = sellerOrganizationId;
        AccountId = accountId;
        ProfileId = profileId;
        AccessToken = accessToken;
        TestMode = testMode;
    }

    /// <summary>The provider (<c>mollie</c>, <c>stripe</c>, <c>test</c>).</summary>
    public string Provider { get; }

    /// <summary>Platform or a connected seller.</summary>
    public MerchantMode Mode { get; }

    /// <summary>The seller organization; null for the platform's own account.</summary>
    public string? SellerOrganizationId { get; }

    /// <summary>The connected account at the provider: Stripe <c>acct_…</c>, Mollie <c>org_…</c>. Null for the platform.</summary>
    public string? AccountId { get; }

    /// <summary>The seller's website profile at the provider (Mollie <c>pfl_…</c>, required with OAuth). Null when not needed.</summary>
    public string? ProfileId { get; }

    /// <summary>The seller's OAuth access token (Mollie Connect). A credential: never log it.</summary>
    public string? AccessToken { get; }

    /// <summary>Whether calls go to the provider's test mode (Mollie OAuth needs <c>testmode=true</c> in the body).</summary>
    public bool TestMode { get; }

    /// <summary>Whether this is a connected seller's account.</summary>
    public bool IsConnected => Mode == MerchantMode.ConnectedSellers && AccountId is not null;

    /// <summary>The platform's own account at <paramref name="provider"/>.</summary>
    public static MerchantContext Platform(string provider, bool testMode = false)
        => new(provider, MerchantMode.Platform, null, null, null, null, testMode);

    /// <summary>A connected seller's account.</summary>
    public static MerchantContext Connected(string provider, string sellerOrganizationId, string accountId, string? profileId = null, string? accessToken = null, bool testMode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerOrganizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return new(provider, MerchantMode.ConnectedSellers, sellerOrganizationId, accountId, profileId, accessToken, testMode);
    }

    /// <inheritdoc />
    public override string ToString()
        => IsConnected ? $"{Provider}:{SellerOrganizationId}({AccountId})" : $"{Provider}:platform";
}

/// <summary>
/// Finds the merchant context for a provider call that does not start from a seller: a Mollie webhook names only a
/// payment id, a Stripe Connect event only an account id. Implemented by the payments package from its own records,
/// so the payment is always fetched with the credentials it was created with (§4.5).
/// </summary>
public interface IPaymentMerchantResolver
{
    /// <summary>The merchant a payment we created belongs to; null for an id that is not ours.</summary>
    Task<MerchantContext?> ForPaymentAsync(string provider, string providerPaymentId, CancellationToken cancellationToken = default);

    /// <summary>The seller whose connected account is <paramref name="accountId"/>; null when no seller linked it.</summary>
    Task<MerchantContext?> ForAccountAsync(string provider, string accountId, CancellationToken cancellationToken = default);

    /// <summary>The merchant for a seller (the platform in <see cref="MerchantMode.Platform"/> mode or for a null seller).</summary>
    Task<MerchantContext> ForSellerAsync(string provider, string? sellerOrganizationId, CancellationToken cancellationToken = default);
}
