using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Commerce;

/// <summary>
/// <c>Spark:Commerce</c> (commerce PRD §4.2): the scenario a host runs, chosen by configuration (K2). Every commerce
/// package reads its part of this one tree, so it lives in the contracts package.
/// </summary>
/// <remarks>
/// <para><b>Precedence: configuration beats code</b> (Moderation's order). <c>AddCommerce(o => …)</c> sets defaults; the
/// bound section is applied after it and wins.</para>
/// <para>Startup validation (<c>MintPlayer.Spark.Commerce</c>) refuses the combinations §4.2 lists and warns on the
/// others; see the guide <c>docs/guide-commerce.md</c>.</para>
/// </remarks>
/// <example>
/// <code>
/// "Spark": { "Commerce": {
///   "Merchant": "ConnectedSellers",
///   "Sellers": { "Registration": "ApprovalRequired" },
///   "Channels": {
///     "tickets": { "Allocation": "HoldFromSelection", "Hold": { "Default": "00:10:00", "Min": "00:05:00", "Max": "00:20:00" }, "RequireSignIn": true },
///     "shop": { "Allocation": "OnPayment" }
///   },
///   "Basket": { "MixChannels": true }
/// } }
/// </code>
/// </example>
public sealed class SparkCommerceOptions : ISparkConfigurationSection
{
    /// <inheritdoc />
    public static string SectionName => "Spark:Commerce";

    /// <summary>Who takes the money (D5). Default <see cref="MerchantMode.Platform"/>.</summary>
    public MerchantMode Merchant { get; set; } = MerchantMode.Platform;

    /// <summary>The ISO 4217 currency prices are kept in when an item does not say. Default <c>EUR</c>.</summary>
    public string DefaultCurrency { get; set; } = "EUR";

    /// <summary>The ISO 3166-1 alpha-2 country of establishment of the platform. It picks the legal floors of the
    /// retention periods (§4.15) and the DSA/DAC7 member state. Default <c>BE</c>.</summary>
    public string Country { get; set; } = "BE";

    /// <summary>The named sales channels (D2). Keys are permanent: every sellable item stores its channel name.</summary>
    public Dictionary<string, CommerceChannelOptions> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The basket rules across channels (D40, GB2).</summary>
    public CommerceBasketOptions Basket { get; set; } = new();

    /// <summary>Seller registration and the marketplace obligations (D21, §4.19).</summary>
    public CommerceSellerOptions Sellers { get; set; } = new();

    /// <summary>Retention per document type (§4.15).</summary>
    public CommerceRetentionOptions Retention { get; set; } = new();

    /// <summary>Price display and the price history (default 4, GA3).</summary>
    public CommercePricingOptions Pricing { get; set; } = new();

    /// <summary>Legal documents (GA2, GA10).</summary>
    public CommerceLegalOptions Legal { get; set; } = new();

    /// <summary>Country-keyed rules on buyers (GA11) that are not specific to one channel. Validated at startup.</summary>
    public List<CountryRuleOptions> CountryRules { get; set; } = [];

    /// <summary>Whether MCP agents may run commerce write actions (§4.15, default 11: no). Default <c>false</c>.</summary>
    public bool AllowMcpWrites { get; set; }

    /// <summary>
    /// Refuses with <see cref="KeyNotFoundException"/> a channel that is not configured, so a typo never falls back to
    /// defaults silently.
    /// </summary>
    public CommerceChannelOptions GetChannel(string name)
        => Channels.TryGetValue(name, out var channel)
            ? channel
            : throw new KeyNotFoundException($"The commerce channel '{name}' is not configured under {SectionName}:Channels.");
}

/// <summary>One named sales channel (<c>Spark:Commerce:Channels:{name}</c>, D2).</summary>
public sealed class CommerceChannelOptions
{
    /// <summary>When units are claimed (D1). Default <see cref="AllocationMode.OnPayment"/>.</summary>
    public AllocationMode Allocation { get; set; } = AllocationMode.OnPayment;

    /// <summary>How long a hold lasts, and the range a seller may pick per sale (D2). Ignored for
    /// <see cref="AllocationMode.OnPayment"/>.</summary>
    public DurationBounds Hold { get; set; } = new() { Default = TimeSpan.FromMinutes(10), Min = TimeSpan.FromMinutes(5), Max = TimeSpan.FromMinutes(20) };

    /// <summary>The longest a hold may be extended while the buyer is at the provider (D3). Methods whose provider
    /// expiry is longer are not offered in this channel. Default 30 minutes.</summary>
    public TimeSpan MaxPaymentWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Whether buyers must sign in (D4). Default <c>false</c>: guests buy with a visitor identity (X1).</summary>
    public bool RequireSignIn { get; set; }

