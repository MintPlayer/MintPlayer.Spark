using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using System.Text.Json;

namespace MintPlayer.Spark.Services;

[Register(typeof(IRetryAccessor), ServiceLifetime.Scoped)]
internal sealed partial class RetryAccessor : IRetryAccessor
{
    [Inject] private readonly IClientAccessor clientAccessor;
    // Optional so tests can build the accessor by hand; DI always supplies it (Invoke's arguments).
    [Inject] private readonly IOptions<JsonOptions>? jsonOptions = null;

    /// <summary>
    /// All answered retry results, keyed by step index.
    /// Set by the endpoint from the incoming request's retryResults array.
    /// </summary>
    internal Dictionary<int, RetryResult>? AnsweredResults { get; set; }

    /// <summary>
    /// Takes the answers carried by an incoming request, so hooks re-running on this attempt see
    /// the steps that were already answered instead of prompting again.
    /// </summary>
    /// <remarks>
    /// Every retry-capable endpoint calls this before running any hook. It used to be four lines
    /// copy-pasted per endpoint, including a downcast of the injected <see cref="IRetryAccessor"/>
    /// back to this class; that arrangement is how <c>refresh</c> ended up with the answering half
    /// of a retry and not the asking half. Null and empty are both "first attempt".
    /// </remarks>
    internal void Accept(IRetryableRequest? request)
    {
        if (request?.RetryResults is { Length: > 0 } answered)
            AnsweredResults = answered.ToDictionary(r => r.Step);
    }

    /// <summary>
    /// Tracks the current step index during action execution.
    /// Incremented each time Action() is called.
    /// </summary>
    private int currentStep;

    public RetryResult? Result { get; private set; }

    public void Action(
        string title,
        string[] options,
        string? defaultOption = null,
        PersistentObject? persistentObject = null,
        string? message = null,
        bool cancellable = false)
    {
        // Cancel is an answer the client gives in the user's language, not a label the action spells:
        // an option "Cancel" would show untranslated, and a translated one would never be recognised.
        if (options.Contains(RetryResult.CancelOption, StringComparer.Ordinal))
            throw new ArgumentException(
                $"\"{RetryResult.CancelOption}\" is not an option to offer; pass cancellable: true and the client " +
                "adds a translated Cancel that answers it.", nameof(options));

        var step = currentStep++;

        // If this step was already answered, expose the result and continue
        if (AnsweredResults?.TryGetValue(step, out var result) == true)
        {
            Result = result;
            return;
        }

        // Push the retry operation onto the client accessor so the endpoint's
        // envelope serializer picks it up alongside any non-blocking operations
        // emitted before this call. Then throw to unwind.
        ((ClientAccessor)clientAccessor).PushRetry(step, title, options, defaultOption, persistentObject, message, cancellable: cancellable);
        throw new SparkRetryActionException(step, title, options, defaultOption, persistentObject, message, cancellable: cancellable);
    }

    public async Task Invoke(string clientMethod, Func<Task<object?>> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientMethod);
        ArgumentNullException.ThrowIfNull(arguments);

        // One step counter for prompts and client methods alike, so a conversation can mix them.
        var step = currentStep++;

        if (AnsweredResults?.TryGetValue(step, out var result) == true)
        {
            Result = result;
            return;
        }

        // Only now: the factory may have side effects (a WebAuthn challenge), and an answered step
        // must not repeat them (PRD D7).
        var value = await arguments();

        // Serialized with the response's own options, so the wire spelling matches the rest of the
        // envelope and a persistent object inside the arguments still meets the boundary net (D13a)
        // here, while the request is current.
        var serialized = value is null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(value, value.GetType(), jsonOptions?.Value.SerializerOptions ?? JsonSerializerOptions.Web);

        ((ClientAccessor)clientAccessor).PushRetry(step, clientMethod, [], null, null, null, clientMethod, serialized);
        throw new SparkRetryActionException(step, clientMethod, [], null, null, null, clientMethod, serialized);
    }
}
