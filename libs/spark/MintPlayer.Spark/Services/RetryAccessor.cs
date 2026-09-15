using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;

namespace MintPlayer.Spark.Services;

[Register(typeof(IRetryAccessor), ServiceLifetime.Scoped)]
internal sealed partial class RetryAccessor : IRetryAccessor
{
    [Inject] private readonly IClientAccessor clientAccessor;

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
        string? message = null)
    {
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
        ((ClientAccessor)clientAccessor).PushRetry(step, title, options, defaultOption, persistentObject, message);
        throw new SparkRetryActionException(step, title, options, defaultOption, persistentObject, message);
    }
}