    /// <summary>The most units of one sale a single buyer may hold or buy; null for no limit (D16).</summary>
    public int? PerBuyerLimit { get; set; }

    /// <summary>The waiting-room modes sellers may choose in this channel (D10).</summary>
    public CommerceWaitingRoomOptions WaitingRoom { get; set; } = new();

    /// <summary>The refund policies sellers may choose (D20).</summary>
    public RefundPolicyBounds Refunds { get; set; } = new();

    /// <summary>The fees the channel charges (default 4). Mandatory fees are part of every displayed price.</summary>
    public List<FeeRuleOptions> Fees { get; set; } = [];

    /// <summary>Ticket transfer and face-value resale (D30).</summary>
    public CommerceResaleOptions Resale { get; set; } = new();

    /// <summary>The deceptive bot measures (D29), by measure name. Each enabled measure needs its explicit trigger
    /// setting; startup refuses one without.</summary>
    public Dictionary<string, DeceptiveMeasureOptions> DeceptiveMeasures { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Country-keyed rules applied in this channel (GA11). Validated at startup.</summary>
    public List<CountryRuleOptions> CountryRules { get; set; } = [];

    /// <summary>The legal-document kinds a seller must have published before a sale opens in this channel (GA2).
    /// Default: terms of sale and privacy policy; withdrawal information is added by a goods channel (Shop).</summary>
    public List<string> RequiredLegalDocuments { get; set; } = [LegalDocumentKinds.TermsOfSale, LegalDocumentKinds.PrivacyPolicy];

    /// <summary>Whether the channel sells goods to consumers at a distance (the 14-day withdrawal right applies,
    /// GA1). Ticketing for dated leisure services is exempt (CRD Art. 16(l)). Default <c>false</c>.</summary>
    public bool SellsGoods { get; set; }
}

/// <summary>A default value and the range a seller may pick inside (D2). Stored values are clamped when read, so
/// tightening the configuration takes effect at once.</summary>
public sealed class DurationBounds
{
    /// <summary>The value used when a sale does not choose one.</summary>
    public TimeSpan Default { get; set; }

    /// <summary>The smallest value a seller may choose.</summary>
    public TimeSpan Min { get; set; }

    /// <summary>The largest value a seller may choose.</summary>
    public TimeSpan Max { get; set; }

    /// <summary>The value to use for a seller's <paramref name="chosen"/> one: <see cref="Default"/> when null,
    /// otherwise clamped into [<see cref="Min"/>, <see cref="Max"/>].</summary>
    public TimeSpan Clamp(TimeSpan? chosen)
    {
        var value = chosen ?? Default;
        if (value < Min) return Min;
        if (value > Max) return Max;
        return value;
    }

    /// <summary>Whether <see cref="Min"/> ≤ <see cref="Default"/> ≤ <see cref="Max"/> and none is negative.</summary>
    public bool IsValid => Min >= TimeSpan.Zero && Min <= Default && Default <= Max;
}

/// <summary>An integer default and range (days before an event, …) a seller may pick inside.</summary>
public sealed class IntBounds
{
    /// <summary>The value used when a sale does not choose one.</summary>
    public int Default { get; set; }

    /// <summary>The smallest value.</summary>
    public int Min { get; set; }

    /// <summary>The largest value.</summary>
    public int Max { get; set; } = int.MaxValue;

    /// <summary><see cref="Default"/> when null, otherwise <paramref name="chosen"/> clamped into [Min, Max].</summary>
    public int Clamp(int? chosen) => Math.Clamp(chosen ?? Default, Min, Max);

    /// <summary>Whether Min ≤ Default ≤ Max.</summary>
    public bool IsValid => Min <= Default && Default <= Max;
}

/// <summary>The waiting-room modes a channel allows (D10).</summary>
public sealed class CommerceWaitingRoomOptions
{
    /// <summary>The modes sellers may choose. Empty: the channel has no waiting room.</summary>
    public List<WaitingRoomMode> Modes { get; set; } = [];

    /// <summary>The mode used when a sale does not choose one.</summary>
    public WaitingRoomMode? Default { get; set; }

