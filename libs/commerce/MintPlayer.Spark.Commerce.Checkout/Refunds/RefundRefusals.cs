namespace MintPlayer.Spark.Commerce.Checkout.Refunds;

/// <summary>
/// The refund flows' refusal codes, answered like <see cref="CheckoutRefusals"/> (HTTP 409, <c>SparkCheckoutRefusal</c>)
/// and translated as <c>commerceCheckout.refusal.{code}</c>. Permanent.
/// </summary>
public static class RefundRefusals
{
    /// <summary>D20: the seller's policy allows no cancellation.</summary>
    public const string NoCancellation = "noCancellation";
    /// <summary>D20: the policy's "until X days before" has passed.</summary>
    public const string CancellationClosed = "cancellationClosed";
    /// <summary>The service date passed: nothing is refundable by policy.</summary>
    public const string ServicePassed = "servicePassed";
    /// <summary>A deduction larger than the line's share.</summary>
    public const string DeductionTooHigh = "deductionTooHigh";
    /// <summary>An amount larger than what is left to refund.</summary>
    public const string AmountTooHigh = "amountTooHigh";
    /// <summary>GA1: no line of the order carries a live withdrawal right (shown before confirming; never an approval).</summary>
    public const string NoWithdrawalRight = "noWithdrawalRight";
    /// <summary>A line is already being withdrawn, claimed or refunded.</summary>
    public const string NothingLeft = "nothingLeft";
    /// <summary>GA8: price reduction or termination needs an Art. 13(4) ground.</summary>
    public const string GroundRequired = "groundRequired";
    /// <summary>A reschedule window that is closed or does not cover the order.</summary>
    public const string WindowClosed = "windowClosed";
    /// <summary>The refund is not in a state that allows it (a retry of a paid refund).</summary>
    public const string RefundState = "refundState";
    /// <summary>CRD Art. 11a(2): the withdrawal needs the buyer's name and an e-mail address for the acknowledgement.</summary>
    public const string ContactRequired = "contactRequired";
}
