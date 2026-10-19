namespace MintPlayer.Spark.Commerce.Checkout;

/// <summary>
/// Why checkout refused something. Codes are permanent: the client translates <c>commerceCheckout.refusal.{code}</c>
/// (translations.json) and the endpoints answer them in <c>SparkCheckoutRefusal</c>.
/// </summary>
public static class CheckoutRefusals
{
    public const string BasketEmpty = "basketEmpty";
    public const string ItemNotFound = "itemNotFound";
    public const string UnknownChannel = "unknownChannel";
    public const string CurrencyMismatch = "currencyMismatch";
    public const string NegativeTotal = "negativeTotal";
    public const string InvalidQuantity = "invalidQuantity";
    /// <summary>D2/D40: the basket holds another channel and <c>Basket:MixChannels</c> is off.</summary>
    public const string ChannelMismatch = "channelMismatch";
    /// <summary>D40/D41: another seller, and mixed-seller baskets are off or the provider cannot split.</summary>
    public const string SellerMismatch = "sellerMismatch";
    /// <summary>D4: a line's channel requires sign-in.</summary>
    public const string SignInRequired = "signInRequired";
    /// <summary>§4.12: a waiting-room-gated line without a valid admission token.</summary>
    public const string AdmissionRequired = "admissionRequired";
    /// <summary>GB2: an add-on without its parent line.</summary>
    public const string ParentRequired = "parentRequired";
    public const string SoldOut = "soldOut";
    public const string UnitTaken = "unitTaken";
    public const string LimitExceeded = "limitExceeded";
    public const string NotTogether = "notTogether";
    public const string LeavesSingleSeat = "leavesSingleSeat";
    public const string HoldExpired = "holdExpired";
    public const string Conflict = "conflict";
    public const string LineNotFound = "lineNotFound";
    /// <summary>GA2: the buyer did not accept every required document in force.</summary>
    public const string TermsNotAccepted = "termsNotAccepted";
    /// <summary>GA2: a required document changed since the last step was shown.</summary>
    public const string TermsChanged = "termsChanged";
    /// <summary>GA2: a seller has no required document in force.</summary>
    public const string TermsMissing = "termsMissing";
    /// <summary>GB12: an age-restricted line without the attestation.</summary>
    public const string AgeAttestationRequired = "ageAttestationRequired";
    public const string MethodNotOffered = "methodNotOffered";
    public const string NoMethod = "noMethod";
    public const string EmailRequired = "emailRequired";
    public const string PendingOrder = "pendingOrder";
    public const string DonationInvalid = "donationInvalid";
    public const string InsuranceUnavailable = "insuranceUnavailable";
    public const string SellerCannotSell = "sellerCannotSell";
    public const string OrderNotFound = "orderNotFound";
    public const string InvalidState = "invalidState";
    public const string Forbidden = "forbidden";
}

/// <summary>A checkout refusal: a <see cref="CheckoutRefusals"/> code, an English message for logs, and the line it concerns.</summary>
public sealed class CheckoutException(string code, string message, string? lineId = null, string? subject = null) : Exception(message)
{
    public string Code { get; } = code;

    public string? LineId { get; } = lineId;

    /// <summary>What refused it (a quota, a seat, a document kind).</summary>
    public string? Subject { get; } = subject;

    public static CheckoutException Of(string code, string? lineId = null, string? subject = null)
        => new(code, $"Checkout refused: {code}{(subject is null ? "" : " (" + subject + ")")}.", lineId, subject);
}