    /// <summary>Allows <see cref="WaitingRoomMode.RandomizedPreQueue"/> or <see cref="WaitingRoomMode.Ballot"/> in a
    /// channel without <see cref="CommerceChannelOptions.RequireSignIn"/> (one entry per identity is then not
    /// enforced). Default <c>false</c>: startup refuses that combination.</summary>
    public bool AllowWithoutSignIn { get; set; }
}

/// <summary>The refund policies a channel's sellers may choose (D20).</summary>
public sealed class RefundPolicyBounds
{
    /// <summary>The kinds sellers may choose. Default: all three.</summary>
    public List<RefundPolicyKind> Allowed { get; set; } = [RefundPolicyKind.None, RefundPolicyKind.UntilDaysBefore, RefundPolicyKind.WithFee];

    /// <summary>The range of "until X days before".</summary>
    public IntBounds DaysBefore { get; set; } = new() { Default = 7, Min = 0, Max = 365 };

    /// <summary>The highest cancellation fee, in basis points of the price (1000 = 10 %).</summary>
    public int MaxFeeBasisPoints { get; set; } = 1000;

    /// <summary>Whether service and platform fees are refunded with a cancellation (§4.7). Default <c>false</c>.
    /// A withdrawal (GA1) ignores this for the delivery cost.</summary>
    public bool RefundFees { get; set; }
}

/// <summary>One fee rule of a channel (default 4).</summary>
public sealed class FeeRuleOptions
{
    /// <summary>A permanent code, unique in the channel (<c>service</c>); stored on order lines and in the fee ledger.</summary>
    public string Code { get; set; } = "";

    /// <summary>What the fee is for.</summary>
    public FeeKind Kind { get; set; } = FeeKind.Service;

    /// <summary>Whether the buyer cannot avoid it. Mandatory fees are part of every displayed price
    /// (<see cref="FinalPrice"/>); optional ones are offered at checkout. Default <c>true</c>.</summary>
    public bool Mandatory { get; set; } = true;

    /// <summary>Per unit or per order. Default per unit.</summary>
    public FeeScope Scope { get; set; } = FeeScope.PerUnit;

    /// <summary>A fixed amount per currency (<c>{"EUR": 1.50}</c>), VAT included. A currency without an entry
    /// charges no fixed part.</summary>
    public Dictionary<string, decimal> Fixed { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A percentage of the gross unit price, in basis points (250 = 2.5 %), rounded once per unit.</summary>
    public int PercentBasisPoints { get; set; }

    /// <summary>The translation key of the label shown to the buyer; null uses <c>commerce.fee.{Kind}</c>.</summary>
    public string? LabelKey { get; set; }
}

/// <summary>Ticket transfer and face-value resale switches (D30).</summary>
public sealed class CommerceResaleOptions
{
    /// <summary>Whether tickets may be transferred to another holder.</summary>
    public bool Transfer { get; set; }

    /// <summary>Whether the official face-value resale exchange is open. The cap is the original <see cref="FinalPrice"/>.</summary>
    public bool FaceValueResale { get; set; }
}

/// <summary>One deceptive bot measure's switch (D29).</summary>
public sealed class DeceptiveMeasureOptions
{
    /// <summary>Whether the measure is on.</summary>
    public bool Enabled { get; set; }

    /// <summary>The risk signal or threshold that triggers it. Required when <see cref="Enabled"/>: a deceptive
    /// measure never runs against every buyer.</summary>
    public string? Trigger { get; set; }
}

/// <summary>A country-keyed rule on buyers (GA11). Startup refuses one keyed on nationality, residence, IP country or
/// card-issuing country unless it only routes delivery.</summary>
public sealed class CountryRuleOptions
{
    /// <summary>A name for messages.</summary>
    public string Name { get; set; } = "";

    /// <summary>The buyer attribute the rule reads.</summary>
    public CountryKey Key { get; set; }

    /// <summary>The ISO 3166-1 alpha-2 countries it matches.</summary>
    public List<string> Countries { get; set; } = [];

    /// <summary>What it does to a matching buyer.</summary>
    public CountryRuleEffect Effect { get; set; }
}

/// <summary>Basket rules across channels and sellers (D40, GB2).</summary>
public sealed class CommerceBasketOptions
{
    /// <summary>Whether a basket may hold lines of several channels (one order, one payment). Default <c>false</c>.</summary>
    public bool MixChannels { get; set; }

    /// <summary>Whether a basket may hold lines of several sellers in <see cref="MerchantMode.ConnectedSellers"/> mode.
    /// Refused at startup unless every registered payment provider reports split capability. Default <c>false</c>.</summary>
    public bool MixSellers { get; set; }

