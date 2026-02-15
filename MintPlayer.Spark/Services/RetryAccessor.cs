using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;

namespace MintPlayer.Spark.Services;

[Register(typeof(IRetryAccessor), ServiceLifetime.Scoped)]
internal sealed partial class RetryAccessor : IRetryAccessor
{
    /// <summary>
    /// The step index of the retry that was answered by the user.
    /// Set by the endpoint from the incoming request's retryResult.step value.
    /// </summary>
    internal int? AnsweredStep { get; set; }

    /// <summary>
    /// The full result payload from the user's response.
    /// Set by the endpoint from the incoming request's retryResult value.
    /// </summary>
    internal RetryResult? AnsweredResult { get; set; }

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
        if (AnsweredStep.HasValue && step <= AnsweredStep.Value)
        {
            if (step == AnsweredStep.Value)
            {
                Result = AnsweredResult;
                return;
            }
            // step < AnsweredStep: a previously-answered step, skip it
            return;
        }

        // Auto-append "Cancel" if not present (case-insensitive)
        if (!options.Any(o => o.Equals("Cancel", StringComparison.OrdinalIgnoreCase)))
        {
            options = [.. options, "Cancel"];
        }

        // New unanswered step — throw to interrupt and prompt the user
        throw new SparkRetryActionException(step, title, options, defaultOption, persistentObject, message);
    }
}
