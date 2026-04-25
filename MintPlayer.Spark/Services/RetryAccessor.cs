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
