using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.Commerce.Payments;

/// <summary>
/// Idempotency keys for provider calls (§4.5, research §4.3). A key is derived, never random, from the operation, our
/// reference (order, refund, dispute) and an attempt number, so the same logical call made twice — after a timeout, a
/// crash and a retry, a duplicate webhook — sends the same key and the provider answers the first result again.
/// </summary>
/// <remarks>
/// <para>
/// Stripe keeps keys for at least 24 hours (<see href="https://docs.stripe.com/api/idempotent_requests">idempotent
/// requests</see>); Mollie honours the <c>Idempotency-Key</c> header too (<see href="https://docs.mollie.com/reference/api-idempotency">API idempotency</see>,
/// keys kept for a limited period). A retry after that window is caught by the store instead: <see cref="ISparkPayments"/>
/// keeps every payment and refund under our reference and returns the recorded result before calling the provider.
/// </para>
/// <para>
/// The reference is hashed, so a key never carries an order number or e-mail into a provider's logs, and it always
/// fits Stripe's 255-character limit.
/// </para>
/// </remarks>
public static class PaymentIdempotencyKeys
{
    /// <summary>The operations a key is made for.</summary>
    public static class Operations
    {
        public const string Create = "create";
        public const string Cancel = "cancel";
        public const string Capture = "capture";
        public const string Void = "void";
        public const string Refund = "refund";
        public const string Evidence = "evidence";
        public const string Onboard = "onboard";
    }

    /// <summary><c>spark-{operation}-{sha256(reference) as 32 hex}-{attempt}</c>.</summary>
    /// <param name="operation">One of <see cref="Operations"/> (lower-case letters and <c>-</c>).</param>
    /// <param name="reference">Our reference: order, refund, dispute or account id.</param>
    /// <param name="attempt">Bumped only when a new provider object is wanted on purpose (a second payment for the same
    /// order after the first expired).</param>
    public static string For(string operation, string reference, int attempt = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        foreach (var c in operation)
        {
            if (c is not ((>= 'a' and <= 'z') or '-'))
                throw new ArgumentException("An operation is lower-case letters and '-'.", nameof(operation));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(reference));
        return $"spark-{operation}-{Convert.ToHexStringLower(hash.AsSpan(0, 16))}-{attempt.ToString(CultureInfo.InvariantCulture)}";
    }
}
