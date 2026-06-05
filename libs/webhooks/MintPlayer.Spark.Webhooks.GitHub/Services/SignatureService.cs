using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

[Register(typeof(ISignatureService), ServiceLifetime.Scoped)]
internal class SignatureService : ISignatureService
{
    public bool VerifySignature(string? signature, string secret, string requestBody)
    {
        // Fail-closed when no secret is configured: an empty secret cannot prove
        // anything about the body's origin, so accepting unsigned deliveries would
        // let any unauthenticated POST flow through MessageBus.BroadcastAsync.
        if (string.IsNullOrEmpty(secret))
            return false;

        if (string.IsNullOrEmpty(signature))
            return false;

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var bodyBytes = Encoding.UTF8.GetBytes(requestBody);

        var hash = HMACSHA256.HashData(keyBytes, bodyBytes);
        var hashHex = Convert.ToHexString(hash);
        var expectedHeader = $"sha256={hashHex.ToLower(System.Globalization.CultureInfo.InvariantCulture)}";

        // Constant-time comparison: string.Equals short-circuits on the first
        // mismatched byte and leaks per-byte timing, which lets a remote attacker
        // recover a valid HMAC one byte at a time over a stable network.
        var signatureBytes = Encoding.UTF8.GetBytes(signature);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedHeader);
        if (signatureBytes.Length != expectedBytes.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(signatureBytes, expectedBytes);
    }
}
