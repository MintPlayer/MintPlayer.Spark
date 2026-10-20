using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using System.Text;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

/// <summary>
/// The <c>X-Hub-Signature-256</c> check for callers that hold the header and the body separately:
/// the webhook processor's second check, which the dev-tunnel clients rely on. It is
/// <see cref="GitHubWebhookSignature"/>, the same HMAC strategy the webhook route verifies with.
/// </summary>
[Register(typeof(ISignatureService), ServiceLifetime.Scoped)]
internal class SignatureService : ISignatureService
{
    // Fail-closed on an empty secret, and constant-time: both are properties of the strategy.
    public bool VerifySignature(string? signature, string secret, string requestBody)
        => GitHubWebhookSignature.Create(secret)
            .VerifyHeader(signature, Encoding.UTF8.GetBytes(requestBody))
            .IsAccepted;
}
