namespace MintPlayer.Spark.Commerce.Checkout.Refunds;

/// <summary>
/// <c>Spark:Commerce:Checkout:Refunds</c> (commerce PRD §4.7, D20, GA1, GA8). The seller refund-policy bounds and the
/// "refund fees" switch stay on the channel (<c>Spark:Commerce:Channels:{name}:Refunds</c>, D2); this section holds what
/// only the refund flows read.
/// </summary>
public sealed class CheckoutRefundOptions
{
    /// <summary>
    /// The IANA zone in which withdrawal and guarantee periods are counted in days (Regulation (EEC, Euratom) No 1182/71,
    /// applied to the CRD by its recital 41). Default <c>UTC</c>; a Belgian shop sets <c>Europe/Brussels</c>.
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    public CheckoutWithdrawalOptions Withdrawal { get; set; } = new();

    public CheckoutGuaranteeOptions Guarantee { get; set; } = new();

    public CheckoutMassRefundOptions MassRefunds { get; set; } = new();

    /// <summary>The withdrawal receipt mail (GA1, CRD Art. 11a: acknowledgement on a durable medium with date and time).</summary>
    public string WithdrawalReceiptMailTemplate { get; set; } = "SparkCheckout/WithdrawalReceipt";

    /// <summary>The keep-or-refund notice sent when an event is rescheduled (D20).</summary>
    public string RescheduleNoticeMailTemplate { get; set; } = "SparkCheckout/RescheduleNotice";

    /// <summary>The refund confirmation mailed to the buyer once a refund is paid out.</summary>
    public string RefundIssuedMailTemplate { get; set; } = "SparkCheckout/RefundIssued";

    /// <summary>The page of the order a signed guest link opens: app-relative, <c>{number}</c> and <c>{token}</c> are replaced.</summary>
    public string GuestOrderPagePath { get; set; } = "/checkout/orders/{number}?access={token}";

    /// <summary>The refund sweep (re-delivers <c>RefundIssued</c>, flags refunds past their due date). Six fields. Default every minute.</summary>
    public string SweepSchedule { get; set; } = "0 * * * * *";
}

/// <summary>The statutory withdrawal from goods (GA1; Directive 2011/83/EU Art. 9–16).</summary>
public sealed class CheckoutWithdrawalOptions
{
    /// <summary>The withdrawal period in days, counted from the day after the buyer received the (last) goods (CRD Art. 9(1), 9(2)(b)). Never below 14.</summary>
    public int PeriodDays { get; set; } = 14;

    /// <summary>How many days the trader has to refund, counted from the withdrawal (CRD Art. 13(1)). Default 14.</summary>
    public int RefundWithinDays { get; set; } = 14;

    /// <summary>
    /// Whether the seller bears the cost of returning withdrawn goods (CRD Art. 14(1): the buyer bears it unless the trader
    /// agreed to bear it or failed to inform). Shown in the withdrawal information. Default <c>false</c>.
    /// </summary>
    public bool SellerPaysReturnShipping { get; set; }

    /// <summary>
    /// How a partial withdrawal treats the standard delivery cost. <see cref="PartialDeliveryRefund.WhenComplete"/> (default)
    /// refunds it with the withdrawal that leaves no goods line of the order kept; <see cref="PartialDeliveryRefund.ProRata"/>
    /// refunds it in proportion. ⚠️ CRD Art. 13 does not settle partial withdrawals; confirm against the Commission's CRD
    /// guidance (2021/C 525/01) and national law (D37).
    /// </summary>
    public PartialDeliveryRefund PartialDelivery { get; set; } = PartialDeliveryRefund.WhenComplete;

    /// <summary>
    /// Whether a line that is not physical goods and has no service date (a non-dated service, digital content) carries
    /// the withdrawal right. Default <c>false</c>: Checkout cannot know whether CRD Art. 16(a) or (m) applies; a
    /// <see cref="IRefundTermsSource"/> decides per item.
    /// </summary>
    public bool ServicesWithoutDate { get; set; }
}

/// <summary>How a partial withdrawal refunds the delivery cost.</summary>
public enum PartialDeliveryRefund
{
    WhenComplete,
    ProRata,
}

/// <summary>The legal guarantee of conformity (GA8; Directive (EU) 2019/771).</summary>
public sealed class CheckoutGuaranteeOptions
{
    /// <summary>
    /// The guarantee period from delivery (Art. 10(1): at least two years; member states may set a longer one, Art. 10(3)).
    /// Default 730 days. ⚠️ Set per deployment for the buyers' member state; confirm per country (D37).
    /// </summary>
    public int PeriodDays { get; set; } = 730;

    /// <summary>Per channel overrides of <see cref="PeriodDays"/> (second-hand goods may have a shorter period, Art. 10(6)).</summary>
    public Dictionary<string, int> ChannelPeriodDays { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Mass refunds (D20: batched, resumable, idempotent, rate limited).</summary>
public sealed class CheckoutMassRefundOptions
{
    /// <summary>The worker's schedule (six fields). Default every 10 seconds. Read at registration.</summary>
    public string Schedule { get; set; } = "*/10 * * * * *";

    /// <summary>Orders one run handles at most. Default 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Provider refund calls per second at most (the provider's rate limit). 0: unlimited. Default 5.</summary>
    public double MaxRefundsPerSecond { get; set; } = 5;

    /// <summary>Order ids per stored chunk of a mass refund's selection. Default 500.</summary>
    public int ChunkSize { get; set; } = 500;
}
