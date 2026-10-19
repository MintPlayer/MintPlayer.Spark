namespace MintPlayer.Spark.Commerce;

/// <summary>
/// When a sale claims the units it sells (commerce PRD D1). Chosen per channel (D2); one engine serves all three.
/// </summary>
public enum AllocationMode
{
    /// <summary>Nothing is claimed until the payment completes; a buyer who loses the race at confirmation is voided
    /// or refunded automatically. The webshop default.</summary>
    OnPayment,

    /// <summary>The units are claimed when the buyer presses "Pay" and held while the buyer is at the provider.</summary>
    HoldFromCheckout,

    /// <summary>The units are claimed when the quantity is chosen and held for the channel's hold time (ticketing).</summary>
    HoldFromSelection,
}

/// <summary>Who takes the buyer's money (D5).</summary>
public enum MerchantMode
{
    /// <summary>The platform sells in its own name and is the merchant of record.</summary>
    Platform,

    /// <summary>Each seller links its own provider account (Connect); the platform never holds third-party funds and
    /// may take an application fee.</summary>
    ConnectedSellers,
}

/// <summary>How an organization becomes a seller (D21).</summary>
public enum SellerRegistrationMode
{
    /// <summary>Only the platform host creates seller profiles.</summary>
    Closed,

    /// <summary>Anyone may apply; a platform approver decides through Approvals (M3).</summary>
    ApprovalRequired,

    /// <summary>Anyone may register and sell once the provider's KYC passes. Refused at startup with
    /// <see cref="MerchantMode.Platform"/> unless acknowledged (the platform would sell for strangers in its own name).</summary>
    Open,
}

/// <summary>A waiting-room admission mode (D10). The modes themselves live in MintPlayer.Spark.WaitingRoom.</summary>
public enum WaitingRoomMode
{
    /// <summary>Everyone who arrives before the start is shuffled once, then admitted in that order. Needs sign-in
    /// (one entry per identity) unless overridden.</summary>
    RandomizedPreQueue,

    /// <summary>Registration window, then a draw. Needs sign-in unless overridden.</summary>
    Ballot,

    /// <summary>First come, first served.</summary>
    Fifo,
}

/// <summary>What happens to a multi-channel order when an <see cref="AllocationMode.OnPayment"/> line loses the race
/// at confirmation (D40, <c>Basket:OnLineFailure</c>).</summary>
public enum BasketLineFailurePolicy
{
    /// <summary>The rest of the order is confirmed; the failed line is refunded with a credit note. Default.</summary>
    RefundLine,

    /// <summary>The whole order is voided or refunded.</summary>
    VoidOrder,
}

/// <summary>Whether a stored price includes VAT.</summary>
public enum PriceBasis
{
    /// <summary>The price includes VAT (consumer prices). Default.</summary>
    Gross,

    /// <summary>The price excludes VAT (B2B, Peppol default per spike S21).</summary>
    Net,
}

/// <summary>What a fee is for (default 4, round 5 <c>FinalPrice</c>). Only <see cref="FeeRuleOptions.Mandatory"/> fees
/// enter the <see cref="FinalPrice"/>; delivery is never a fee here (the buyer chooses the method).</summary>
public enum FeeKind
{
    /// <summary>A service fee per ticket or item.</summary>
    Service,

    /// <summary>A booking fee, usually per order.</summary>
    Booking,

    /// <summary>The platform's fee shown to the buyer.</summary>
    Platform,

    /// <summary>Anything else the buyer is charged, such as a payment surcharge where the law allows one.</summary>
    Other,
}

/// <summary>Whether a fee is charged per unit or once per order.</summary>
public enum FeeScope
{
    /// <summary>Charged on every unit (a ticket's service fee).</summary>
    PerUnit,

    /// <summary>Charged once per order of the channel (a booking fee).</summary>
    PerOrder,
}

/// <summary>The kinds of seller refund policy a channel allows (D20). Withdrawal from goods (GA1) is never governed
/// by these: it is unconditional.</summary>
public enum RefundPolicyKind
{
    /// <summary>No cancellation by the buyer.</summary>
    None,

    /// <summary>Cancellation allowed until a number of days before the event or date.</summary>
    UntilDaysBefore,

    /// <summary>Cancellation allowed against a fee.</summary>
    WithFee,
}

/// <summary>Whether a seller acts as a trader (CRD Art. 6a(1)(b), as inserted by Directive (EU) 2019/2161). Shown on
/// every listing (GA5): consumer-protection law does not apply to a consumer-to-consumer sale.</summary>
public enum TraderStatus
{
    /// <summary>The seller has not declared it yet; such a seller cannot open a sale.</summary>
    Undeclared,

    /// <summary>A trader (a business). Consumer-protection rules apply.</summary>
    Trader,

    /// <summary>A private individual. The listing says that EU consumer-protection rules do not apply.</summary>
    NonTrader,
}

/// <summary>The provider's verification state of a connected seller (D21). Reported by the payments package through
/// <see cref="ISellerKycSource"/>.</summary>
public enum KycStatus
{
    /// <summary>No provider account is linked.</summary>
    NotStarted,

    /// <summary>Linked, verification in progress or more information requested.</summary>
    Pending,

    /// <summary>The provider verified the seller; charges and payouts are enabled.</summary>
    Verified,

    /// <summary>The provider rejected or disabled the account.</summary>
    Rejected,
}

/// <summary>A buyer attribute a country-keyed rule may look at (GA11, Regulation (EU) 2018/302).</summary>
public enum CountryKey
{
    /// <summary>The delivery address. Delivery zones keyed on it are allowed (Art. 4(1)(a) leaves where a trader delivers to the trader).</summary>
    DeliveryAddress,

    /// <summary>The buyer's nationality. Discrimination on it is forbidden (Art. 4).</summary>
    Nationality,

    /// <summary>The buyer's place of residence. Discrimination on it is forbidden (Art. 3, 4).</summary>
    Residence,

    /// <summary>The issuing country of the card. Refusing or surcharging on it is forbidden (Art. 5(1)(c)).</summary>
    CardIssuingCountry,

    /// <summary>The country of the buyer's IP address (geo-location). Blocking or redirecting on it is forbidden (Art. 3).</summary>
    IpCountry,
}

/// <summary>What a country-keyed rule does to a buyer it matches (GA11).</summary>
public enum CountryRuleEffect
{
    /// <summary>The buyer is refused (blocked, or a method is hidden).</summary>
    Refuse,

    /// <summary>The buyer gets other prices, fees or conditions.</summary>
    DifferentConditions,

    /// <summary>The buyer is redirected to another storefront.</summary>
    Redirect,

    /// <summary>A risk signal: the buyer is challenged or scored, not refused outright.</summary>
    RiskSignal,
}
