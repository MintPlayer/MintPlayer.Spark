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
        if (string.IsNullOrEmpty(secret))
            return true;

        if (string.IsNullOrEmpty(signature))
            return false;

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var bodyBytes = Encoding.UTF8.GetBytes(requestBody);

        var hash = HMACSHA256.HashData(keyBytes, bodyBytes);
        var hashHex = Convert.ToHexString(hash);
        var expectedHeader = $"sha256={hashHex.ToLower(System.Globalization.CultureInfo.InvariantCulture)}";
        return string.Equals(signature, expectedHeader, StringComparison.Ordinal);
    }
}
