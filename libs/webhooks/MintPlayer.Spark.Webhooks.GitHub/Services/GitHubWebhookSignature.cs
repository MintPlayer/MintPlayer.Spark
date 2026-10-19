namespace MintPlayer.Spark.Webhooks.GitHub.Services;

/// <summary>
/// GitHub's webhook signature as an instance of the generic HMAC strategy:
/// <c>X-Hub-Signature-256: sha256={lower-case hex HMAC-SHA256 of the body}</c>, keyed with the
/// GitHub App's webhook secret.
/// </summary>
public static class GitHubWebhookSignature
{
    /// <summary>The header GitHub signs into.</summary>
    public const string HeaderName = "X-Hub-Signature-256";

    /// <summary>The scheme: HMAC-SHA256, lower-case hex, prefix <c>sha256=</c>.</summary>
    public static readonly HmacSignatureScheme Scheme = new(
        HeaderName,
        WebhookHmacAlgorithm.Sha256,
        WebhookSignatureEncoding.Hex,
        Prefix: "sha256=");

    /// <summary>The strategy for one delivery, keyed with <paramref name="secret"/>. An empty secret refuses everything.</summary>
    public static HmacSignatureStrategy Create(string? secret) => new(Scheme, secret);
}
