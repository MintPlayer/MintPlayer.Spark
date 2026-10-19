using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce;

/// <summary>
/// Something a buyer can put in a basket: a ticket type's price, a product variant, an add-on (commerce PRD §4.2).
/// App and library entities implement it; Commerce prices it, records its price history and labels its seller.
/// </summary>
/// <remarks>
/// <para><see cref="OrganizationId"/> is the seller. It has the same name as Organizations'
/// <c>IOrganizationOwned.OrganizationId</c> on purpose: an entity that implements both has one property, so the
/// organization row policy (which seller's staff may edit it) and commerce (who sells it) can never disagree.</para>
/// <para>Every sellable item belongs to exactly one channel (D2).</para>
/// </remarks>
public interface ISellableItem
{
    /// <summary>The document id.</summary>
    string? Id { get; }

    /// <summary>The channel name (<c>Spark:Commerce:Channels:{name}</c>).</summary>
    string Channel { get; }

    /// <summary>The selling organization; null when the platform sells in its own name.</summary>
    string? OrganizationId { get; }

    /// <summary>The current unit price before fees, on <see cref="PriceBasis"/>.</summary>
    Money? Price { get; }

    /// <summary>Whether <see cref="Price"/> includes VAT. Default <see cref="Commerce.PriceBasis.Gross"/>.</summary>
    PriceBasis PriceBasis => PriceBasis.Gross;

    /// <summary>The VAT rate in basis points (2100 = 21 %); null when the price carries no VAT breakdown here (the
    /// invoicing package decides the rate later, and a gross price is shown as is).</summary>
    int? VatRateBasisPoints => null;
}

/// <summary>
/// The one definition of the price a buyer sees (round 5, §4.4): <c>FinalPrice = unit price + VAT + every mandatory
/// fee and charge the buyer cannot avoid</c> (service, booking and platform fees; never optional extras, never
/// delivery the buyer chooses). Every displayed price, the face-value resale cap (D30), the Omnibus prior price (GA3)
/// and promotions' "was/now" use it.
/// </summary>
/// <remarks>
/// Legal basis: Price Indication Directive 98/6/EC Art. 2(a) ("final price … including VAT and all other taxes") and
/// the Consumer Rights Directive 2011/83/EU Art. 6(1)(e) ("total price … inclusive of taxes … and all additional …
/// charges"). Registered (singleton) by <c>AddCommerce()</c>; a host may <c>Replace</c> it, but then owns those rules.
/// </remarks>
public interface IFinalPriceCalculator
{
    /// <summary>Computes the final price of <paramref name="request"/>. Pure: no I/O, no clock.</summary>
    FinalPrice Calculate(FinalPriceRequest request);
}

/// <summary>What <see cref="IFinalPriceCalculator"/> prices.</summary>
/// <param name="Channel">The channel whose fee rules apply.</param>
/// <param name="UnitPrice">The unit price before fees.</param>
/// <param name="Basis">Whether <paramref name="UnitPrice"/> includes VAT.</param>
/// <param name="VatRateBasisPoints">The VAT rate (2100 = 21 %); null treats the price as final gross with no breakdown.</param>
/// <param name="Quantity">Units; per-order fees are charged once. Default 1.</param>
/// <param name="ExtraMandatoryCharges">Charges the seller adds on top of the channel's fees that the buyer cannot
/// avoid (a per-sale booking fee), VAT included.</param>
public sealed record FinalPriceRequest(
    string Channel,
    Money UnitPrice,
    PriceBasis Basis = PriceBasis.Gross,
    int? VatRateBasisPoints = null,
    long Quantity = 1,
    IReadOnlyList<PriceComponent>? ExtraMandatoryCharges = null)
{
    /// <summary>A request for one <paramref name="item"/>.</summary>
    public static FinalPriceRequest For(ISellableItem item, long quantity = 1)
        => new(item.Channel,
            item.Price ?? throw new InvalidOperationException($"Sellable item '{item.Id}' has no price."),
            item.PriceBasis, item.VatRateBasisPoints, quantity);
}

