using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Client;

/// <summary>
/// Outcome of <see cref="SparkClient.ExecuteActionAsync"/>. Preserves the server's three
/// possible in-protocol response shapes without collapsing them:
/// <list type="bullet">
///   <item><description>Success — action completed on the server. <see cref="IsRetry"/> is <c>false</c>.</description></item>
///   <item><description>Retry — the server's custom action threw <c>SparkRetryActionException</c>
///     (HTTP 449), meaning it needs the caller to answer a question before proceeding.
///     <see cref="IsRetry"/> is <c>true</c> and <see cref="Retry"/> carries the prompt.</description></item>
/// </list>
/// Failure statuses (401/403/404/500) are thrown as <see cref="SparkClientException"/>.
/// </summary>
public sealed class SparkActionResult
{
    public int StatusCode { get; }
    public RetryActionPayload? Retry { get; }

    public bool IsRetry => Retry is not null;

    private SparkActionResult(int statusCode, RetryActionPayload? retry)
    {
        StatusCode = statusCode;
        Retry = retry;
    }

    internal static SparkActionResult ForSuccess(int statusCode)
        => new(statusCode, retry: null);

    internal static SparkActionResult ForRetry(RetryActionPayload payload)
        => new(449, payload);
}

/// <summary>
/// Mirrors the JSON body of a 449 Retry-With response from the custom-action endpoint.
/// Properties map directly to the server's <c>SparkRetryActionException</c>:
/// <c>step</c>, <c>title</c>, <c>message</c>, <c>options</c>, <c>defaultOption</c>,
/// <c>persistentObject</c>.
/// </summary>
public sealed class RetryActionPayload
{
    public int Step { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Message { get; init; }
    public string[] Options { get; init; } = [];
    public string? DefaultOption { get; init; }
    public PersistentObject? PersistentObject { get; init; }
}
