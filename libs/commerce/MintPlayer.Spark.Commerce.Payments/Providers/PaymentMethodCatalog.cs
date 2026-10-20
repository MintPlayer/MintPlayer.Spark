namespace MintPlayer.Spark.Commerce.Payments;

/// <summary>
/// One payment method of one provider, as the method catalog states it (§4.5). The values come from the provider's
/// public documentation (research §4.1–§4.5) and may be overridden per method under
/// <c>Spark:Commerce:Payments:Methods:{provider}:{id}</c>.
/// </summary>
/// <param name="Provider">The provider.</param>
/// <param name="Id">The catalog id the suite uses (<c>card</c>, <c>ideal</c>, <c>bancontact</c>, <c>paypal</c>, <c>banktransfer</c>, …).</param>
/// <param name="ProviderMethod">The provider's own name for it (Mollie <c>creditcard</c>; Stripe <c>card</c>).</param>
/// <param name="MaxExpiry">
/// How long a created payment stays payable at the provider, at most (Mollie iDEAL 15 min, card 30 min, Bancontact 1 h,
/// PayPal 6 h, Klarna 48 h, bank transfer 12 days). Null when the provider lets us set the expiry ourselves
/// (<paramref name="ExpiresOnDemand"/>).
/// </param>
/// <param name="ExpiresOnDemand">
/// The payment can be made unpayable at any moment by an API call, with no payment still able to complete afterwards
/// except one already in flight (Stripe <c>POST /v1/checkout/sessions/{id}/expire</c>; Mollie cancel on a cancelable
/// payment). D3 then counts the hold, not the provider's own window.
/// </param>
/// <param name="DelayedConfirmation">
/// A completed payment is confirmed days later (SEPA Direct Debit, bank transfer). Never offered in a held channel: the
/// late payment would arrive long after the hold.
/// </param>
/// <param name="SupportsAuthorization">Authorize-then-capture is possible (cards, PayPal, Klarna; not iDEAL or Bancontact).</param>
/// <param name="Fingerprint">When a payer fingerprint becomes known (D29 linked limits).</param>
/// <param name="SupportsSplit">The method can carry a split across several connected sellers (D40).</param>
/// <param name="Enabled">Whether the host offers it at all.</param>
public sealed record PaymentMethodInfo(
    string Provider,
    string Id,
    string ProviderMethod,
    TimeSpan? MaxExpiry,
    bool ExpiresOnDemand,
    bool DelayedConfirmation,
    bool SupportsAuthorization,
    FingerprintTiming Fingerprint,
    bool SupportsSplit,
    bool Enabled = true);

/// <summary>What a basket asks of the methods it may offer.</summary>
public sealed record PaymentMethodQuery
{
    /// <summary>
    /// The basket holds units while the buyer pays (<see cref="AllocationMode.HoldFromCheckout"/> or
    /// <see cref="AllocationMode.HoldFromSelection"/>), so D3's cap applies.
    /// </summary>
    public bool Held { get; init; }

    /// <summary>The shortest <c>MaxPaymentWindow</c> among the basket's held lines (D3, D40).</summary>
    public TimeSpan? MaxPaymentWindow { get; init; }

    /// <summary>Only methods that can authorize (authorize-then-capture webshop channels).</summary>
    public bool RequireAuthorization { get; init; }

    /// <summary>Only methods that can split across sellers (a mixed-seller basket, D40).</summary>
    public bool RequireSplit { get; init; }

    /// <summary>Methods the channel itself excludes (host configuration).</summary>
    public IReadOnlyCollection<string> Excluded { get; init; } = [];
}

/// <summary>
/// The method filter. Deliberately has no country parameter: no method is hidden because of the card's issuing
/// country, the buyer's residence or nationality (GA11, Regulation (EU) 2018/302 Art. 5(1): within the means of payment
/// a trader accepts, no different conditions for a payment transaction for reasons of the customer's nationality,
/// residence or establishment, the location of the payment account, the payment service provider's establishment or
/// the place of issue of the payment instrument within the Union, for card, credit-transfer and direct-debit payments in
/// an accepted currency). Checked against EUR-Lex 32018R0302 (Art. 5(1)(a)–(c)) on
/// 2026-10-20. Prepaid-card refusal (D29) is decided per method by
/// Checkout, never per country.
/// </summary>
public static class PaymentMethodCatalog
{
    /// <summary>
    /// The methods a basket may offer, in catalog order. In a held basket a method is offered only when the payment
    /// cannot outlive the payment window (D3): its provider expiry fits the window, or it can be expired on demand and
    /// is confirmed at once. Bank transfer (12 days at Mollie) is therefore never offered in a ticketing channel.
    /// </summary>
    public static IReadOnlyList<PaymentMethodInfo> Offerable(IEnumerable<PaymentMethodInfo> methods, PaymentMethodQuery query)
    {
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(query);
        var excluded = new HashSet<string>(query.Excluded, StringComparer.OrdinalIgnoreCase);
        return methods.Where(m => m.Enabled
                && !excluded.Contains(m.Id)
                && (!query.RequireAuthorization || m.SupportsAuthorization)
                && (!query.RequireSplit || m.SupportsSplit)
                && (!query.Held || FitsWindow(m, query.MaxPaymentWindow)))
            .ToList();
    }

    /// <summary>D3: whether a payment with this method cannot be completed after <paramref name="window"/>.</summary>
    public static bool FitsWindow(PaymentMethodInfo method, TimeSpan? window)
    {
        if (method.DelayedConfirmation)
            return false;
        if (method.ExpiresOnDemand)
            return true;
        if (method.MaxExpiry is not { } expiry)
            return false;
        return window is null || expiry <= window.Value;
    }

    /// <summary>
    /// The PaymentPending deadline for a held payment (D3): the method's provider expiry plus <paramref name="grace"/>,
    /// capped by the payment window. A method that expires on demand gets the window itself.
    /// </summary>
    public static TimeSpan PaymentPendingWindow(PaymentMethodInfo method, TimeSpan maxPaymentWindow, TimeSpan grace)
    {
        if (method.ExpiresOnDemand || method.MaxExpiry is null)
            return maxPaymentWindow;
        var extended = method.MaxExpiry.Value + grace;
        return extended < maxPaymentWindow ? extended : maxPaymentWindow;
    }

    /// <summary>The catalog with the host's overrides applied (<c>Spark:Commerce:Payments:Methods</c>).</summary>
    internal static IReadOnlyList<PaymentMethodInfo> WithOverrides(IEnumerable<PaymentMethodInfo> methods, IReadOnlyDictionary<string, PaymentMethodOverride>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
            return methods.ToList();
        return methods.Select(m => overrides.TryGetValue(m.Id, out var o)
                ? m with
                {
                    Enabled = o.Enabled ?? m.Enabled,
                    MaxExpiry = o.MaxExpiry ?? m.MaxExpiry,
                    SupportsSplit = o.SupportsSplit ?? m.SupportsSplit,
                }
                : m)
            .ToList();
    }
}

/// <summary>A host's override of one catalog entry.</summary>
public sealed class PaymentMethodOverride
{
    /// <summary>Offer the method or not.</summary>
    public bool? Enabled { get; set; }

    /// <summary>A shorter (or, after checking the provider's account settings, different) provider expiry.</summary>
    public TimeSpan? MaxExpiry { get; set; }

    /// <summary>The split capability as the host's provider contract allows it (confirm with the provider, D37).</summary>
    public bool? SupportsSplit { get; set; }
}
