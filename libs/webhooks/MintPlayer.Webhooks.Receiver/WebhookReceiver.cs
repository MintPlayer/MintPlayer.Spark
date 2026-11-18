using System;
using System.Threading;
using System.Threading.Tasks;

namespace MintPlayer.Webhooks;

public sealed class WebhookReceiveResult
{
    private WebhookReceiveResult(int statusCode, WebhookVerificationFailure failure, string? messageId)
    {
        StatusCode = statusCode;
        Failure = failure;
        MessageId = messageId;
    }

    /// <summary>204 handled, 400 not a webhook envelope, 401 verification failed.</summary>
    public int StatusCode { get; }

    public WebhookVerificationFailure Failure { get; }

    public string? MessageId { get; }

    public bool IsSuccess => StatusCode is >= 200 and < 300;

    internal static WebhookReceiveResult Handled(string id) => new(204, WebhookVerificationFailure.None, id);
    internal static WebhookReceiveResult Unauthorized(WebhookVerificationFailure failure, string? id) => new(401, failure, id);
    internal static WebhookReceiveResult BadRequest(string? id) => new(400, WebhookVerificationFailure.None, id);
}

/// <summary>
/// The whole receive path without a web framework: verify, parse, dispatch. Hosts call it from their own endpoint
/// (MintPlayer.Webhooks.Receiver.AspNetCore does this for ASP.NET Core). An exception from the processor
/// propagates: the host answers 5xx and the producer retries.
/// </summary>
public sealed class WebhookReceiver
{
    private readonly WebhookVerifier verifier;

    public WebhookReceiver(WebhookVerifier verifier) => this.verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));

    public WebhookReceiver(WebhookVerifierOptions options) : this(new WebhookVerifier(options)) { }

    public async Task<WebhookReceiveResult> HandleAsync(Func<string, string?> getHeader, byte[] body, WebhookProcessor processor, CancellationToken cancellationToken = default)
    {
        if (processor is null) throw new ArgumentNullException(nameof(processor));

        var verification = verifier.Verify(getHeader, body);
        if (!verification.IsValid)
            return WebhookReceiveResult.Unauthorized(verification.Failure, verification.MessageId);

        var envelope = WebhookEnvelope.TryParse(body);
        if (envelope is null)
            return WebhookReceiveResult.BadRequest(verification.MessageId);

        await processor.ProcessAsync(new WebhookContext(verification.MessageId!, verification.Timestamp, envelope, getHeader), cancellationToken).ConfigureAwait(false);
        return WebhookReceiveResult.Handled(verification.MessageId!);
    }
}
