using System.Collections.Concurrent;
using System.Security.Cryptography;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Commerce.Payments.Test;

/// <summary><c>Spark:Commerce:Payments:Test</c>.</summary>
public sealed class TestPaymentsOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Payments:Test";

    /// <summary>The test provider refuses to start in Production unless this is set (a demo deployment).</summary>
    public bool AllowInProduction { get; set; }

    /// <summary>What onboarding a seller ends in. Default <see cref="KycStatus.Verified"/>.</summary>
    public KycStatus OnboardingOutcome { get; set; } = KycStatus.Verified;

    /// <summary>Whether dispute evidence is "submitted by API" (Stripe-like) or a download (Mollie-like). Default true.</summary>
    public bool EvidenceByApi { get; set; } = true;

    /// <summary>Whether payments can be split across sellers (D40). Default true.</summary>
    public bool SupportsSplit { get; set; } = true;
}

/// <summary>What the next refund of a payment does.</summary>
public enum TestRefundBehaviour
{
    /// <summary>Refunded at once.</summary>
    Succeed,

    /// <summary>Accepted, still pending (completed later with <see cref="TestPaymentSimulator.CompleteRefundAsync"/>).</summary>
    Pending,

    /// <summary>Refused for lack of balance on the (connected) account (D20).</summary>
    InsufficientBalance,

    /// <summary>Failed for another reason.</summary>
    Fail,
}

/// <summary>A simulated buyer action at the provider.</summary>
public enum TestPaymentOutcome
{
    Pay,
    Authorize,
    Pending,
    Fail,
    Cancel,
    Expire,
}

/// <summary>How the webhook of a simulated change reaches the app.</summary>
public enum TestDelivery
{
    /// <summary>Delivered now, once.</summary>
    Immediate,

    /// <summary>Held until <see cref="TestPaymentSimulator.DeliverLateAsync"/> (a webhook that arrives after the hold died).</summary>
    Late,

    /// <summary>Delivered twice with the same event id, then once more under a new id (a provider redelivery).</summary>
    Duplicate,

    /// <summary>Not delivered at all (the app finds out by polling or the redirect).</summary>
    None,
}

internal sealed class TestPayment
{
    public required string Id { get; init; }
    public required Money Amount { get; init; }
    public required string Reference { get; init; }
    public string? Method { get; set; }
    public PaymentCaptureMode Capture { get; init; }
    public ProviderPaymentStatus Status { get; set; } = ProviderPaymentStatus.Open;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? PaidAt { get; set; }
    public required string RedirectUrl { get; init; }
    public string? AccountId { get; init; }
    public Money? ApplicationFee { get; init; }
    public string Fingerprint { get; } = "tfp_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    public TestRefundBehaviour RefundBehaviour { get; set; }
    public List<TestRefund> Refunds { get; } = [];
    public List<TestDispute> Disputes { get; } = [];
    public bool Settled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class TestRefund
{
    public required string Id { get; init; }
    public required Money Amount { get; init; }
    public string? Reference { get; init; }
    public ProviderRefundStatus Status { get; set; }
    public RefundFailureReason? Failure { get; set; }
    public bool Settled { get; set; }
}

internal sealed class TestDispute
{
    public required string Id { get; init; }
    public required Money Amount { get; init; }
    public DisputeStatus Status { get; set; } = DisputeStatus.NeedsResponse;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset DueBy { get; init; }
    public bool Reinstated { get; set; }
    public bool Settled { get; set; }
}

internal sealed class TestAccount
{
    public required string Id { get; init; }
    public required string SellerOrganizationId { get; init; }
    public KycStatus Kyc { get; set; }
}

/// <summary>The test provider's in-memory state (a singleton). Lost on restart, like a sandbox reset.</summary>
internal sealed class TestPaymentStore
{
    /// <summary>The per-process key the simulator signs its webhooks with (HMAC-SHA256), so the real webhook path verifies them.</summary>
    public string WebhookSecret { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public ConcurrentDictionary<string, TestPayment> Payments { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, string> IdempotencyKeys { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, TestAccount> Accounts { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<byte[]> LateDeliveries { get; } = new();

    public int SettlementCounter;

    public static string NewId(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
}
