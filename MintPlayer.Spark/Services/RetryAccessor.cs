using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;

namespace MintPlayer.Spark.Services;

[Register(typeof(IRetryAccessor), ServiceLifetime.Scoped)]
internal sealed partial class RetryAccessor : IRetryAccessor
{
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

    public RetryResult? Result { get; internal set; }

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

        // New unanswered step — throw to interrupt and prompt the user
        throw new SparkRetryActionException(step, title, options, defaultOption, persistentObject, message);
    }
}