/// <summary>One named part of a price: a fee, a charge.</summary>
/// <param name="Code">The permanent code (<c>service</c>).</param>
/// <param name="Kind">What it is for.</param>
/// <param name="Amount">The amount for the whole request (all units), VAT included.</param>
/// <param name="Mandatory">Whether it is part of the <see cref="FinalPrice"/>.</param>
/// <param name="LabelKey">The translation key of its label.</param>
public sealed record PriceComponent(string Code, FeeKind Kind, Money Amount, bool Mandatory, string LabelKey);

/// <summary>The computed final price (§4.4). All amounts are for the whole request (every unit).</summary>
/// <param name="Net">The goods' price without VAT (equal to <paramref name="Gross"/> when no rate is known).</param>
/// <param name="Vat">The VAT on the goods (zero when no rate is known).</param>
/// <param name="Gross">The goods' price with VAT.</param>
/// <param name="MandatoryFees">The fees and charges the buyer cannot avoid, broken down (shown at checkout).</param>
/// <param name="OptionalFees">Fees the buyer may choose; not part of <paramref name="Total"/>.</param>
/// <param name="Total">The final price: <c>Gross + Σ MandatoryFees</c>.</param>
/// <param name="UnitTotal">The final price of one unit, per-order fees included once: the figure a listing shows.</param>
public sealed record FinalPrice(
    Money Net,
    Money Vat,
    Money Gross,
    IReadOnlyList<PriceComponent> MandatoryFees,
    IReadOnlyList<PriceComponent> OptionalFees,
    Money Total,
    Money UnitTotal);

/// <summary>
/// The append-only price history per sellable item (GA3; the money audit of default 8 with the final price before and
/// after). Feeds the Omnibus prior price (PID Art. 6a, inserted by Directive (EU) 2019/2161): the lowest final price of
/// the previous 30 days is shown beside any announced reduction.
/// </summary>
public interface ISparkPriceHistory
{
    /// <summary>
    /// Appends a price change of <paramref name="item"/> to the history, in <paramref name="session"/> (saved with the
    /// change itself). Computes the final price through <see cref="IFinalPriceCalculator"/>. A change that leaves the
    /// final price the same is still recorded (the audit wants every change), flagged unchanged.
    /// </summary>
    /// <param name="session">The session that saves the price change.</param>
    /// <param name="item">The item after the change.</param>
    /// <param name="previous">The final price before the change; null for the first price.</param>
    /// <param name="reason">Why (<c>edit</c>, <c>sale-start</c>, <c>sale-end</c>, <c>import</c>).</param>
    /// <param name="isAnnouncedReduction">Whether the change is a reduction the storefront announces (a sale): then
    /// its prior price is computed and stored with the entry.</param>
    Task<PriceHistoryEntry> RecordAsync(
        Raven.Client.Documents.Session.IAsyncDocumentSession session,
        ISellableItem item,
        Money? previous,
        string reason,
        bool isAnnouncedReduction = false,
        CancellationToken cancellationToken = default);

    /// <summary>The lowest final price of <paramref name="itemId"/> in the window before <paramref name="before"/>
    /// (default the configured 30 days), or null when the item had no price then. Progressive reductions pass the
    /// time of the first step as <paramref name="before"/>.</summary>
    Task<Money?> GetLowestFinalPriceAsync(string itemId, DateTimeOffset before, TimeSpan? window = null, CancellationToken cancellationToken = default);
}

/// <summary>One entry of the price history (GA3). Never edited after it is written.</summary>
/// <param name="ItemId">The sellable item.</param>
/// <param name="At">When the price took effect.</param>
/// <param name="Before">The final unit price before; null for the first.</param>
/// <param name="After">The final unit price after.</param>
/// <param name="UnitPrice">The stored unit price after (before fees).</param>
/// <param name="Reason">Why it changed.</param>
/// <param name="PriorPrice">For an announced reduction: the lowest final price of the window before it (Omnibus).</param>
/// <param name="ChangedBy">The user id that made the change, if any.</param>
public sealed record PriceHistoryEntry(
    string ItemId,
    DateTimeOffset At,
    Money? Before,
    Money After,
    Money UnitPrice,
    string Reason,
    Money? PriorPrice,
    string? ChangedBy);