    /// <summary>What a failed <see cref="AllocationMode.OnPayment"/> line does to the order.</summary>
    public BasketLineFailurePolicy OnLineFailure { get; set; } = BasketLineFailurePolicy.RefundLine;
}

/// <summary>Seller registration and marketplace obligations (D21, §4.19).</summary>
public sealed class CommerceSellerOptions
{
    /// <summary>How organizations become sellers. Default <see cref="SellerRegistrationMode.Closed"/>.</summary>
    public SellerRegistrationMode Registration { get; set; } = SellerRegistrationMode.Closed;

    /// <summary>Allows <see cref="SellerRegistrationMode.Open"/> with <see cref="MerchantMode.Platform"/> (D21).</summary>
    public bool AcknowledgeOpenPlatformMerchant { get; set; }

    /// <summary>Trader traceability (DSA Art. 30). Default <c>true</c>. Switching it off needs
    /// <see cref="AcknowledgeDsaArt29Exemption"/>.</summary>
    public bool TraderTraceability { get; set; } = true;

    /// <summary>The host states it qualifies for the micro/small enterprise exemption of DSA Art. 29 (contested; the
    /// host's counsel decides). Required to switch <see cref="TraderTraceability"/> off.</summary>
    public bool AcknowledgeDsaArt29Exemption { get; set; }

    /// <summary>Seller complaint handling (P2B Reg. (EU) 2019/1150 Art. 11). Default <c>true</c>. Switching it off needs
    /// <see cref="AcknowledgeP2BSmallEnterprise"/> (Art. 11(5) exempts small enterprises).</summary>
    public bool ComplaintHandling { get; set; } = true;

    /// <summary>The host states it is a small enterprise under Recommendation 2003/361/EC (P2B Art. 11(5)).</summary>
    public bool AcknowledgeP2BSmallEnterprise { get; set; }

    /// <summary>How long a seller complaint may stay undecided before it expires. Default 30 days.</summary>
    public TimeSpan ComplaintDeadline { get; set; } = TimeSpan.FromDays(30);

    /// <summary>DAC7 seller data collection (Directive (EU) 2021/514). Default on in connected mode.</summary>
    public CommerceDac7Options Dac7 { get; set; } = new();

    /// <summary>The organization role that may export buyers and attendees of the seller's own sales (GB14), beside
    /// <c>finance</c>. Default <c>seller-admin</c>.</summary>
    public string ExportRole { get; set; } = "seller-admin";
}

/// <summary>DAC7 seller data (GA6).</summary>
public sealed class CommerceDac7Options
{
    /// <summary>Whether DAC7 data is collected. Default <c>true</c>; only meaningful in connected mode.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The interval between reminders for missing data. Default 14 days.</summary>
    public TimeSpan ReminderInterval { get; set; } = TimeSpan.FromDays(14);

    /// <summary>The reminders sent before payouts are blocked. Default 2: Directive (EU) 2021/514 makes the platform
    /// close the account or withhold the consideration once a seller has not answered two reminders after the first
    /// request (Annex V, effective-implementation rules; the minimum 60-day wait is <see cref="MinimumWaitBeforeBlock"/>).
    /// Confirm the paragraph and the national transposition (D37).</summary>
    public int RemindersBeforeBlock { get; set; } = 2;

    /// <summary>The shortest time between the first request and the payout block. Default 60 days (confirm, D37).</summary>
    public TimeSpan MinimumWaitBeforeBlock { get; set; } = TimeSpan.FromDays(60);
}

/// <summary>Retention per document type (§4.15). Periods below the legal floor are refused at startup.</summary>
public sealed class CommerceRetentionOptions
{
    /// <summary>The period per document type (<see cref="CommerceDocumentTypes"/>), counted from the type's own start
    /// (issue, end of the relationship, …). A type without an entry uses its floor.</summary>
    public Dictionary<string, TimeSpan> Periods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lower floors the host's counsel confirmed for its country, overriding the built-in ones per type. Each
    /// override is logged at startup.</summary>
    public Dictionary<string, TimeSpan> ConfirmedFloors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Price display and the price history (default 4, GA3).</summary>
public sealed class CommercePricingOptions
{
    /// <summary>The window of the Omnibus prior price (PID Art. 6a(2): "not shorter than 30 days"). Default 30 days.</summary>
    public TimeSpan PriorPriceWindow { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>Legal documents (GA2, GA10).</summary>
public sealed class CommerceLegalOptions
{
    /// <summary>The URL of the host's accessibility statement (European Accessibility Act, Directive (EU) 2019/882
    /// Annex V). When empty and no published <see cref="LegalDocumentKinds.AccessibilityStatement"/> exists, startup
    /// warns (GA10).</summary>
    public string? AccessibilityStatementUrl { get; set; }
}
