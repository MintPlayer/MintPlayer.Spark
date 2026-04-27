using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Pins the retry-action step pump: <see cref="RetryAccessor.Action"/> either throws (to
/// unwind the action and ask the user a question) or sets <see cref="RetryAccessor.Result"/>
/// from a previously-answered step and returns. The step counter must increment on every
/// call so re-runs (after the user answered step 0) advance to step 1.
/// </summary>
public class RetryAccessorTests
{
    [Fact]
    public void Action_pushes_RetryOperation_and_throws_when_no_answer_for_current_step()
    {
        var clientAccessor = new ClientAccessor();
        var retry = new RetryAccessor(clientAccessor);

        var act = () => retry.Action(
            title: "Pick one", options: ["a", "b"], defaultOption: "a",
            persistentObject: null, message: null);

        var ex = act.Should().Throw<SparkRetryActionException>().Which;
        ex.Step.Should().Be(0);
        ex.Title.Should().Be("Pick one");

        // PushRetry was called before the throw → the operation is in the envelope.
        var op = clientAccessor.Operations.OfType<RetryOperation>().Should().ContainSingle().Which;
        op.Step.Should().Be(0);
        op.Title.Should().Be("Pick one");
        op.Options.Should().Equal("a", "b");
        op.DefaultOption.Should().Be("a");
    }

    [Fact]
    public void Action_returns_without_throwing_when_step_is_already_answered()
    {
        var clientAccessor = new ClientAccessor();
        var answered = new RetryResult { Option = "yes" };

        var retry = new RetryAccessor(clientAccessor)
        {
            AnsweredResults = new Dictionary<int, RetryResult> { [0] = answered }
        };

        retry.Action("Confirm?", ["yes", "no"]);

        retry.Result.Should().BeSameAs(answered);
        // No retry operation pushed when the step is satisfied.
        clientAccessor.Operations.OfType<RetryOperation>().Should().BeEmpty();
    }

    [Fact]
    public void Action_increments_currentStep_on_each_call_so_subsequent_runs_advance()
    {
        var clientAccessor = new ClientAccessor();
        var step0Answer = new RetryResult { Option = "ok" };

        var retry = new RetryAccessor(clientAccessor)
        {
            AnsweredResults = new Dictionary<int, RetryResult> { [0] = step0Answer }
        };

        // Step 0 is answered → returns silently.
        retry.Action("Step 0", ["ok"]);
        retry.Result.Should().BeSameAs(step0Answer);

        // Step 1 has no answer → throws SparkRetryActionException with step=1 (NOT 0).
        var act = () => retry.Action("Step 1", ["a", "b"]);

        var ex = act.Should().Throw<SparkRetryActionException>().Which;
        ex.Step.Should().Be(1);
    }

    [Fact]
    public void Action_with_no_AnsweredResults_dictionary_throws_for_every_step()
    {
        var clientAccessor = new ClientAccessor();
        var retry = new RetryAccessor(clientAccessor);

        var act = () => retry.Action("Pick", ["a"]);

        act.Should().Throw<SparkRetryActionException>().Which.Step.Should().Be(0);
    }

    [Fact]
    public void Action_with_AnsweredResults_missing_current_step_throws()
    {
        var clientAccessor = new ClientAccessor();
        var retry = new RetryAccessor(clientAccessor)
        {
            // Step 5 is answered, but we're on step 0 — should still throw.
            AnsweredResults = new Dictionary<int, RetryResult> { [5] = new() { Option = "x" } }
        };

        var act = () => retry.Action("Pick", ["a"]);

        act.Should().Throw<SparkRetryActionException>().Which.Step.Should().Be(0);
    }
}
