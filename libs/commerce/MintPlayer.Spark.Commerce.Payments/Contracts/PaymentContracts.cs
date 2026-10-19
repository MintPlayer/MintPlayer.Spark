using System.Text.Json.Serialization;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Payments.Contracts;

/// <summary>One offerable payment method, as <c>GET /spark/commerce/payments/methods</c> answers it.</summary>
/// <param name="Id">The catalog id (<c>ideal</c>, <c>card</c>, …); the label is the translation key <c>commercePayments.method.{id}</c>.</param>
/// <param name="Provider">The provider.</param>
/// <param name="MaxExpiryMinutes">The provider's payment window in minutes; null when it is set by us (expires on demand).</param>
/// <param name="SupportsAuthorization">Authorize-then-capture is possible.</param>
/// <param name="SupportsSplit">The method can split a payment across sellers (D40).</param>
[SparkContract]
public sealed record SparkPaymentMethod(string Id, string Provider, int? MaxExpiryMinutes, bool SupportsAuthorization, bool SupportsSplit);

/// <summary>What <c>POST /spark/commerce/payments/connect/start</c> answers: where to send the seller.</summary>
[SparkContract]
public sealed record SparkConnectStartResponse(string RedirectUrl);

/// <summary>A seller's verification state at a provider (D21).</summary>
[SparkContract]
[JsonConverter(typeof(JsonStringEnumConverter<SparkKycStatus>))]
public enum SparkKycStatus
{
    [JsonStringEnumMemberName("notStarted")] NotStarted,
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("verified")] Verified,
    [JsonStringEnumMemberName("rejected")] Rejected,
}

/// <summary>What <c>GET /spark/commerce/payments/connect/status</c> answers for a seller.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Linked">An account is linked.</param>
/// <param name="Kyc">Its verification state.</param>
/// <param name="ChargesEnabled">It can take payments.</param>
/// <param name="PayoutsEnabled">It is paid out.</param>
/// <param name="Requirements">What the provider still needs from the seller.</param>
[SparkContract]
public sealed record SparkSellerPaymentStatus(
    string Provider,
    bool Linked,
    SparkKycStatus Kyc,
    bool ChargesEnabled,
    bool PayoutsEnabled,
    IReadOnlyList<string> Requirements);

/// <summary>The outcome of submitting a dispute's evidence (GB1).</summary>
[SparkContract]
[JsonConverter(typeof(JsonStringEnumConverter<SparkEvidenceSubmission>))]
public enum SparkEvidenceSubmission
{
    /// <summary>The provider has it.</summary>
    [JsonStringEnumMemberName("submitted")] Submitted,

    /// <summary>The provider takes evidence in its dashboard only: download the pack and upload it there.</summary>
    [JsonStringEnumMemberName("manualOnly")] ManualOnly,

    /// <summary>The dispute is closed or already under review.</summary>
    [JsonStringEnumMemberName("closed")] Closed,
}

/// <summary>What <c>POST /spark/commerce/payments/disputes/submit-evidence</c> answers.</summary>
[SparkContract]
public sealed record SparkEvidenceSubmissionResponse(SparkEvidenceSubmission Result);
