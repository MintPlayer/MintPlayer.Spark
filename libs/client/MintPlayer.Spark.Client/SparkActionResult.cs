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

    /// <summary>
    /// The request that produced this prompt, so <see cref="SparkClient.ContinueAsync"/> can send it
    /// again with one more answer attached. Null on a completed action.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The conversation lives here, on the result, and not on the client.</b> Two conversations
    /// running concurrently through one <see cref="SparkClient"/> each carry their own answers, so
    /// they cannot interleave — which they would if the client held "the current retry".
    /// </remarks>
    internal Dictionary<string, object?>? Body { get; }

    /// <summary>Answers already given in this conversation, oldest first.</summary>
    internal IReadOnlyList<object> Answers { get; }

    /// <summary>
    /// Client operations the server emitted alongside this response, in emission order — the
    /// notifications, refreshes and navigations the action asked the client to perform.
    /// </summary>
    /// <remarks>
    /// Populated on a prompt as well as on a completed action, so a hook that says something on its
    /// way to asking a question is not lost behind the question (FR11). Never null; empty when the
    /// action emitted none. The retry prompt itself is in <see cref="Retry"/> rather than here.
    /// </remarks>
    public IReadOnlyList<SparkClientOperation> Operations { get; }

    /// <summary>
    /// What the action returned through <c>CustomActionArgs.SetResult</c> (#460, T5): the response
    /// envelope's <c>result</c>, as raw JSON. Null when the action set nothing, and always null on a
    /// prompt — a 449 carries no result; the attempt that completes does.
    /// </summary>
    /// <remarks>Read it typed with <see cref="GetResult{T}"/>.</remarks>
    public System.Text.Json.JsonElement? Result { get; }

    /// <summary>
    /// <see cref="Result"/> deserialized as <typeparamref name="T"/> with the client's web defaults
    /// (camelCase, case-insensitive); <c>default</c> when the action returned nothing.
    /// </summary>
    public T? GetResult<T>()
        => Result is { } json ? System.Text.Json.JsonSerializer.Deserialize<T>(json, SparkClient.JsonOptions) : default;

    private SparkActionResult(
        int statusCode, RetryActionPayload? retry, Dictionary<string, object?>? body,
        IReadOnlyList<object>? answers, IReadOnlyList<SparkClientOperation>? operations,
        System.Text.Json.JsonElement? result = null)
    {
        StatusCode = statusCode;
        Retry = retry;
        Body = body;
        Answers = answers ?? [];
        Operations = operations ?? [];
        Result = result;
    }

    internal static SparkActionResult ForSuccess(
        int statusCode, IReadOnlyList<SparkClientOperation>? operations = null, System.Text.Json.JsonElement? result = null)
        => new(statusCode, retry: null, body: null, answers: null, operations, result);

    internal static SparkActionResult ForRetry(
        RetryActionPayload payload, Dictionary<string, object?> body, IReadOnlyList<object> answers,
        IReadOnlyList<SparkClientOperation>? operations = null)
        => new(449, payload, body, answers, operations);
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
